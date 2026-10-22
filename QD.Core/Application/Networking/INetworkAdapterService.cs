using QD.Core.Helper;

namespace QD.Core.Application.Networking;

/// <summary>
/// Platform facade for enumerating network adapters and applying/clearing DNS on a chosen one.
/// Implementations: <see cref="WindowsNetworkAdapterService"/> (WMI) and
/// <see cref="LinuxNetworkAdapterService"/> (nmcli / resolvectl / resolv.conf).
/// </summary>
public interface INetworkAdapterService
{
    /// <summary>Friendly platform name ("Windows", "Linux").</summary>
    string Platform { get; }

    /// <summary>Short description of the mechanism used to change DNS (for diagnostics).</summary>
    string BackendDescription { get; }

    /// <summary>True when the current process may change system DNS (administrator on Windows, root on Linux).</summary>
    bool IsElevated { get; }

    /// <summary>All usable adapters (Ethernet / Wi-Fi), any status, best candidates first.</summary>
    IReadOnlyList<AdapterInfo> GetAdapters();

    /// <summary>
    /// Resolves the adapter DNS should be applied to: the preferred adapter when its id still exists,
    /// otherwise an auto-detected candidate (up + IPv4 gateway first).
    /// </summary>
    AdapterInfo? ResolveTarget(string? preferredAdapterId);

    OperationResult ApplyDns(AdapterInfo adapter, string primaryDns, string? secondaryDns);

    /// <summary>Reverts the adapter to automatic (DHCP-provided) DNS.</summary>
    OperationResult ClearDns(AdapterInfo adapter);
}
