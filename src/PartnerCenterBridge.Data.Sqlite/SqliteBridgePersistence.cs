using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PartnerCenterBridge.Data.Sqlite;

/// <summary>
/// Configures <see cref="BridgeDbContext"/> for the SQLite store used by the Local Workbench.
/// Migrations come from this assembly, not PartnerCenterBridge.Data (which holds the Postgres set).
/// </summary>
public static class SqliteBridgePersistence
{
    public const string MigrationsAssembly = "PartnerCenterBridge.Data.Sqlite";

    /// <summary>How long a writer waits on SQLite's single write lock before failing with SQLITE_BUSY.</summary>
    public const int BusyTimeoutMilliseconds = 5000;

    public static DbContextOptionsBuilder UseBridgeSqlite(this DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(MigrationsAssembly))
            .AddInterceptors(SqlitePragmaInterceptor.Instance);

    /// <summary>Builds a connection string for a database file, creating its directory if needed.</summary>
    public static string ConnectionStringForFile(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
    }
}

/// <summary>
/// Applies per-connection pragmas every time EF opens a connection: WAL so readers never block the
/// single writer, a busy timeout so concurrent writers queue instead of failing immediately, and
/// foreign keys (off by default in SQLite) so cascades match the Postgres behavior.
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public static readonly SqlitePragmaInterceptor Instance = new();

    private static readonly string Pragmas =
        "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; " +
        $"PRAGMA busy_timeout={SqliteBridgePersistence.BusyTimeoutMilliseconds};";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (!IsFileDatabase(connection)) return;
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (!IsFileDatabase(connection)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // WAL is meaningless (and rejected) for in-memory databases.
    private static bool IsFileDatabase(DbConnection connection) =>
        connection is SqliteConnection sqlite
        && !string.IsNullOrEmpty(sqlite.DataSource)
        && !sqlite.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
        && !sqlite.ConnectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase);
}
