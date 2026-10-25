using QD.Core.Application;
using QD.Core.Application.Catalog;
using QD.Core.Application.Networking;
using QD.Core.Cotracts.DnsObjContracts;
using QD.Core.Helper;
using QD.Tests.Fakes;

namespace QD.Tests;

public class DnsCatalogTests
{
    [Fact]
    public void EmbeddedCatalog_Loads()
    {
        var catalog = DnsCatalog.Embedded.Entries;

        Assert.True(catalog.Count >= 18, $"expected at least 18 catalog entries, found {catalog.Count}");
        Assert.Contains(catalog, e => e.Group == "Global");
        Assert.Contains(catalog, e => e.Group == "Iran");
        Assert.Contains(catalog, e => e.Name == "Shecan" && e.Primary == "178.22.122.100");
        Assert.Contains(catalog, e => e.Name == "Shatel" && e.Primary == "85.15.1.14");
        Assert.Contains(catalog, e => e.Name == "Electro" && e.Primary == "78.157.42.100");
        Assert.Contains(catalog, e => e.Name == "Begzar" && e.Primary == "185.55.226.26");
        Assert.Contains(catalog, e => e.Name == "403.online" && e.Primary == "10.202.10.202");
        Assert.Contains(catalog, e => e.Name == "Cloudflare" && e.Primary == "1.1.1.1");
    }

    [Fact]
    public void EmbeddedCatalog_EveryServerAddressIsValid()
    {
        foreach (var entry in DnsCatalog.Embedded.Entries)
        {
            Assert.True(DnsValidator.IsValidIpAddress(entry.Primary), $"{entry.Name} primary '{entry.Primary}'");
            Assert.True(
                string.IsNullOrWhiteSpace(entry.Secondary) || DnsValidator.IsValidIpAddress(entry.Secondary),
                $"{entry.Name} secondary '{entry.Secondary}'");
        }
    }

    [Fact]
    public void EmbeddedCatalog_NamesAreUnique()
    {
        var names = DnsCatalog.Embedded.Entries.Select(e => e.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CatalogImport_CreatesPresetsAndSkipsDuplicates()
    {
        var repo = new FakeDnsObjRepository();
        var adapters = new FakeAdapterService();
        var app = new DnsObjApplication(repo, adapters);

        // The bundled defaults include Cloudflare; importing the whole catalog must
        // skip that duplicate and add the rest.
        repo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");

        var imported = 0;
        var skipped = 0;
        foreach (var entry in DnsCatalog.Embedded.Entries)
        {
            var result = app.Create(new CreateDnsObj(entry.Primary, entry.Secondary, entry.Name));
            if (result.IsSucceeded) imported++;
            else skipped++;
        }

        Assert.Equal(DnsCatalog.Embedded.Entries.Count - 1, imported);
        Assert.Equal(1, skipped);
    }
}
