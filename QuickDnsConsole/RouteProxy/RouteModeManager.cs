using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using QD.Core.Application.Networking;
using QD.Core.Application.Routing;
using QD.Core.Cotracts.DnsObjContracts;
using QD.Core.Domain.DnsRoute;
using QD.Core.Helper;
using QD.Core.Infrastructure.SQLite;

namespace QuickDNSConsole.RouteProxy;

/// <summary>Heartbeat record: the host process id plus its start time.</summary>
public sealed record RouteState(int Pid, DateTime StartedUtc);

/// <summary>
/// Manages route mode end to end: build the rule snapshot, spawn the detached host
/// process (no external service/installer — QuickDNS spawns itself), point every
/// usable adapter's DNS at the loopback proxy, and restore automatic DNS on stop.
///
/// Safety rails:
/// - activation requires elevation (the DNS change itself does anyway) — checked before
///   anything is spawned, so a failed start can never leave an orphaned proxy;
/// - the host refreshes a heartbeat every 5 s; <see cref="IsProxyAlive"/> detects a dead
///   host, and <see cref="AutoHealOrphanedActivation"/> restores automatic DNS when the
///   adapters still point at a proxy that is gone;
/// - port 53 is probed before spawn (Cisco Umbrella / Acrylic / Technitium may own it).
/// </summary>
public sealed class RouteModeManager(
    IDnsRouteRepository routes,
    IDnsObjApplication dnsApp,
    INetworkAdapterService adapters)
{
    private const string DefaultBindIp = "127.0.0.2";

    private readonly IDnsRouteRepository _routes = routes;
    private readonly IDnsObjApplication _dnsApp = dnsApp;
    private readonly INetworkAdapterService _adapters = adapters;

    public static bool HasStateFile => File.Exists(AppPaths.RouteStateFile);

    /// <summary>True when the host process is alive and its heartbeat is fresh (≤ 15 s old).</summary>
    public bool IsProxyAlive()
    {
        if (!HasStateFile)
            return false;

        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(AppPaths.RouteStateFile);
            if (age > TimeSpan.FromSeconds(15))
                return false;

            var state = JsonSerializer.Deserialize(
                File.ReadAllText(AppPaths.RouteStateFile),
                RouteSnapshotJsonContext.Default.RouteState);
            if (state is null)
                return false;

            using var process = Process.GetProcessById(state.Pid);
            return !process.HasExited;
        }
        catch
        {
            return false; // missing/corrupt state or dead pid
        }
    }

    /// <summary>Builds the snapshot, spawns the detached host, waits for readiness, points the adapters at the proxy.</summary>
    public OperationResult Start()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return new OperationResult().Failed("Route mode supports Windows and Linux only.");

        if (!_adapters.IsElevated)
            return new OperationResult().Failed(ApplicationMessages.NotElevated);

        if (IsProxyAlive())
            return new OperationResult().Failed($"Route mode is already running (pid {ReadPid()}). Stop it with --route-stop first.");

        var rules = _routes.GetAll().Where(r => r.Enabled).ToList();
        if (rules.Count == 0)
            return new OperationResult().Failed("No route rules yet. Add at least one rule (and a '*' fallback) first — press P in the dashboard.");

        var presets = _dnsApp.GetAll().ToDictionary(p => p.Id);
        var snapshot = new RouteSnapshot();
        foreach (var rule in rules)
        {
            if (!presets.TryGetValue(rule.PresetId, out var preset))
                continue; // cascade should prevent this; skip defensively

            snapshot.Rules.Add(new RouteServerSnapshot
            {
                Pattern = rule.Pattern,
                Primary = preset.FirstDns,
                Secondary = string.IsNullOrWhiteSpace(preset.SecondDns) ? null : preset.SecondDns,
            });
        }

        if (snapshot.Rules.Count == 0)
            return new OperationResult().Failed("None of the enabled rules maps to an existing preset.");

        if (!snapshot.Rules.Any(r => r.Pattern == "*"))
            return new OperationResult().Failed(
                "No '*' fallback rule — queries that match no rule would fail. Add a rule with pattern '*' (any preset) and try again.");

        var bindIp = PickLoopbackIp();
        if (bindIp is null)
            return new OperationResult().Failed(
                "No free loopback address (tried 127.0.0.2–127.0.0.9): another local DNS proxy owns them all. Stop it first, then try again.");

        snapshot.BindIp = bindIp;
        RouteSnapshot.Save(AppPaths.RouteActiveFile, snapshot);

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return new OperationResult().Failed("Could not locate the running QuickDNS executable.");

        Process host;
        try
        {
            host = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "--route-host" },
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            return new OperationResult().Failed($"Could not start the route host process: {ex.Message}");
        }

        if (!WaitForProxyReady(bindIp, TimeSpan.FromSeconds(6)))
        {
            TryKill(host);
            return new OperationResult().Failed(
                "The route host did not become ready within 6 s — see routes.log in the config directory. A common cause is another DNS server already bound to the loopback address.");
        }

        // SAFETY GATE: prove a real DNS round trip through the bind address BEFORE pointing
        // any adapter at it. DNS filters (Shecan/NextDNS helpers, YogaDNS, AdGuard drivers…)
        // intercept loopback port-53 traffic and would swallow every answer, leaving DNS
        // completely broken while route mode looks "active". Fail loudly instead.
        if (!ProbeServer(bindIp))
        {
            TryKill(host);
            return new OperationResult().Failed(
                "The route host is running but DNS round trips to it are being swallowed — a DNS filter " +
                "(Shecan/NextDNS helper, YogaDNS, AdGuard driver…) is likely intercepting loopback port 53. " +
                "Route mode was NOT activated and your adapters are unchanged; stop that tool and try again.");
        }

        // Point every usable adapter at the loopback proxy. Windows queries all adapters'
        // DNS servers in parallel, so a single-adapter activation would leave routing
        // inconsistent — Cisco Umbrella/YogaDNS do the same (all interfaces).
        var failures = new List<string>();
        foreach (var adapter in _adapters.GetAdapters())
        {
            var applied = SafeApply(() => _adapters.ApplyDns(adapter, bindIp, null));
            if (!applied.IsSucceeded)
                failures.Add($"{adapter.Name}: {applied.Message}");
        }

        if (failures.Count > 0)
        {
            // Roll back whatever succeeded so the machine is never left half-activated.
            foreach (var adapter in _adapters.GetAdapters())
                SafeApply(() => _adapters.ClearDns(adapter));
            TryKill(host);
            return new OperationResult().Failed(
                $"Route host is running but the adapters could not be switched to {bindIp} — rolled back. {string.Join("; ", failures)}");
        }

        return new OperationResult().Succeeded(
            $"Route mode active: {snapshot.Rules.Count} rule(s) (fallback '*'), " +
            $"system DNS → {bindIp} on all usable adapters, host pid {host.Id}. " +
            "Stop with --route-stop (restores automatic DNS).");
    }

    /// <summary>Restores automatic DNS on every adapter, then stops the host process.</summary>
    public OperationResult Stop()
    {
        if (!HasStateFile && !IsProxyAlive())
            return new OperationResult().Failed("Route mode is not running.");

        if (!_adapters.IsElevated)
            return new OperationResult().Failed(ApplicationMessages.NotElevated);

        // Restore DNS first: even if the kill fails, resolution keeps working.
        var failures = new List<string>();
        foreach (var adapter in _adapters.GetAdapters())
        {
            var cleared = SafeApply(() => _adapters.ClearDns(adapter));
            if (!cleared.IsSucceeded)
                failures.Add($"{adapter.Name}: {cleared.Message}");
        }

        var pid = ReadPid();
        if (pid is not null)
            TryKillById(pid.Value);

        CleanupFiles();

        return failures.Count > 0
            ? new OperationResult().Succeeded($"Route mode stopped; DNS restored with warnings: {string.Join("; ", failures)}")
            : new OperationResult().Succeeded("Route mode stopped — adapters back to automatic (DHCP) DNS.");
    }

    /// <summary>Multi-line human-readable status used by --route-status and the TUI.</summary>
    public string DescribeStatus()
    {
        var lines = new List<string>();
        var alive = IsProxyAlive();
        var ruleCount = _routes.Count();
        var bindIp = CurrentBindIp();

        if (alive)
            lines.Add($"Route mode: RUNNING (pid {ReadPid()}, {ruleCount} rule(s) in the snapshot, proxy on {bindIp}:53)");
        else if (HasStateFile)
            lines.Add("Route mode: STOPPED (a previous host is not alive; heartbeat stale)");
        else
            lines.Add($"Route mode: STOPPED ({ruleCount} rule(s) stored)");

        if (_adapters.IsElevated)
        {
            var loopbackAdapters = _adapters.GetAdapters().Count(a => a.DnsServers.Any(d =>
                d.Equals(bindIp, StringComparison.OrdinalIgnoreCase)));

            if (alive && loopbackAdapters == 0)
                lines.Add($"Warning: the proxy runs but no adapter points at {bindIp} — run --route-stop, then --route-start.");
            if (!alive && loopbackAdapters > 0 && HasStateFile)
                lines.Add($"Warning: adapters still point at {bindIp} but the proxy is dead — DNS is BROKEN until --route-stop restores it.");
            lines.Add($"Adapters pointing at the proxy: {loopbackAdapters}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Called at startup: when a previous activation was orphaned (adapters still point at
    /// 127.0.0.1 while the host is dead) this restores automatic DNS and returns a warning;
    /// otherwise null. Gated on our own state file so third-party loopback DNS tools
    /// (AdGuard, Umbrella…) are never touched.
    /// </summary>
    public string? AutoHealOrphanedActivation()
    {
        if (!HasStateFile || IsProxyAlive() || !_adapters.IsElevated)
            return null;

        var bindIp = CurrentBindIp();
        var orphaned = _adapters.GetAdapters().Any(a => a.DnsServers.Any(d =>
            d.Equals(bindIp, StringComparison.OrdinalIgnoreCase)));
        if (!orphaned)
            return null;

        foreach (var adapter in _adapters.GetAdapters())
            SafeApply(() => _adapters.ClearDns(adapter));

        CleanupFiles();
        return "The route-mode host was no longer running while adapters still pointed at 127.0.0.1 — DNS was restored to automatic.";
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// Finds a free loopback address for the proxy. 127.0.0.0/8 is all loopback, so the
    /// default 127.0.0.2 coexists with tools that own 127.0.0.1:53 (the adapter DNS is
    /// then set to the chosen address — DNS always speaks on port 53).
    /// </summary>
    /// <summary>Real DNS round trip through the bind address (2 attempts, 2 s each).</summary>
    private static bool ProbeServer(string bindIp)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.ReceiveTimeout = 2000;
                udp.Connect(new IPEndPoint(IPAddress.Parse(bindIp), 53));

                var query = DnsWire.BuildQuery("quickdns.probe.test", (ushort)Random.Shared.Next(1, ushort.MaxValue), DnsWire.QtypeA);
                udp.Send(query);

                var buffer = new byte[65527];
                var received = udp.Receive(buffer);
                if (received > 0)
                    return true;
            }
            catch
            {
                // Fall through to the next attempt.
            }
        }

        return false;
    }

    private static string? PickLoopbackIp()
    {
        for (var octet = 2; octet <= 9; octet++)
        {
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Bind(new IPEndPoint(IPAddress.Parse($"127.0.0.{octet}"), 53));
                return $"127.0.0.{octet}";
            }
            catch (SocketException)
            {
                // Taken by another local DNS tool; try the next loopback address.
            }
        }

        return null;
    }

    /// <summary>The bind IP of the running/last activation snapshot, or the default.</summary>
    private static string CurrentBindIp()
    {
        try
        {
            if (File.Exists(AppPaths.RouteActiveFile))
                return RouteSnapshot.Load(AppPaths.RouteActiveFile).BindIp;
        }
        catch
        {
            // Fall through to the default.
        }

        return DefaultBindIp;
    }

    private static OperationResult SafeApply(Func<OperationResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            return new OperationResult().Failed(ex.Message);
        }
    }



    private static bool WaitForProxyReady(string bindIp, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var connect = probe.BeginConnect(new IPEndPoint(IPAddress.Parse(bindIp), 53), null, null);
                if (connect.AsyncWaitHandle.WaitOne(250) && probe.Connected)
                    return true;
            }
            catch
            {
                // Not ready yet; retry until the deadline.
            }

            Thread.Sleep(150);
        }

        return false;
    }

    private int? ReadPid()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.RouteStateFile), RouteSnapshotJsonContext.Default.RouteState)?.Pid;
        }
        catch
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static void TryKillById(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
    }

    private static void CleanupFiles()
    {
        try
        {
            if (File.Exists(AppPaths.RouteStateFile))
                File.Delete(AppPaths.RouteStateFile);
        }
        catch { /* best effort */ }
    }
}
