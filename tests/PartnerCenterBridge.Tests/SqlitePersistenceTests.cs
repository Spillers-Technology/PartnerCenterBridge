using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;
using PartnerCenterBridge.Data.Sqlite;

namespace PartnerCenterBridge.Tests;

/// <summary>The Local Workbench SQLite store: its own migration set, generated from the shared model.</summary>
public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pcb-sqlite-" + Guid.NewGuid().ToString("N"));
    private readonly DbContextOptions<BridgeDbContext> _options;

    public SqlitePersistenceTests()
    {
        var builder = new DbContextOptionsBuilder<BridgeDbContext>();
        builder.UseBridgeSqlite(SqliteBridgePersistence.ConnectionStringForFile(Path.Combine(_directory, "pcb.db")));
        _options = builder.Options;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Migrations_apply_cleanly_to_a_fresh_file_database()
    {
        await using (var db = new BridgeDbContext(_options))
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), id => id.EndsWith("_InitialCreate"));
            Assert.Equal("wal", await ScalarAsync(db, "PRAGMA journal_mode;"));

            db.Tenants.Add(new Tenant { TenantId = "t", DisplayName = "Contoso" });
            db.WorkflowRuns.Add(new WorkflowRun
            {
                WorkflowId = "w", WorkflowName = "W", Operator = "op",
                Tenant = new Tenant { TenantId = "t2", DisplayName = "Fabrikam" },
                Inputs = new() { ["identity"] = "ada@contoso.com" }
            });
            await db.SaveChangesAsync();
        }

        // Re-running is a no-op and the data (including JSON columns) round-trips.
        await using var again = new BridgeDbContext(_options);
        await again.Database.MigrateAsync();
        Assert.Equal(2, await again.Tenants.CountAsync());
        var run = await again.WorkflowRuns.SingleAsync();
        Assert.Equal("ada@contoso.com", run.Inputs["identity"]);
        Assert.Single(await again.InstanceAuthorizationStates.ToListAsync());
    }

    [Fact]
    public void Sqlite_model_has_no_pending_changes()
    {
        using var db = new BridgeDbContext(_options);
        Assert.False(HasPendingModelChanges(db));
    }

    [Fact]
    public void Postgres_model_has_no_pending_changes()
    {
        var options = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseNpgsql("Host=localhost;Database=pcb_model_check").Options;
        using var db = new BridgeDbContext(options);
        Assert.False(HasPendingModelChanges(db));
    }

    [Fact]
    public void Only_postgres_maps_json_payloads_to_jsonb()
    {
        using var postgres = new BridgeDbContext(new DbContextOptionsBuilder<BridgeDbContext>()
            .UseNpgsql("Host=localhost;Database=pcb_model_check").Options);
        using var sqlite = new BridgeDbContext(_options);

        static string? ColumnType(BridgeDbContext db) =>
            db.Model.FindEntityType(typeof(WorkflowRun))!.FindProperty(nameof(WorkflowRun.Inputs))!.GetColumnType();

        Assert.Equal("jsonb", ColumnType(postgres));
        Assert.Equal("TEXT", ColumnType(sqlite));
    }

    [Fact]
    public async Task Pending_action_raw_sql_retry_claim_works_on_sqlite()
    {
        await using (var setup = new BridgeDbContext(_options)) await setup.Database.MigrateAsync();
        await using var db = new BridgeDbContext(_options);
        var tenant = new Tenant { TenantId = "t", DisplayName = "Contoso" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var service = new PendingActionService(db, new FixedActor());
        var staged = await service.StageAsync(tenant.Id, "test", Guid.NewGuid(), new { }, "preview", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(staged.Id, Guid.NewGuid(),
            _ => throw new InvalidOperationException("boom"), CancellationToken.None));
        var retried = await service.RetryAsync(staged.Id, _ => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(PendingActionStatus.Executed, retried.Status);
        Assert.Null(retried.ExecutionError);
    }

    [Fact]
    public async Task Authorization_lock_holds_the_sqlite_write_lock_until_commit()
    {
        await using (var setup = new BridgeDbContext(_options)) await setup.Database.MigrateAsync();
        await using var db = new BridgeDbContext(_options);

        await using (var held = await InstanceAuthorizationLock.AcquireAsync(db, CancellationToken.None))
        {
            // A second writer cannot even begin while the lock is held.
            await using var other = new SqliteConnection(db.Database.GetConnectionString() + ";Pooling=False");
            await other.OpenAsync();
            await using (var pragma = other.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=100;";
                await pragma.ExecuteNonQueryAsync();
            }
            var begin = other.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            var error = await Assert.ThrowsAsync<SqliteException>(() => begin.ExecuteNonQueryAsync());
            Assert.Equal(5, error.SqliteErrorCode); // SQLITE_BUSY
            await held.CommitAsync(CancellationToken.None);
        }
    }

    private static async Task<string?> ScalarAsync(BridgeDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    /// <summary>EF 8 equivalent of <c>dotnet ef migrations has-pending-model-changes</c>.</summary>
    private static bool HasPendingModelChanges(BridgeDbContext db)
    {
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot
            ?? throw new InvalidOperationException("No model snapshot in the migrations assembly.");
        var snapshotModel = snapshot.Model is IMutableModel mutable ? mutable.FinalizeModel() : snapshot.Model;
        snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel);
        return db.GetService<IMigrationsModelDiffer>().HasDifferences(
            snapshotModel.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());
    }

    private sealed class FixedActor : PartnerCenterBridge.Core.Abstractions.ICurrentActor
    {
        public Guid? UserId => null;
        public string Name => "test";
    }
}
