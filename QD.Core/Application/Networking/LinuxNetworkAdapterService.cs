using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QD.Core.Helper;

namespace QD.Core.Application.Networking;

/// <summary>
/// Linux implementation. Adapter enumeration uses <see cref="System.Net.NetworkInformation"/>.
/// DNS changes use the first available backend:
/// 1. NetworkManager (nmcli) — persistent across reboots,
/// 2. systemd-resolved (resolvectl) — runtime only,
/// 3. direct /etc/resolv.conf rewrite — fallback, requires root.
/// Backends are detected lazily on first use and cached.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxNetworkAdapterService : INetworkAdapterService
{
    private enum Backend
    {
        NetworkManager,
        SystemdResolved,
        ResolvConf,
    }

    private Backend? _backend;

    public string Platform => "Linux";

    public string BackendDescription => DetectBackend() switch
    {
        Backend.NetworkManager => "NetworkManager (nmcli, persistent)",
        Backend.SystemdResolved => "systemd-resolved (resolvectl, non-persistent)",
        _ => "/etc/resolv.conf (direct write, requires root)",
    };

    public bool IsElevated => GetEuid() == 0;

    [DllImport("libc", SetLastError = false, EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    public IReadOnlyList<AdapterInfo> GetAdapters() => AdapterCatalog.FromNetworkInterfaces();

    public AdapterInfo? ResolveTarget(string? preferredAdapterId) =>
        AdapterCatalog.ResolveTarget(GetAdapters(), preferredAdapterId);

    public OperationResult ApplyDns(AdapterInfo adapter, string primaryDns, string? secondaryDns)
    {
        var servers = secondaryDns is null ? [primaryDns] : new[] { primaryDns, secondaryDns };
        return DetectBackend() switch
        {
            Backend.NetworkManager => ApplyWithNetworkManager(adapter, servers),
            Backend.SystemdResolved => ApplyWithResolvectl(adapter, servers),
            _ => ApplyWithResolvConf(servers),
        };
    }

    public OperationResult ClearDns(AdapterInfo adapter) => DetectBackend() switch
    {
        Backend.NetworkManager => ClearWithNetworkManager(adapter),
        Backend.SystemdResolved => RunAndMap("resolvectl", ["revert", adapter.Name],
            successMessage: $"{ApplicationMessages.DnsCleared} ({BackendDescription})"),
        _ => ApplyWithResolvConf([]),
    };

    private Backend DetectBackend()
    {
        if (_backend.HasValue)
            return _backend.Value;

        if (Which("nmcli") is not null)
            _backend = Backend.NetworkManager;
        else if (Which("resolvectl") is not null)
            _backend = Backend.SystemdResolved;
        else
            _backend = Backend.ResolvConf;

        return _backend.Value;
    }

    // ---- NetworkManager -------------------------------------------------

    private OperationResult ApplyWithNetworkManager(AdapterInfo adapter, string[] servers)
    {
        var connection = GetNetworkManagerConnection(adapter.Name);
        if (string.IsNullOrEmpty(connection))
            return new OperationResult().Failed(ApplicationMessages.LinuxDeviceUnmanaged);

        var v4 = servers.Where(s => !s.Contains(':')).ToArray();
        var v6 = servers.Where(s => s.Contains(':')).ToArray();

        var args = new List<string> { "-t", "connection", "modify", connection };
        if (v4.Length > 0)
            args.AddRange(["ipv4.dns", string.Join(' ', v4), "ipv4.ignore-auto-dns", "yes"]);
        if (v6.Length > 0)
            args.AddRange(["ipv6.dns", string.Join(' ', v6), "ipv6.ignore-auto-dns", "yes"]);

        var modify = RunAndMap("nmcli", [.. args], successMessage: null);
        if (!modify.IsSucceeded)
            return modify;

        return ReapplyNetworkManager(adapter, ApplicationMessages.DnsChanged);
    }

    private OperationResult ClearWithNetworkManager(AdapterInfo adapter)
    {
        var connection = GetNetworkManagerConnection(adapter.Name);
        if (string.IsNullOrEmpty(connection))
            return new OperationResult().Failed(ApplicationMessages.LinuxDeviceUnmanaged);

        var modify = RunAndMap("nmcli",
        [
            "-t", "connection", "modify", connection,
            "ipv4.dns", "", "ipv4.ignore-auto-dns", "no",
            "ipv6.dns", "", "ipv6.ignore-auto-dns", "no",
        ], successMessage: null);
        if (!modify.IsSucceeded)
            return modify;

        return ReapplyNetworkManager(adapter, ApplicationMessages.DnsCleared);
    }

    private OperationResult ReapplyNetworkManager(AdapterInfo adapter, string successMessage)
    {
        var reapply = RunAndMap("nmcli", ["device", "reapply", adapter.Name], successMessage: null);
        if (reapply.IsSucceeded)
            return new OperationResult().Succeeded(successMessage);

        // The profile was saved, but the device is not active right now — the DNS
        // takes effect as soon as the connection comes up. That is not a failure.
        return new OperationResult().Succeeded(
            $"{successMessage} (saved on the connection; becomes active when the connection comes up)");
    }

    private string? GetNetworkManagerConnection(string deviceName)
    {
        var result = Capture("nmcli", ["-g", "GENERAL.CONNECTION", "device", "show", deviceName]);
        if (result.ExitCode != 0)
            return null;

        var name = result.StandardOutput.Trim().Trim('"');
        return string.IsNullOrEmpty(name) || name == "--" ? null : name;
    }

    // ---- systemd-resolved ------------------------------------------------

    private OperationResult ApplyWithResolvectl(AdapterInfo adapter, string[] servers)
    {
        var args = new List<string> { "dns", adapter.Name };
        args.AddRange(servers);

        return RunAndMap("resolvectl", [.. args],
            successMessage: $"{ApplicationMessages.DnsChanged} ({BackendDescription})");
    }

    // ---- /etc/resolv.conf fallback ---------------------------------------

    private OperationResult ApplyWithResolvConf(string[] servers)
    {
        const string path = "/etc/resolv.conf";

        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.LinkTarget is not null)
                return new OperationResult().Failed(ApplicationMessages.ResolvConfIsSymlink);

            var lines = file.Exists ? File.ReadAllLines(path).ToList() : [];
            lines.RemoveAll(l => l.TrimStart().StartsWith("nameserver", StringComparison.OrdinalIgnoreCase));

            // Mirror primary/secondary semantics: at most two nameservers.
            foreach (var server in servers.Take(2))
                lines.Insert(0, $"nameserver {server}");

            File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            return new OperationResult().Succeeded($"{ApplicationMessages.DnsChanged} ({BackendDescription})");
        }
        catch (UnauthorizedAccessException)
        {
            return new OperationResult().Failed(ApplicationMessages.NotElevated);
        }
        catch (Exception ex)
        {
            return new OperationResult().Failed($"{ApplicationMessages.DnsChangeFailed}: {ex.Message}");
        }
    }

    // ---- process helpers --------------------------------------------------

    private OperationResult RunAndMap(string fileName, string[] args, string? successMessage)
    {
        var result = Capture(fileName, args);
        if (result.ExitCode == 0)
            return new OperationResult().Succeeded(successMessage ?? ApplicationMessages.DnsChanged);

        var error = result.StandardError.Trim();
        if (error.Length == 0)
            error = result.StandardOutput.Trim();

        return new OperationResult().Failed(
            $"{ApplicationMessages.DnsChangeFailed} ({Path.GetFileName(fileName)}): {error}");
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Capture(
        string fileName, string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, string.Empty, "timed out after 15s");
        }

        // Drain remaining output of the already-exited process.
        process.WaitForExit();

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static string? Which(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, executable);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Malformed PATH entries are ignored.
            }
        }

        return null;
    }
}
