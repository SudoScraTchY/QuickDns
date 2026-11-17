using QD.Core.Application.Routing;
using QD.Core.Infrastructure.SQLite;
using QuickDNSConsole;

namespace QD.Tests;

/// <summary>Unit tests for the YogaDNS-style route pattern matching.</summary>
public class DnsRouteEngineTests
{
    private static DnsRouteEngine Engine(params (string Pattern, string Primary)[] rules) =>
        new(rules.Select(r => new DnsRouteServer(r.Pattern, r.Primary, null)).ToList());

    [Fact]
    public void PlainPattern_MatchesDomain_AndSubdomains()
    {
        var engine = Engine(("example.com", "1.1.1.1"));

        Assert.Equal("1.1.1.1", engine.Match("example.com")?.Primary);
        Assert.Equal("1.1.1.1", engine.Match("EXAMPLE.COM.")?.Primary); // case + trailing dot
        Assert.Equal("1.1.1.1", engine.Match("www.example.com")?.Primary);
        Assert.Equal("1.1.1.1", engine.Match("a.b.example.com")?.Primary);
        Assert.Null(engine.Match("notexample.com")?.Primary);        // no dot-boundary bypass
        Assert.Null(engine.Match("example.com.evil.net")?.Primary);  // suffix must align
        Assert.Null(engine.Match(null)?.Primary);
        Assert.Null(engine.Match("")?.Primary);
    }

    [Fact]
    public void StarPrefixPattern_MatchesSubdomainsOnly()
    {
        var engine = Engine(("*.example.com", "1.1.1.1"));

        Assert.Equal("1.1.1.1", engine.Match("www.example.com")?.Primary);
        Assert.Null(engine.Match("example.com")?.Primary); // the domain itself is excluded
        Assert.Equal("1.1.1.1", engine.Match("a.b.example.com")?.Primary); // deep subdomains match
    }

    [Fact]
    public void LongestSuffix_Wins_OverCatchAll()
    {
        var engine = Engine(("*", "9.9.9.9"), ("*.ir", "178.22.122.100"), ("shecan.ir", "178.22.122.101"));

        Assert.Equal("178.22.122.101", engine.Match("shecan.ir")?.Primary);          // exact beats suffix
        Assert.Equal("178.22.122.101", engine.Match("www.shecan.ir")?.Primary);      // more labels wins over *.ir
        Assert.Equal("178.22.122.100", engine.Match("radio.git.ir")?.Primary);
        Assert.Equal("9.9.9.9", engine.Match("google.com")?.Primary);                // fallback
        Assert.Equal("9.9.9.9", engine.Match("anything.example")?.Primary);
    }

    [Fact]
    public void CatchAllOnly_MatchesEverything()
    {
        var engine = Engine(("*", "9.9.9.9"));

        Assert.Equal("9.9.9.9", engine.Match("anything.at.all")?.Primary);
        Assert.Equal("9.9.9.9", engine.Match("localhost")?.Primary);
    }

    [Fact]
    public void NoRules_MatchesNothing()
    {
        var engine = Engine();

        Assert.Null(engine.Match("example.com"));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("example.com", true)]
    [InlineData("*.example.com", true)]
    [InlineData("*.ir", true)]
    [InlineData("Example.COM.", true)]
    [InlineData("", false)]
    [InlineData("*.", false)]
    [InlineData("*example.com", false)]     // bare star without dot
    [InlineData("*.exa mple.com", false)]   // space in base
    [InlineData("exa_mple.com", false)]     // underscore
    [InlineData("m\u00fcnchen.de", false)]  // non-ASCII
    public void IsValidPattern_AcceptsOnlySupportedForms(string pattern, bool expected)
    {
        Assert.Equal(expected, DnsRouteEngine.IsValidPattern(pattern));
    }

    [Fact]
    public void InvalidPatterns_AreSkipped_AtCompileTime()
    {
        var engine = new DnsRouteEngine(
        [
            new DnsRouteServer("not valid", "1.1.1.1", null),
            new DnsRouteServer("*", "9.9.9.9", null),
        ]);

        Assert.Single(engine.Rules);
        Assert.Equal("9.9.9.9", engine.Match("example.com")?.Primary);
    }
}

