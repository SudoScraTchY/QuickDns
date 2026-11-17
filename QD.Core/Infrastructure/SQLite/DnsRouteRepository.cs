using Microsoft.Data.Sqlite;
using QD.Core.Domain.DnsRoute;

namespace QD.Core.Infrastructure.SQLite;

/// <summary>SQLite-backed route rule store (plain ADO.NET, no reflection mapping — trim-safe).</summary>
public sealed class DnsRouteRepository(DnsDatabase database) : IDnsRouteRepository
{
    private const string SelectColumns = "id, pattern, preset_id, enabled, position";

    private readonly DnsDatabase _database = database;

    public int Count() => (int)_database.Query(connection =>
        DnsDatabase.Scalar(connection, "SELECT COUNT(1) FROM dns_routes;"));

    public IReadOnlyList<DnsRoute> GetAll() => _database.Query(connection =>
    {
        var routes = new List<DnsRoute>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM dns_routes ORDER BY position, id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            routes.Add(MapRow(reader));
        return routes;
    });

    public DnsRoute? FindById(int id) => _database.Query(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM dns_routes WHERE id = @id;";
        DnsDatabase.Bind(command, ("@id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapRow(reader) : null;
    });

    public DnsRoute Create(string pattern, int presetId) => _database.Query(connection =>
    {
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO dns_routes (pattern, preset_id, enabled, position, created_utc)
                VALUES (@pattern, @presetId, 1,
                        COALESCE((SELECT MAX(position) FROM dns_routes), 0) + 1,
                        @now);
                """;
            DnsDatabase.Bind(insert,
                ("@pattern", pattern.Trim().ToLowerInvariant()),
                ("@presetId", presetId),
                ("@now", DateTime.UtcNow.ToString("O")));
            insert.ExecuteNonQuery();
        }

        var id = DnsDatabase.Scalar(connection, "SELECT last_insert_rowid();");
        return new DnsRoute
        {
            Id = (int)id,
            Pattern = pattern.Trim().ToLowerInvariant(),
            PresetId = presetId,
            Enabled = true,
            Position = (int)id,
        };
    });

    public bool Update(int id, string pattern, int presetId, bool enabled) => _database.Query(connection =>
        DnsDatabase.ExecuteRows(connection, """
            UPDATE dns_routes
            SET pattern = @pattern, preset_id = @presetId, enabled = @enabled
            WHERE id = @id;
            """,
            ("@pattern", pattern.Trim().ToLowerInvariant()),
            ("@presetId", presetId),
            ("@enabled", enabled ? 1 : 0),
            ("@id", id)) > 0);

    public bool Delete(int id) => _database.Query(connection =>
        DnsDatabase.ExecuteRows(connection, "DELETE FROM dns_routes WHERE id = @id;", ("@id", id)) > 0);

    public bool PatternExists(string pattern, int excludeId = 0) => _database.Query(connection =>
        DnsDatabase.Scalar(connection,
            "SELECT COUNT(1) FROM dns_routes WHERE pattern = @pattern AND id <> @exclude;",
            ("@pattern", pattern.Trim().ToLowerInvariant()),
            ("@exclude", excludeId)) > 0);

    private static DnsRoute MapRow(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Pattern = reader.GetString(1),
        PresetId = reader.GetInt32(2),
        Enabled = reader.GetInt64(3) != 0,
        Position = reader.GetInt32(4),
    };
}
