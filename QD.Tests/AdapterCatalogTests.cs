using QD.Core.Application.Networking;
using QD.Tests.Fakes;

namespace QD.Tests;

public class AdapterCatalogTests
{
    [Fact]
    public void ResolveTarget_EmptyList_ReturnsNull()
    {
        Assert.Null(AdapterCatalog.ResolveTarget([], null));
    }

    [Fact]
    public void ResolveTarget_PreferredExists_ReturnsPreferred_EvenWhenNotBest()
    {
        var down = TestAdapters.Create("down", "DownAdapter", isUp: false, hasGateway: false);
        var best = TestAdapters.Create("best", "UpAdapter", isUp: true, hasGateway: true);

        var resolved = AdapterCatalog.ResolveTarget([down, best], "down");

        Assert.NotNull(resolved);
        Assert.Equal("down", resolved.Id);
    }

    [Fact]
    public void ResolveTarget_PreferredMissing_FallsBackToBest()
    {
        var best = TestAdapters.Create("best", "UpAdapter", isUp: true, hasGateway: true);

        var resolved = AdapterCatalog.ResolveTarget([best], "vanished");

        Assert.NotNull(resolved);
        Assert.Equal("best", resolved.Id);
    }

    [Fact]
    public void ResolveTarget_Auto_PrefersUpWithGateway()
    {
        var wifi = TestAdapters.Create("wifi", "Wi-Fi", isUp: true, hasGateway: false, kind: AdapterKind.Wireless);
        var ethernet = TestAdapters.Create("eth", "Ethernet", isUp: true, hasGateway: true);

        var resolved = AdapterCatalog.ResolveTarget([wifi, ethernet], null);

        Assert.NotNull(resolved);
        Assert.Equal("eth", resolved.Id);
    }

    [Fact]
    public void ResolveTarget_Auto_NoGateway_PrefersUpOverDown()
    {
        var down = TestAdapters.Create("down", "DownAdapter", isUp: false, hasGateway: true);
        var up = TestAdapters.Create("up", "UpAdapter", isUp: true, hasGateway: false);

        var resolved = AdapterCatalog.ResolveTarget([down, up], null);

        Assert.NotNull(resolved);
        Assert.Equal("up", resolved.Id);
    }

    [Fact]
    public void ResolveTarget_AllDown_ReturnsFirstAsLastResort()
    {
        var a = TestAdapters.Create("a", "A", isUp: false);
        var b = TestAdapters.Create("b", "B", isUp: false);

        var resolved = AdapterCatalog.ResolveTarget([a, b], null);

        Assert.NotNull(resolved);
        Assert.Equal("a", resolved.Id);
    }

    [Fact]
    public void SortUsable_PutsUpWithGatewayFirst()
    {
        var down = TestAdapters.Create("down", "Down", isUp: false);
        var upNoGw = TestAdapters.Create("upNoGw", "UpNoGw", isUp: true);
        var upGw = TestAdapters.Create("upGw", "UpGw", isUp: true, hasGateway: true);

        var sorted = AdapterCatalog.SortUsable([down, upNoGw, upGw]);

        Assert.Equal(["upGw", "upNoGw", "down"], sorted.Select(a => a.Id).ToArray());
    }

    [Fact]
    public void ResolveTarget_PreferredIdIsCaseInsensitive()
    {
        var best = TestAdapters.Create("ETH-GUID", "Ethernet", isUp: true, hasGateway: true);

        var resolved = AdapterCatalog.ResolveTarget([best], "eth-guid");

        Assert.NotNull(resolved);
        Assert.Equal("ETH-GUID", resolved.Id);
    }
}
