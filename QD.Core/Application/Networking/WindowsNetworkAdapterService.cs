using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using QD.Core.Helper;

namespace QD.Core.Application.Networking;

/// <summary>
/// Windows implementation. Adapter enumeration uses <see cref="System.Net.NetworkInformation"/>
/// (fast, in-process); DNS changes go through the documented <c>netsh interface ip set dns</c>
/// command, run as a child process.
///
/// Why not WMI (Win32_NetworkAdapterConfiguration)? System.Management is built-in COM interop,
/// which NativeAOT does not support (IL3052) and IL trimming breaks — WMI calls crashed with
/// InvalidProgramException inside System.Management in published builds (dotnet/runtime#61960).
/// netsh ships with every Windows install, is trimming/AOT-safe and needs no extra package.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkAdapterService : INetworkAdapterService
{
    public string Platform => "Windows";

    public string BackendDescription => "netsh (interface ip set dns)";

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public IReadOnlyList<AdapterInfo> GetAdapters() => AdapterCatalog.FromNetworkInterfaces();

    public AdapterInfo? ResolveTarget(string? preferredAdapterId) =>
        AdapterCatalog.ResolveTarget(GetAdapters(), preferredAdapterId);

    public OperationResult ApplyDns(AdapterInfo adapter, string primaryDns, string? secondaryDns)
    {
        if (!IsElevated)
            return new OperationResult().Failed(ApplicationMessages.NotElevated);

        string[] servers = secondaryDns is null ? [primaryDns] : [primaryDns, secondaryDns];

        // netsh is family-scoped (ipv4/ipv6 contexts), so group the requested servers.
        // WMI replaced the adapter's whole search order in one call; to keep the same
        // semantics each family not present in the request is reset back to automatic.
        var v4 = servers.Where(IsIpv4).ToArray();
        var v6 = servers.Where(IsIpv6).ToArray();

        if (v4.Length == 0 && v6.Length == 0)
            return new OperationResult().Failed(ApplicationMessages.InvalidIpAddress);

        var notes = new List<string>();

        var v4Result = v4.Length > 0
            ? ApplyFamily(adapter, "ipv4", v4)
            : BestEffortClearFamily(adapter, "ipv4", notes);
        if (v4Result is { } fail)
            return fail;

        var v6Result = v6.Length > 0
            ? ApplyFamily(adapter, "ipv6", v6)
            : BestEffortClearFamily(adapter, "ipv6", notes);
        if (v6Result is { } fail6)
            return fail6;

        var message = BuildSuccessMessage(ApplicationMessages.DnsChanged, notes);
        return new OperationResult().Succeeded(message);
    }

    /// <summary>Appends the (best-effort) DNS cache flush result to a success message.</summary>
    private static string BuildSuccessMessage(string baseMessage, List<string> notes)
    {
        var parts = new List<string>();
        if (FlushDnsCache())
            parts.Add("DNS cache flushed");
        else
            parts.Add("DNS cache could not be flushed (ipconfig /flushdns failed)");
        parts.AddRange(notes);

        return $"{baseMessage} ({string.Join("; ", parts)})";
    }

    /// <summary>
    /// Flushes the Windows DNS resolver cache so lookups immediately use the new servers
    /// instead of previously cached answers. Best effort: a failed flush never fails the
    /// change itself (cached entries expire on their own TTLs).
    /// </summary>
    private static bool FlushDnsCache() => RunTool("ipconfig.exe", "/flushdns").ExitCode == 0;

    /// <summary>Resets a family the request did not cover; a failure is a note, not an error.</summary>
    private OperationResult? BestEffortClearFamily(AdapterInfo target, string family, List<string> noteList)
    {
        var result = ClearFamily(target, family);
        if (result is not null)
            noteList.Add($"{family} DNS could not be reset: {result.Message}");
        return null;
    }

    public OperationResult ClearDns(AdapterInfo adapter)
    {
        if (!IsElevated)
            return new OperationResult().Failed(ApplicationMessages.NotElevated);

        var v4 = ClearFamily(adapter, "ipv4");
        if (v4 is { } v4Fail)
            return v4Fail;

        var v6 = ClearFamily(adapter, "ipv6");
        if (v6 is { } v6Fail)
            return v6Fail;

        return new OperationResult().Succeeded(
            BuildSuccessMessage(ApplicationMessages.DnsCleared, []));
    }

    /// <summary>Sets a static DNS list for one address family. Returns null on success.</summary>
    private OperationResult? ApplyFamily(AdapterInfo adapter, string family, string[] servers)
    {
        // First server replaces the family's whole list; the rest are appended in order.
        var set = RunNetsh(
            "interface", family, "set", "dns",
            $"name={adapter.Name}",
            "source=static",
            $"addr={servers[0]}",
            "validate=no");
        if (set.ExitCode != 0)
            return MapNetshFailure(family, set);

        for (var i = 1; i < servers.Length; i++)
        {
            var add = RunNetsh(
                "interface", family, "add", "dns",
                $"name={adapter.Name}",
                $"addr={servers[i]}",
                $"index={i + 1}",
                "validate=no");
            if (add.ExitCode != 0)
                return MapNetshFailure(family, add);
        }

        return null;
    }

    /// <summary>
    /// Resets one family back to automatic (DHCP-provided) DNS. Returns null on success.
    /// <c>set dns source=dhcp</c> is the canonical revert; <c>delete dns addr=all</c> covers
    /// adapters where DHCP cannot own DNS (statically configured interfaces).
    /// </summary>
    private OperationResult? ClearFamily(AdapterInfo adapter, string family)
    {
        var reset = RunNetsh(
            "interface", family, "set", "dns",
            $"name={adapter.Name}",
            "source=dhcp");
        if (reset.ExitCode == 0)
            return null;

        var delete = RunNetsh(
            "interface", family, "delete", "dns",
            $"name={adapter.Name}",
            "addr=all");
        if (delete.ExitCode == 0)
            return null;

        return MapNetshFailure(family, delete);
    }

    private static OperationResult MapNetshFailure(string family, (int ExitCode, string Output) result) =>
        new OperationResult().Failed($"{ApplicationMessages.DnsChangeFailed} ({family}): {FailureOutput(result)}");

    private static string FailureOutput((int ExitCode, string Output) result)
    {
        var text = result.Output.Trim();
        return text.Length > 0 ? text : $"netsh exit code {result.ExitCode}";
    }

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Process.Start with ArgumentList needs no reflection or COM.")]
    private static (int ExitCode, string Output) RunTool(string fileName, params string[] args)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    // Resolve from System32 explicitly: PATH-dependent resolution in an
                    // elevated process would let a rogue PATH entry run as admin.
                    FileName = Path.Combine(Environment.SystemDirectory, fileName),
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
                return (-1, $"{fileName} timed out after 15s");
            }

            // Drain remaining output of the already-exited process.
            process.WaitForExit();

            var output = stdoutTask.GetAwaiter().GetResult();
            var error = stderrTask.GetAwaiter().GetResult();
            var combined = string.Join(Environment.NewLine, new[] { error, output }.Where(s => s.Trim().Length > 0));
            return (process.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static (int ExitCode, string Output) RunNetsh(params string[] args) => RunTool("netsh.exe", args);

    private static bool IsIpv4(string server) =>
        IPAddress.TryParse(server.Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    private static bool IsIpv6(string server) =>
        IPAddress.TryParse(server.Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
}
