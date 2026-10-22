using QD.Core.Cotracts.DnsObjContracts;
using QuickDNSConsole;

namespace QD.Tests;

public class DnsObjViewModelTests
{
    [Fact]
    public void DnsAddresses_WithSecondary_ShowsBoth()
    {
        var vm = new DnsObjViewModel(1, "Cloudflare", "1.1.1.1", "1.0.0.1");

        Assert.Equal("1.1.1.1 | 1.0.0.1", vm.DnsAddresses);
    }

    [Fact]
    public void DnsAddresses_WithoutSecondary_ShowsPrimaryOnly()
    {
        var vm = new DnsObjViewModel(1, "Single", "1.1.1.1", "");

        Assert.Equal("1.1.1.1", vm.DnsAddresses);
    }

    [Fact]
    public void DnsAddresses_WithNullSecondary_ShowsPrimaryOnly()
    {
        var vm = new DnsObjViewModel(1, "Single", "1.1.1.1", null);

        Assert.Equal("1.1.1.1", vm.DnsAddresses);
    }

    [Fact]
    public void Constructor_NormalizesNullsToEmpty()
    {
        var vm = new DnsObjViewModel(1, null, null, null);

        Assert.Equal(string.Empty, vm.FirstDns);
        Assert.Equal(string.Empty, vm.SecondDns);
        Assert.Null(vm.Name);
    }
}

public class AppSettingsRoundTripTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"quickdns-tests-{Guid.NewGuid():N}.json");

    [Fact]
    public void RoundTrip_PreservesAdapterAndHistory()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = new AppSettings
            {
                AdapterId = "{A1B2C3D4-0000-0000-0000-000000000000}",
                HistoryLimit = 5,
                AccentColor = new RgbColor(10, 20, 30),
                History =
                [
                    new HistoryEntry { When = new DateTime(2026, 1, 2, 3, 4, 5), Text = "Applied Cloudflare" },
                ],
            };

            AppSettingsStore.Save(settings, path);
            var loaded = AppSettingsStore.Load(path);

            Assert.Equal(settings.AdapterId, loaded.AdapterId);
            Assert.Equal(5, loaded.HistoryLimit);
            Assert.Equal(new RgbColor(10, 20, 30), loaded.AccentColor);
            Assert.Single(loaded.History);
            Assert.Equal("Applied Cloudflare", loaded.History[0].Text);
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), loaded.History[0].When);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoundTrip_NullAdapterId_StaysNull()
    {
        var path = TempSettingsPath();
        try
        {
            AppSettingsStore.Save(new AppSettings { AdapterId = null }, path);
            var loaded = AppSettingsStore.Load(path);

            Assert.Null(loaded.AdapterId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var loaded = AppSettingsStore.Load(Path.Combine(Path.GetTempPath(), $"quickdns-missing-{Guid.NewGuid():N}.json"));

        Assert.Null(loaded.AdapterId);
        Assert.Equal(10, loaded.HistoryLimit);
        Assert.Empty(loaded.History);
    }

    [Fact]
    public void Load_MalformedFile_ReturnsDefaults()
    {
        var path = TempSettingsPath();
        try
        {
            File.WriteAllText(path, "{ this is not json ]");
            var loaded = AppSettingsStore.Load(path);

            Assert.Null(loaded.AdapterId);
            Assert.Empty(loaded.History);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