/// <summary>Integration tests for the dns_routes store (schema v2) and its preset cascade.</summary>
public sealed class DnsRouteRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"qd-routes-{Guid.NewGuid():N}");

    private string DbPath => Path.Combine(_directory, "DnsDB.db");

    public DnsRouteRepositoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private (DnsRouteRepository Routes, DnsObjRepository Presets) OpenStores()
    {
        var database = new DnsDatabase(DbPath);
        database.Initialize();
        return (new DnsRouteRepository(database), new DnsObjRepository(database));
    }

    [Fact]
    public void Create_Find_Update_Delete_RoundTrip()
    {
        var (routes, presets) = OpenStores();
        var preset = presets.Create("Quad9", "9.9.9.9", "149.112.112.112");

        var created = routes.Create("*.ir", preset.Id);
        Assert.True(created.Id > 0);
        Assert.Equal("*.ir", created.Pattern);
        Assert.True(created.Enabled);

        var found = routes.FindById(created.Id);
        Assert.NotNull(found);
        Assert.Equal("*.ir", found.Pattern);
        Assert.Equal(preset.Id, found.PresetId);

        Assert.True(routes.Update(created.Id, "*.shecan.ir", preset.Id, enabled: false));
        var updated = routes.FindById(created.Id);
        Assert.NotNull(updated);
        Assert.Equal("*.shecan.ir", updated.Pattern);
        Assert.False(updated.Enabled);

        Assert.True(routes.Delete(created.Id));
        Assert.Null(routes.FindById(created.Id));
        Assert.Equal(0, routes.Count());
    }

    [Fact]
    public void Patterns_AreUnique_AndNormalized()
    {
        var (routes, presets) = OpenStores();
        var preset = presets.Create("CF", "1.1.1.1", "1.0.0.1");

        routes.Create("Example.COM", preset.Id);
        Assert.True(routes.PatternExists("example.com"));   // case-insensitive match
        Assert.False(routes.PatternExists("example.org"));

        // Unique index rejects a second identical pattern (case-insensitive).
        Assert.ThrowsAny<Exception>(() => routes.Create("example.com", preset.Id));
    }

    [Fact]
    public void SeedFallbackRouteIfEmpty_CreatesStarRule_Once()
    {
        var (routes, presets) = OpenStores();
        presets.Create("Cloudflare", "1.1.1.1", "1.0.0.1");
        presets.Create("Google", "8.8.8.8", "8.8.4.4");

        PresetSeeder.SeedFallbackRouteIfEmpty(routes, presets);
        PresetSeeder.SeedFallbackRouteIfEmpty(routes, presets); // idempotent

        Assert.Equal(1, routes.Count());
        var fallback = routes.GetAll().Single();
        Assert.Equal("*", fallback.Pattern);
        Assert.Equal("Cloudflare", presets.FindById(fallback.PresetId)?.Name);

        // User-created rules disable the default seeding forever.
        routes.Create("*.ir", presets.GetAll().First(p => p.Name != "Cloudflare").Id);
        PresetSeeder.SeedFallbackRouteIfEmpty(routes, presets);
        Assert.Equal(2, routes.Count());
    }

    [Fact]
    public void DeletingAPreset_CascadesItsRoutes()
    {
        var (routes, presets) = OpenStores();
        var preset = presets.Create("Quad9", "9.9.9.9", "149.112.112.112");
        routes.Create("*.ir", preset.Id);

        Assert.Equal(1, routes.Count());
        presets.Delete(preset.Id);

        Assert.Equal(0, routes.Count()); // FK ON DELETE CASCADE cleaned the rule
    }

    [Fact]
    public void ExistingV1Store_GainsRoutesTable_OnMigration()
    {
        // Simulate a v1 store: create it with the current schema, then force user_version back to 1.
        var (routes, presets) = OpenStores();
        var preset = presets.Create("CF", "1.1.1.1", "1.0.0.1");
        routes.Create("*.ir", preset.Id);

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            DnsDatabase.Execute(connection, "PRAGMA user_version = 1;");
        }

        // Reopening must migrate to v2 without losing data (schema uses IF NOT EXISTS).
        var database = new DnsDatabase(DbPath);
        Assert.False(database.Initialize()); // no quarantine, clean migration
        var reopened = new DnsRouteRepository(database);
        Assert.Equal(1, reopened.Count());
        Assert.NotNull(reopened.FindById(1));
    }
}
