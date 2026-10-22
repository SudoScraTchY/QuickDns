using QD.Core.Application;
using QD.Core.Cotracts.DnsObjContracts;
using QD.Core.Helper;
using QD.Tests.Fakes;

namespace QD.Tests;

public class DnsObjApplicationTests
{
    private static (DnsObjApplication App, FakeDnsObjRepository Repo, FakeAdapterService Adapters) Build()
    {
        var repo = new FakeDnsObjRepository();
        var adapters = new FakeAdapterService();
        return (new DnsObjApplication(repo, adapters), repo, adapters);
    }

    // ---- create ------------------------------------------------------------

    [Fact]
    public void Create_ValidPreset_SucceedsAndStores()
    {
        var (app, repo, _) = Build();

        var result = app.Create(new CreateDnsObj("1.1.1.1", "1.0.0.1", "Cloudflare"));

        Assert.True(result.IsSucceeded);
        Assert.Single(repo.Store);
        Assert.Equal("Cloudflare", repo.Store[0].Name);
    }

    [Fact]
    public void Create_DuplicateName_Fails()
    {
        var (app, _, _) = Build();
        app.Create(new CreateDnsObj("1.1.1.1", "1.0.0.1", "Cloudflare"));

        var result = app.Create(new CreateDnsObj("8.8.8.8", "", "Cloudflare"));

        Assert.False(result.IsSucceeded);
        Assert.Equal(ApplicationMessages.DuplicatedRecord, result.Message);
    }

    [Fact]
    public void Create_DuplicateNameCaseInsensitive_Fails()
    {
        var (app, _, _) = Build();
        app.Create(new CreateDnsObj("1.1.1.1", "1.0.0.1", "Cloudflare"));

        var result = app.Create(new CreateDnsObj("8.8.8.8", "", "cloudflare"));

        Assert.False(result.IsSucceeded);
    }

    [Fact]
    public void Create_DuplicateServersReversed_Fails()
    {
        var (app, _, _) = Build();
        app.Create(new CreateDnsObj("1.1.1.1", "1.0.0.1", "Cloudflare"));

        var result = app.Create(new CreateDnsObj("1.0.0.1", "1.1.1.1", "Cloudflare Reversed"));

        Assert.False(result.IsSucceeded);
    }

    [Fact]
    public void Create_InvalidPrimary_Fails()
    {
        var (app, repo, _) = Build();

        var result = app.Create(new CreateDnsObj("not-an-ip", "", "Bad"));

        Assert.False(result.IsSucceeded);
        Assert.Equal(ApplicationMessages.InvalidIpAddress, result.Message);
        Assert.Empty(repo.Store);
    }

    [Fact]
    public void Create_InvalidSecondary_Fails()
    {
        var (app, repo, _) = Build();

        var result = app.Create(new CreateDnsObj("1.1.1.1", "999.1.1.1", "Bad"));

        Assert.False(result.IsSucceeded);
        Assert.Empty(repo.Store);
    }

    [Fact]
    public void Create_EmptyName_Fails()
    {
        var (app, _, _) = Build();

        var result = app.Create(new CreateDnsObj("1.1.1.1", "", "  "));

        Assert.False(result.IsSucceeded);
        Assert.Equal(ApplicationMessages.NameRequired, result.Message);
    }

    // ---- delete (regression: previous inverted guard) ------------------------

    [Fact]
    public void Delete_OneOfSeveral_Succeeds()
    {
        var (app, repo, _) = Build();
        repo.AddPreset("A", "1.1.1.1");
        repo.AddPreset("B", "8.8.8.8");
        var idB = repo.Store.Single(x => x.Name == "B").Id;

        var result = app.Delete(idB);

        Assert.True(result.IsSucceeded, result.Message);
        Assert.Single(repo.Store);
        Assert.Equal("A", repo.Store[0].Name);
    }

    [Fact]
    public void Delete_MissingId_Fails()
    {
        var (app, repo, _) = Build();
        repo.AddPreset("A", "1.1.1.1");

        var result = app.Delete(999);

        Assert.False(result.IsSucceeded);
        Assert.Equal(ApplicationMessages.RequestedRecordNotExists, result.Message);
    }

    // ---- edit ----------------------------------------------------------------

    [Fact]
    public void Edit_ExistingPreset_Updates()
    {
        var (app, repo, _) = Build();
        repo.AddPreset("A", "1.1.1.1");
        var id = repo.Store[0].Id;

        var result = app.Edit(new EditDnsObj("9.9.9.9", "149.112.112.112", "Quad9", id));

        Assert.True(result.IsSucceeded, result.Message);
        Assert.Equal("Quad9", repo.Store[0].Name);
        Assert.Equal("9.9.9.9", repo.Store[0].FirstDns);
    }

