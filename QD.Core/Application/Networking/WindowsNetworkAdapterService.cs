using System.Diagnostics.CodeAnalysis;
using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using QD.Core.Helper;

namespace QD.Core.Application.Networking;

/// <summary>
/// Windows implementation. Adapter enumeration uses <see cref="System.Net.NetworkInformation"/>
/// (fast, in-process); DNS changes go through WMI <c>Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder</c>,
/// resolved with a scoped query (InterfaceIndex) so no full WMI enumeration ever happens.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkAdapterService : INetworkAdapterService
{
    public string Platform => "Windows";

    public string BackendDescription => "WMI (Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder)";

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

    public OperationResult ApplyDns(AdapterInfo adapter, string primaryDns, string? secondaryDns) =>
        ApplyWithWmi(adapter, secondaryDns is null ? [primaryDns] : [primaryDns, secondaryDns]);

    public OperationResult ClearDns(AdapterInfo adapter) =>
        // Passing null to SetDNSServerSearchOrder reverts the adapter to DHCP-provided DNS.
        ApplyWithWmi(adapter, null);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "System.Management talks to WMI over COM interop; no managed reflection over trimmed app types.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Same as IL2026.")]
    private OperationResult ApplyWithWmi(AdapterInfo adapter, string[]? serverOrder)
    {
        if (!IsElevated)
            return new OperationResult().Failed(ApplicationMessages.NotElevated);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                scope: "root\\cimv2",
                queryString: BuildAdapterQuery(adapter));

            foreach (var instance in searcher.Get().Cast<ManagementObject>())
            {
                using (instance)
                {
                    using var inParams = instance.GetMethodParameters("SetDNSServerSearchOrder");
                    if (inParams is null)
                        continue;

                    inParams["DNSServerSearchOrder"] = serverOrder;

                    using var outParams = instance.InvokeMethod("SetDNSServerSearchOrder", inParams, null);
                    if (outParams is null)
                        return new OperationResult().Failed(ApplicationMessages.DnsChangeFailed);

                    var returnCode = Convert.ToUInt32(outParams["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
                    return returnCode == 0
                        ? new OperationResult().Succeeded(ApplicationMessages.DnsChanged)
                        : new OperationResult().Failed($"{ApplicationMessages.DnsChangeFailed} (WMI error code {returnCode})");
                }
            }

            return new OperationResult().Failed(ApplicationMessages.AdapterNotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return new OperationResult().Failed(ApplicationMessages.NotElevated);
        }
        catch (ManagementException ex)
        {
            return new OperationResult().Failed($"{ApplicationMessages.DnsChangeFailed} (WMI: {ex.Message})");
        }
        catch (Exception ex)
        {
            return new OperationResult().Failed($"{ApplicationMessages.DnsChangeFailed}: {ex.Message}");
        }
    }

    private static string BuildAdapterQuery(AdapterInfo adapter)
    {
        const string baseQuery = "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE AND ";

        if (adapter.InterfaceIndex is int index)
            return $"{baseQuery}InterfaceIndex = {index}";

        var escaped = adapter.Description.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"{baseQuery}Description = \"{escaped}\"";
    }
}
