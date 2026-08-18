using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// An in-memory Sqlite-backed BridgeDbContext for tests that need real EF Core behavior
/// (querying, FindAsync, SaveChanges) without a Postgres instance. The connection must stay open
/// for the context's lifetime -- Sqlite's ":memory:" database is destroyed when its one
/// connection closes.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    public BridgeDbContext Context { get; }

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite(_connection)
            .Options;
        Context = new BridgeDbContext(options);
        Context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
