using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QD.Core.Application.Networking;

/// <summary>
/// Shared adapter enumeration / resolution logic used by both platform services.
/// Public so the unit tests can drive <see cref="ResolveTarget"/> with synthetic adapters.
/// </summary>
public static class AdapterCatalog
{
    /// <summary>Enumerates usable adapters (Ethernet / Wi-Fi) from the OS.</summary>
    public static IReadOnlyList<AdapterInfo> FromNetworkInterfaces()
    {
        var adapters = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
                continue;

            // Windows exposes driver sub-interfaces (QoS scheduler, WFP/lightweight filters,
            // WAN miniports, kernel debugger, virtual switch extensions) as separate
            // NetworkInterfaces. None of them can carry DNS settings, so drop anything
            // without an IPv4 stack plus the few pseudo adapters that still report one.
            if (!HasIpv4Stack(nic) || IsPseudoAdapter(nic))
                continue;

            adapters.Add(Map(nic));
        }

        return SortUsable(adapters);
    }

    private static bool HasIpv4Stack(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties() is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPseudoAdapter(NetworkInterface nic)
    {
        var description = nic.Description;
        return description.StartsWith("WAN Miniport", StringComparison.OrdinalIgnoreCase)
               || description.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Best candidates first: up with IPv4 gateway, then up, then Ethernet before Wi-Fi, then by name.</summary>
    public static List<AdapterInfo> SortUsable(IEnumerable<AdapterInfo> adapters)
    {
        return adapters
            .OrderByDescending(a => a.IsUp && a.HasGateway)
            .ThenByDescending(a => a.IsUp)
            .ThenBy(a => a.Kind == AdapterKind.Ethernet ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static AdapterInfo? ResolveTarget(IEnumerable<AdapterInfo> adapters, string? preferredAdapterId)
    {
        var list = adapters as IList<AdapterInfo> ?? adapters.ToList();
        if (list.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(preferredAdapterId))
        {
            var preferred = list.FirstOrDefault(a => a.Id.Equals(preferredAdapterId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
                return preferred;
        }

        return list.FirstOrDefault(a => a.IsUp && a.HasGateway)
               ?? list.FirstOrDefault(a => a.IsUp)
               ?? list[0];
    }

    public static AdapterInfo Map(NetworkInterface nic)
    {
        var ip = nic.GetIPProperties();

        int? interfaceIndex = null;
        try
        {
            interfaceIndex = ip.GetIPv4Properties()?.Index;
        }
        catch
        {
            // Some virtual adapters do not expose IPv4 properties.
        }

        return new AdapterInfo
        {
            Id = nic.Id,
            Name = nic.Name,
            Description = nic.Description,
            Kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? AdapterKind.Wireless : AdapterKind.Ethernet,
            IsUp = nic.OperationalStatus == OperationalStatus.Up,
            HasGateway = ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork),
            InterfaceIndex = interfaceIndex,
            IpAddresses = ip.UnicastAddresses
                .Select(u => u.Address.ToString())
                .Where(s => s.Length > 0)
                .ToArray(),
            GatewayAddresses = ip.GatewayAddresses
                .Select(g => g.Address.ToString())
                .Where(s => s.Length > 0 && s != "::")
                .ToArray(),
            DnsServers = ip.DnsAddresses
                .Select(d => d.ToString())
                .Where(s => s.Length > 0)
                .ToArray(),
        };
    }
}
