using Microsoft.Data.Sqlite;
using QD.Core.Infrastructure.SQLite;

namespace QD.Tests;

/// <summary>Integration tests against a real SQLite file (Microsoft.Data.Sqlite).</summary>
public sealed class DnsObjRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"qd-store-{Guid.NewGuid():N}");

    private string DbPath => Path.Combine(_directory, "DnsDB.db");

    public DnsObjRepositoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private DnsObjRepository OpenRepository(out DnsDatabase database)
    {
        database = new DnsDatabase(DbPath);
        database.Initialize();
        return new DnsObjRepository(database);
    }

    [Fact]
    public void Create_Find_Update_Delete_RoundTrip()
    {
        var repository = OpenRepository(out _);

        var created = repository.Create("Cloudflare", "1.1.1.1", "1.0.0.1");
        Assert.True(created.Id > 0);

        var found = repository.FindById(created.Id);
        Assert.NotNull(found);
        Assert.Equal("Cloudflare", found.Name);
        Assert.Equal("1.1.1.1", found.FirstDns);
        Assert.Equal("1.0.0.1", found.SecondDns);

        Assert.True(repository.Update(created.Id, "CF", "1.1.1.1", ""));
        var updated = repository.FindById(created.Id);
        Assert.NotNull(updated);
        Assert.Equal("CF", updated.Name);
        Assert.Equal("", updated.SecondDns);

        Assert.True(repository.Delete(created.Id));
        Assert.Null(repository.FindById(created.Id));
        Assert.Equal(0, repository.Count());
    }

    [Fact]
    public void GetAll_ReturnsNewestFirst()
    {
        var repository = OpenRepository(out _);
        repository.Create("First", "1.1.1.1", "");
        repository.Create("Second", "8.8.8.8", "");

        var all = repository.GetAll();

        Assert.Equal(["Second", "First"], all.Select(p => p.Name!).ToArray());
    }

    [Fact]
    public void PersistsAcrossRepositoryInstances()
    {
        var first = OpenRepository(out _);
        first.Create("Persisted", "9.9.9.9", "");

        var second = OpenRepository(out _);

        Assert.Equal(1, second.Count());
        Assert.Equal("Persisted", second.GetAll()[0].Name);
    }

    [Fact]
    public void NameExists_IsCaseInsensitive_AndRespectsExcludeId()
    {
        var repository = OpenRepository(out _);
        var preset = repository.Create("Cloudflare", "1.1.1.1", "");

        Assert.True(repository.NameExists("cloudflare"));
        Assert.False(repository.NameExists("cloudflare", excludeId: preset.Id));
        Assert.False(repository.NameExists("Google"));
    }

    [Fact]
    public void ServersExist_MatchesEitherOrder()
    {
        var repository = OpenRepository(out _);
        repository.Create("Cloudflare", "1.1.1.1", "1.0.0.1");

        Assert.True(repository.ServersExist("1.1.1.1", "1.0.0.1"));
        Assert.True(repository.ServersExist("1.0.0.1", "1.1.1.1"));
        Assert.False(repository.ServersExist("1.1.1.1", "8.8.8.8"));
    }

    [Fact]
    public void DuplicateName_IsRejectedByUniqueIndex()
    {
        var repository = OpenRepository(out _);
        repository.Create("Cloudflare", "1.1.1.1", "");

        // Application layer checks NameExists first; the index is the last line of defence.
        Assert.Throws<SqliteException>(() => repository.Create("CLOUDFLARE", "8.8.8.8", ""));
    }

    [Fact]
    public void JournalMode_IsWal()
    {
        var repository = OpenRepository(out var database);
        repository.Create("A", "1.1.1.1", "");

        using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", command.ExecuteScalar()?.ToString(), ignoreCase: true);
    }

    [Fact]
    public void MigratesLegacySqliteNetTable()
    {
        // Build a legacy store exactly like QuickDNS 1.x/2.0 (sqlite-net) wrote it:
        // table "Dns", dates as INTEGER ticks, one duplicate name to be ignored.
        using (var legacy = new SqliteConnection($"Data Source={DbPath}"))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                CREATE TABLE Dns (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FirstDns TEXT NOT NULL,
                    SecondDns TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    CreationDate BIGINT NOT NULL,
                    ModifiedDate BIGINT
                );
                INSERT INTO Dns (FirstDns, SecondDns, Name, CreationDate, ModifiedDate)
                VALUES ('1.1.1.1', '1.0.0.1', 'Cloudflare', 639282198071979330, NULL);
                INSERT INTO Dns (FirstDns, SecondDns, Name, CreationDate, ModifiedDate)
                VALUES ('8.8.8.8', '8.8.4.4', 'Google', 639282198072042043, NULL);
                INSERT INTO Dns (FirstDns, SecondDns, Name, CreationDate, ModifiedDate)
                VALUES ('9.9.9.9', '', 'cloudflare', 639282198072085872, NULL);
                """;
            command.ExecuteNonQuery();
        }

        var repository = OpenRepository(out var database);

        var all = repository.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, p => p.Name == "Cloudflare" && p.FirstDns == "1.1.1.1");
        Assert.Contains(all, p => p.Name == "Google" && p.SecondDns == "8.8.4.4");

        // Legacy table is gone after the migration.
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
        connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='Dns';";
        Assert.Equal(0L, (long)check.ExecuteScalar()!);
    }

    [Fact]
    public void CorruptStore_IsQuarantinedAndRecreated()
    {
        File.WriteAllBytes(DbPath, "this is definitely not a sqlite database — just garbage bytes"u8.ToArray());

        var database = new DnsDatabase(DbPath);
        var quarantined = database.Initialize();

        Assert.True(quarantined);
        Assert.NotEmpty(Directory.GetFiles(_directory, "DnsDB.db.corrupt-*"));

        var repository = new DnsObjRepository(database);
        Assert.Equal(0, repository.Count());
        repository.Create("Fresh", "1.1.1.1", ""); // fresh store is writable
        Assert.Equal(1, repository.Count());
    }

    [Fact]
    public void SecondInstance_CanReadWhileFirstIsOpen()
    {
        var first = OpenRepository(out _);
        first.Create("Shared", "1.1.1.1", "");

        // Simulates a second running instance: separate DnsDatabase/repository instances.
        var second = OpenRepository(out _);

        Assert.Equal(1, second.Count());
        Assert.Equal("Shared", second.GetAll()[0].Name);
    }
}
