namespace QD.Core.Application.Networking;

/// <summary>Broad physical kind of a network adapter, used for display and ordering.</summary>
public enum AdapterKind
{
    Ethernet,
    Wireless,
    Other,
}

/// <summary>
/// A snapshot of one network adapter: identity, status and current IP/DNS configuration.
/// Immutable; refresh by re-enumerating through <see cref="INetworkAdapterService.GetAdapters"/>.
/// </summary>
public sealed class AdapterInfo
{
    /// <summary>Stable OS id (GUID on Windows, interface name/path on Linux).</summary>
    public required string Id { get; init; }

    /// <summary>OS connection name ("Ethernet", "Wi-Fi", "eth0", "wlan0", ...).</summary>
    public required string Name { get; init; }

    /// <summary>Hardware/driver description.</summary>
    public required string Description { get; init; }

    public required AdapterKind Kind { get; init; }

    public required bool IsUp { get; init; }

    /// <summary>True when the adapter has an IPv4 gateway (i.e. it is the likely internet connection).</summary>
    public bool HasGateway { get; init; }

    /// <summary>IPv4 interface index where the OS exposes one (Windows WMI join key, resolvectl link id).</summary>
    public int? InterfaceIndex { get; init; }

    public IReadOnlyList<string> IpAddresses { get; init; } = [];
    public IReadOnlyList<string> GatewayAddresses { get; init; } = [];

    /// <summary>DNS servers currently configured on this adapter, in OS order.</summary>
    public IReadOnlyList<string> DnsServers { get; init; } = [];

    public string DnsSummary => DnsServers.Count == 0 ? "(automatic / none)" : string.Join(", ", DnsServers);

    public string StatusText => IsUp ? "Up" : "Down";
}
