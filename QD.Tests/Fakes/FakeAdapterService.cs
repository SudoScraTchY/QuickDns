using QD.Core.Application.Networking;
using QD.Core.Helper;

namespace QD.Tests.Fakes;

/// <summary>Scriptable INetworkAdapterService that records apply/clear calls.</summary>
internal sealed class FakeAdapterService : INetworkAdapterService
{
    public List<AdapterInfo> Adapters { get; set; } = [];
    public bool Elevated { get; set; } = true;
    public OperationResult ApplyResult { get; set; } = new();
    public OperationResult ClearResult { get; set; } = new();

    public List<(AdapterInfo Adapter, string Primary, string? Secondary)> Applied { get; } = [];
    public List<AdapterInfo> Cleared { get; } = [];

    public string Platform => "Fake";
    public string BackendDescription => "fake backend";
    public bool IsElevated => Elevated;

    public IReadOnlyList<AdapterInfo> GetAdapters() => Adapters;

    public AdapterInfo? ResolveTarget(string? preferredAdapterId) =>
        AdapterCatalog.ResolveTarget(Adapters, preferredAdapterId);

    public OperationResult ApplyDns(AdapterInfo adapter, string primaryDns, string? secondaryDns)
    {
        Applied.Add((adapter, primaryDns, secondaryDns));
        return ApplyResult.Succeeded("fake applied");
    }

    public OperationResult ClearDns(AdapterInfo adapter)
    {
        Cleared.Add(adapter);
        return ClearResult.Succeeded("fake cleared");
    }
}

internal static class TestAdapters
{
    public static AdapterInfo Create(
        string id,
        string name,
        bool isUp = true,
        bool hasGateway = false,
        params string[] dnsServers)
        => Create(id, name, isUp, hasGateway, AdapterKind.Ethernet, dnsServers);

    public static AdapterInfo Create(
        string id,
        string name,
        bool isUp,
        bool hasGateway,
        AdapterKind kind,
        params string[] dnsServers)
    {
        return new AdapterInfo
        {
            Id = id,
            Name = name,
            Description = $"{name} (fake)",
            Kind = kind,            IsUp = isUp,
            HasGateway = hasGateway,
            InterfaceIndex = 7,
            IpAddresses = hasGateway ? ["192.168.1.10"] : [],
            GatewayAddresses = hasGateway ? ["192.168.1.1"] : [],
            DnsServers = dnsServers,
        };
    }
}
