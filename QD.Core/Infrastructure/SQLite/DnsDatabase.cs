using Microsoft.Data.Sqlite;

namespace QD.Core.Infrastructure.SQLite;

/// <summary>
/// Owns the SQLite preset store: schema, migrations and connection management.
///
/// Design notes (v2.1 redesign):
/// - short-lived pooled connections instead of one long-lived connection, so the store
///   never holds a file lock while the app idles and multiple instances coexist;
/// - WAL journaling + a busy timeout, so a crash or a concurrent reader cannot wedge it;
/// - explicit schema with <c>PRAGMA user_version</c> migrations, including a one-time
///   import of the legacy QuickDNS 1.x/2.0 <c>Dns</c> table;
/// - a damaged store is quarantined (renamed) and recreated — startup must never fail
///   because of the database.
/// </summary>
public sealed class DnsDatabase
{
    private const int CurrentSchemaVersion = 1;

    // Corruption signatures: 11 = SQLITE_CORRUPT, 26 = SQLITE_NOTADB.
    // Everything else (busy, io, disk full) must NOT trigger a quarantine.
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS dns_presets (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            name          TEXT    NOT NULL COLLATE NOCASE,
            primary_dns   TEXT    NOT NULL,
            secondary_dns TEXT    NOT NULL DEFAULT '',
            created_utc   TEXT    NOT NULL,
            updated_utc   TEXT    NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_dns_presets_name ON dns_presets (name);
        """;

    private readonly string _dbPath;
    private readonly object _initLock = new();
    private bool _initialized;

    public DnsDatabase(string dbPath)
    {
        _dbPath = Path.GetFullPath(dbPath);

        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    public string DatabasePath => _dbPath;

    /// <summary>
    /// Opens/creates the store and runs pending migrations.
    /// Returns <c>true</c> when the previous store file was damaged and got quarantined
    /// (a fresh empty store was created in its place).
    /// </summary>
    public bool Initialize()
    {
        lock (_initLock)
        {
            if (_initialized)
                return false;

            try
            {
                using var connection = OpenConnection();
                Migrate(connection);
                _initialized = true;
                return false;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is (SqliteCorrupt or SqliteNotADatabase) && File.Exists(_dbPath))
            {
                Quarantine();
                using var connection = OpenConnection();
                Migrate(connection);
                _initialized = true;
                return true;
            }
        }
    }

    public T Query<T>(Func<SqliteConnection, T> action)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        return action(connection);
    }

    public void Query(Action<SqliteConnection> action)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        action(connection);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            Initialize();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            connection.DefaultTimeout = 3; // seconds; doubles as the SQLite busy timeout

            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=NORMAL;");
            Execute(connection, "PRAGMA foreign_keys=ON;");
            return connection;
        }
        catch
        {
            // Never leak a half-opened handle: it would keep the file locked and
            // block the corruption quarantine below.
            connection.Dispose();
            throw;
        }
    }

    private void Migrate(SqliteConnection connection)
    {
        var version = Convert.ToInt64(Scalar(connection, "PRAGMA user_version;"));
        if (version >= CurrentSchemaVersion)
            return;

        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, SchemaSql);

        if (TableExists(connection, transaction, "Dns"))
        {
            CopyLegacyDnsTable(connection, transaction);
            Execute(connection, transaction, "DROP TABLE Dns;");
        }

        Execute(connection, transaction, $"PRAGMA user_version = {CurrentSchemaVersion};");
        transaction.Commit();
    }

    /// <summary>Imports rows of the legacy sqlite-net table (column dates stored as ticks or text).</summary>
    private static void CopyLegacyDnsTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Name, string First, string Second, string? CreatedRaw, string? ModifiedRaw)>();

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT Id, Name, FirstDns, SecondDns, CreationDate, ModifiedDate FROM Dns ORDER BY Id;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.IsDBNull(1) ? string.Empty : reader.GetValue(1).ToString() ?? string.Empty,
                    reader.IsDBNull(2) ? string.Empty : reader.GetValue(2).ToString() ?? string.Empty,
                    reader.IsDBNull(3) ? string.Empty : reader.GetValue(3).ToString() ?? string.Empty,
                    reader.IsDBNull(4) ? null : reader.GetValue(4).ToString(),
                    reader.IsDBNull(5) ? null : reader.GetValue(5).ToString()));
            }
        }

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO dns_presets (name, primary_dns, secondary_dns, created_utc, updated_utc)
                VALUES (@name, @primary, @secondary, @created, @updated);
                """;
            insert.Parameters.AddWithValue("@name", row.Name);
            insert.Parameters.AddWithValue("@primary", row.First);
            insert.Parameters.AddWithValue("@secondary", row.Second);
            insert.Parameters.AddWithValue("@created", NormalizeTimestamp(row.CreatedRaw));
            insert.Parameters.AddWithValue("@updated", NormalizeTimestamp(row.ModifiedRaw));
            insert.ExecuteNonQuery();
        }
    }

    private static string NormalizeTimestamp(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            // sqlite-net tick storage (INTEGER) ...
            if (long.TryParse(raw, out var ticks) && ticks > 0)
            {
                try
                {
                    return DateTime.SpecifyKind(new DateTime(ticks), DateTimeKind.Local)
                        .ToUniversalTime()
                        .ToString("O");
                }
                catch
                {
                    // fall through to text/now handling
                }
            }

            // ... or a parseable text date (local, UTC or unspecified — assume local).
            if (DateTime.TryParse(raw, out var parsed))
            {
                var utc = parsed.Kind switch
                {
                    DateTimeKind.Utc => parsed,
                    DateTimeKind.Local => parsed.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(parsed, DateTimeKind.Local).ToUniversalTime(),
                };
                return utc.ToString("O");
            }
        }

        return DateTime.UtcNow.ToString("O");
    }

    private void Quarantine()
    {
        // Pooled connections (even failed ones) may still hold the file handle —
        // drop them all so the rename can succeed.
        SqliteConnection.ClearAllPools();

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        MoveIfExists(_dbPath, $"{_dbPath}.corrupt-{stamp}");
        MoveIfExists($"{_dbPath}-wal", $"{_dbPath}.corrupt-{stamp}-wal");
        MoveIfExists($"{_dbPath}-shm", $"{_dbPath}.corrupt-{stamp}-shm");
    }

    private static void MoveIfExists(string source, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(source))
                    File.Move(source, target, overwrite: false);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                // Antivirus/indexer occasionally holds files briefly; wait and retry.
                Thread.Sleep(50 + attempt * 50);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(50 + attempt * 50);
            }
        }
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    // ---- small SQL helpers shared with the repository ----------------------

    public static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        command.ExecuteNonQuery();
    }

    public static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static int ExecuteRows(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        return command.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    public static void Bind(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
