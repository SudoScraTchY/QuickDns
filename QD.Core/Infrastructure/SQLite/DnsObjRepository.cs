using Microsoft.Data.Sqlite;
using QD.Core.Cotracts.DnsObjContracts;
using QD.Core.Domain.DnsObj;

namespace QD.Core.Infrastructure.SQLite;

/// <summary>SQLite-backed preset store (plain ADO.NET, no reflection mapping — trim-safe).</summary>
public sealed class DnsObjRepository(DnsDatabase database) : IDnsObjRepository
{
    private const string SelectColumns = "id, name, primary_dns, secondary_dns";

    private readonly DnsDatabase _database = database;

    public int Count() => (int)_database.Query(connection =>
        DnsDatabase.Scalar(connection, "SELECT COUNT(1) FROM dns_presets;"));

    public IReadOnlyList<DnsObjViewModel> GetAll() => _database.Query(connection =>
    {
        var presets = new List<DnsObjViewModel>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM dns_presets ORDER BY id DESC;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            presets.Add(MapRow(reader));
        return presets;
    });

    public DnsObjViewModel? FindById(int id) => _database.Query(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM dns_presets WHERE id = @id;";
        DnsDatabase.Bind(command, ("@id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapRow(reader) : null;
    });

    public DnsObjViewModel Create(string name, string primaryDns, string secondaryDns) => _database.Query(connection =>
    {
        var now = DateTime.UtcNow.ToString("O");
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO dns_presets (name, primary_dns, secondary_dns, created_utc, updated_utc)
                VALUES (@name, @primary, @secondary, @now, @now);
                """;
            DnsDatabase.Bind(insert,
                ("@name", name),
                ("@primary", primaryDns),
                ("@secondary", secondaryDns),
                ("@now", now));
            insert.ExecuteNonQuery();
        }

        var id = DnsDatabase.Scalar(connection, "SELECT last_insert_rowid();");
        return new DnsObjViewModel((int)id, name, primaryDns, secondaryDns);
    });

    public bool Update(int id, string name, string primaryDns, string secondaryDns) => _database.Query(connection =>
        DnsDatabase.ExecuteRows(connection, """
            UPDATE dns_presets
            SET name = @name, primary_dns = @primary, secondary_dns = @secondary, updated_utc = @now
            WHERE id = @id;
            """,
            ("@name", name),
            ("@primary", primaryDns),
            ("@secondary", secondaryDns),
            ("@now", DateTime.UtcNow.ToString("O")),
            ("@id", id)) > 0);

    public bool Delete(int id) => _database.Query(connection =>
        DnsDatabase.ExecuteRows(connection, "DELETE FROM dns_presets WHERE id = @id;", ("@id", id)) > 0);

    public bool NameExists(string name, int excludeId = 0) => _database.Query(connection =>
        DnsDatabase.Scalar(connection,
            "SELECT COUNT(1) FROM dns_presets WHERE name = @name AND id <> @exclude;",
            ("@name", name),
            ("@exclude", excludeId)) > 0);

    public bool ServersExist(string primaryDns, string secondaryDns) => _database.Query(connection =>
        DnsDatabase.Scalar(connection, """
            SELECT COUNT(1) FROM dns_presets
            WHERE (primary_dns = @p AND secondary_dns = @s) OR (primary_dns = @s AND secondary_dns = @p);
            """,
            ("@p", primaryDns),
            ("@s", secondaryDns)) > 0);

    private static DnsObjViewModel MapRow(SqliteDataReader reader) =>
        new(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
}