    [Fact]
    public void Edit_NameCollisionWithOtherId_Fails()
    {
        var (app, repo, _) = Build();
        repo.AddPreset("A", "1.1.1.1");
        repo.AddPreset("B", "8.8.8.8");
        var idA = repo.Store.Single(x => x.Name == "A").Id;

        var result = app.Edit(new EditDnsObj("1.1.1.1", "", "B", idA));

        Assert.False(result.IsSucceeded);
    }

    [Fact]
    public void Edit_MissingId_Fails()
    {
        var (app, _, _) = Build();

        var result = app.Edit(new EditDnsObj("1.1.1.1", "", "X", 42));

        Assert.False(result.IsSucceeded);
    }

    // ---- set / unset -----------------------------------------------------------

    [Fact]
    public void SetDns_UnknownId_Fails()
    {
        var (app, _, adapters) = Build();
        adapters.Adapters.Add(TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true));

        var result = app.SetDns(123, null);

        Assert.False(result.IsSucceeded);
        Assert.Empty(adapters.Applied);
    }

    [Fact]
    public void SetDns_NoAdapters_Fails()
    {
        var (app, repo, adapters) = Build();
        repo.AddPreset("A", "1.1.1.1");
        var id = repo.Store[0].Id;

        var result = app.SetDns(id, null);

        Assert.False(result.IsSucceeded);
        Assert.Equal(ApplicationMessages.NoSuitableAdapter, result.Message);
    }

    [Fact]
    public void SetDns_UsesPreferredAdapter_WhenItExists()
    {
        var (app, repo, adapters) = Build();
        adapters.Adapters.Add(TestAdapters.Create("wifi0", "Wi-Fi", isUp: true, hasGateway: false, "8.8.8.8"));
        adapters.Adapters.Add(TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true));
        repo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");
        var id = repo.Store[0].Id;

        var result = app.SetDns(id, "wifi0");

        Assert.True(result.IsSucceeded);
        var call = Assert.Single(adapters.Applied);
        Assert.Equal("wifi0", call.Adapter.Id);
        Assert.Equal("1.1.1.1", call.Primary);
        Assert.Equal("1.0.0.1", call.Secondary);
    }

    [Fact]
    public void SetDns_MissingPreferredId_FallsBackToAuto()
    {
        var (app, repo, adapters) = Build();
        adapters.Adapters.Add(TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true));
        repo.AddPreset("Cloudflare", "1.1.1.1", "");
        var id = repo.Store[0].Id;

        var result = app.SetDns(id, "gone-adapter");

        Assert.True(result.IsSucceeded);
        Assert.Equal("eth0", Assert.Single(adapters.Applied).Adapter.Id);
    }

    [Fact]
    public void SetDns_EmptySecondary_PassesSingleServer()
    {
        var (app, repo, adapters) = Build();
        adapters.Adapters.Add(TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true));
        repo.AddPreset("Single", "1.1.1.1", "");
        var id = repo.Store[0].Id;

        app.SetDns(id, null);

        var call = Assert.Single(adapters.Applied);
        Assert.Equal("1.1.1.1", call.Primary);
        Assert.Null(call.Secondary);
    }

    [Fact]
    public void UnSetDns_ClearsResolvedAdapter()
    {
        var (app, _, adapters) = Build();
        adapters.Adapters.Add(TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true));

        var result = app.UnSetDns(null);

        Assert.True(result.IsSucceeded);
        Assert.Equal("eth0", Assert.Single(adapters.Cleared).Id);
    }

    [Fact]
    public void UnSetDns_NoAdapters_Fails()
    {
        var (app, _, _) = Build();

        var result = app.UnSetDns(null);

        Assert.False(result.IsSucceeded);
    }

    // ---- current dns matching ----------------------------------------------------

    [Fact]
    public void GetCurrentDns_MatchesPresetForward()
    {
        var (app, repo, adapters) = Build();
        adapters.Adapters.Add(TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true, "1.1.1.1", "1.0.0.1"));
        repo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");

        var current = app.GetCurrentDns(null);

        Assert.NotNull(current);
        Assert.Equal("Cloudflare", current.Name);
    }

    [Fact]
    public void GetCurrentDns_MatchesPresetReversedOrder()
    {
        var (app, repo, adapters) = Build();
        adapters.Adapters.Add(TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true, "1.0.0.1", "1.1.1.1"));
        repo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");

        var current = app.GetCurrentDns(null);

        Assert.NotNull(current);
        Assert.Equal("Cloudflare", current.Name);
    }

    [Fact]
    public void GetCurrentDns_CustomServers_ReturnsNull()
    {
        var (app, repo, adapters) = Build();
        adapters.Adapters.Add(TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true, "192.168.1.254"));
        repo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");

        var current = app.GetCurrentDns(null);

        Assert.Null(current);
    }

    [Fact]
    public void GetCurrentDns_NoDnsConfigured_ReturnsNull()
    {
        var (app, _, adapters) = Build();
        adapters.Adapters.Add(TestAdapters.Create("eth0", "Ethernet", isUp: true, hasGateway: true));

        Assert.Null(app.GetCurrentDns(null));
    }

    [Fact]
    public void GetCurrentDns_NoAdapter_ReturnsNull()
    {
        var (app, _, _) = Build();

        Assert.Null(app.GetCurrentDns(null));
    }

    [Fact]
    public void GetDetail_MissingId_ReturnsNull()
    {
        var (app, _, _) = Build();

        Assert.Null(app.GetDetail(1));
    }

    [Fact]
    public void GetDetail_ExistingId_ReturnsOrderedFields()
    {
        var (app, repo, _) = Build();
        repo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");
        var id = repo.Store[0].Id;

        var detail = app.GetDetail(id);

        Assert.NotNull(detail);
        Assert.Equal("1.1.1.1", detail.FirstDns);
        Assert.Equal("1.0.0.1", detail.SecondDns);
        Assert.Equal("Cloudflare", detail.Name);
        Assert.Equal(id, detail.Id);
    }

    // ---- export / import ---------------------------------------------------------

    [Fact]
    public void ExportImport_RoundTrip_TransfersAllPresets()
    {
        var file = TempPath(".json");
        try
        {
            var (source, sourceRepo, _) = Build();
            sourceRepo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");
            sourceRepo.AddPreset("Quad9", "9.9.9.9", "");

            var export = source.ExportPresets(file);
            Assert.True(export.IsSucceeded, export.Message);

            var (target, targetRepo, _) = Build();
            var import = target.ImportPresets(file);

            Assert.True(import.IsSucceeded, import.Message);
            Assert.Equal(2, targetRepo.Store.Count);
            Assert.Contains(targetRepo.Store, p => p.Name == "Cloudflare" && p.FirstDns == "1.1.1.1" && p.SecondDns == "1.0.0.1");
            Assert.Contains(targetRepo.Store, p => p.Name == "Quad9" && p.SecondDns == "");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Import_SkipsDuplicates()
    {
        var file = TempPath(".json");
        try
        {
            var (source, sourceRepo, _) = Build();
            sourceRepo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");
            Assert.True(source.ExportPresets(file).IsSucceeded);

            var (target, targetRepo, _) = Build();
            targetRepo.AddPreset("Cloudflare", "8.8.8.8", ""); // same name, different servers

            var import = target.ImportPresets(file);

            Assert.True(import.IsSucceeded);
            Assert.Single(targetRepo.Store); // skipped, not duplicated
            Assert.Equal(0, targetRepo.Store.Count(p => p.FirstDns == "1.1.1.1"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Import_IgnoresInvalidRows()
    {
        var file = TempPath(".json");
        var payload = """
            {
              "App": "QuickDNS",
              "Schema": 1,
              "Presets": [
                { "Name": "Broken", "PrimaryDns": "not-an-ip", "SecondaryDns": "" },
                { "Name": "Good", "PrimaryDns": "9.9.9.9", "SecondaryDns": "" }
              ]
            }
            """;
        try
        {
            File.WriteAllText(file, payload);
            var (app, repo, _) = Build();

            var import = app.ImportPresets(file);

            Assert.True(import.IsSucceeded, import.Message);
            Assert.Single(repo.Store);
            Assert.Equal("Good", repo.Store[0].Name);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Import_MissingFile_Fails()
    {
        var (app, _, _) = Build();

        var result = app.ImportPresets(TempPath(".missing.json"));

        Assert.False(result.IsSucceeded);
    }

    [Fact]
    public void Import_GarbageFile_Fails()
    {
        var file = TempPath(".json");
        try
        {
            File.WriteAllText(file, "{ this is not json ]");
            var (app, _, _) = Build();

            var result = app.ImportPresets(file);

            Assert.False(result.IsSucceeded);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void WriteAutoBackup_EmptyStore_SkipsAndKeepsExistingBackup()
    {
        var file = TempPath(".json");
        try
        {
            File.WriteAllText(file, "keep me");
            var (app, repo, _) = Build(); // empty store

            var result = app.WriteAutoBackup(file);

            Assert.False(result.IsSucceeded);
            Assert.Equal("keep me", File.ReadAllText(file)); // untouched
            Assert.Empty(repo.Store);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void WriteAutoBackup_NonEmptyStore_RefreshesBackup()
    {
        var file = TempPath(".json");
        try
        {
            var (app, repo, _) = Build();
            repo.AddPreset("Cloudflare", "1.1.1.1", "1.0.0.1");

            var result = app.WriteAutoBackup(file);

            Assert.True(result.IsSucceeded, result.Message);
            Assert.Contains("Cloudflare", File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string TempPath(string suffix) =>
        Path.Combine(Path.GetTempPath(), $"quickdns-tests-{Guid.NewGuid():N}{suffix}");
}
