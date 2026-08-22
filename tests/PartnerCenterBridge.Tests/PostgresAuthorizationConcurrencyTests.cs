using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// PostgreSQL-only proof for the FOR UPDATE invariant. The ordinary suite returns immediately
/// unless PCB_TEST_POSTGRES points at a disposable server; the release gate runs this explicitly.
/// </summary>
public class PostgresAuthorizationConcurrencyTests
{
    [Fact]
    public async Task Concurrent_first_registrations_create_exactly_one_administrator()
    {
        var server = Environment.GetEnvironmentVariable("PCB_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server)) return;

        var databaseName = "pcb_bootstrap_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(server) { Database = "postgres" };
        await CreateDatabaseAsync(adminBuilder.ConnectionString, databaseName);
        var testBuilder = new NpgsqlConnectionStringBuilder(server) { Database = databaseName };
        var options = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseNpgsql(testBuilder.ConnectionString).Options;
        try
        {
            await using (var migrate = new BridgeDbContext(options))
                await migrate.Database.MigrateAsync();

            var first = RegisterAsync(options, "first@example.com");
            var second = RegisterAsync(options, "second@example.com");
            await Task.WhenAll(first, second);

            await using var verify = new BridgeDbContext(options);
            Assert.Equal(2, await verify.AppUsers.CountAsync());
            Assert.Equal(1, await verify.AppUsers.CountAsync(user => user.IsActive
                && (user.InstanceRoles & InstanceRole.Administrator) != 0));
        }
        finally
        {
            await DropDatabaseAsync(adminBuilder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_demotions_leave_one_active_administrator()
    {
        var server = Environment.GetEnvironmentVariable("PCB_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server)) return;

        var databaseName = "pcb_rbac_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(server) { Database = "postgres" };
        await using (var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", adminConnection);
            await create.ExecuteNonQueryAsync();
        }

        var testBuilder = new NpgsqlConnectionStringBuilder(server) { Database = databaseName };
        var options = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseNpgsql(testBuilder.ConnectionString).Options;
        try
        {
            await using (var setup = new BridgeDbContext(options))
            {
                await setup.Database.MigrateAsync();
                setup.AppUsers.AddRange(NewAdmin("one@example.com"), NewAdmin("two@example.com"));
                await setup.SaveChangesAsync();
            }

            Guid firstId;
            Guid secondId;
            await using (var read = new BridgeDbContext(options))
            {
                var users = await read.AppUsers.OrderBy(user => user.Email).ToListAsync();
                firstId = users[0].Id;
                secondId = users[1].Id;
            }

            var first = DemoteAsync(options, firstId);
            var second = DemoteAsync(options, secondId);
            var results = await Task.WhenAll(first, second);

            Assert.Single(results, result => result is OkObjectResult);
            Assert.Single(results, result => result is ConflictObjectResult);
            await using var verify = new BridgeDbContext(options);
            Assert.Equal(1, await verify.AppUsers.CountAsync(user => user.IsActive
                && (user.InstanceRoles & InstanceRole.Administrator) != 0));
        }
        finally
        {
            await DropDatabaseAsync(adminBuilder.ConnectionString, databaseName);
        }
    }

    private static async Task RegisterAsync(DbContextOptions<BridgeDbContext> options, string email)
    {
        await using var db = new BridgeDbContext(options);
        var authOptions = Options.Create(new LocalAuthOptions
        {
            SigningKey = "postgres-concurrency-test-signing-key-32-bytes",
            MinPasswordLength = 12
        });
        var tokens = new LocalTokenService(authOptions);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new AuthController(
            db, authOptions, new AuthModeInfo(AuthModeInfo.Local), new ChallengeCache(cache),
            new AuthResponseFactory(db, tokens));
        var result = await controller.Register(
            new RegisterRequest(email, "correct horse battery staple", email), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    private static async Task CreateDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task<IActionResult?> DemoteAsync(
        DbContextOptions<BridgeDbContext> options, Guid targetId)
    {
        await using var db = new BridgeDbContext(options);
        var version = await db.AppUsers.Where(user => user.Id == targetId)
            .Select(user => user.AuthorizationVersion).SingleAsync();
        var controller = new InstanceAccessController(db, new NonLocalAdministratorAccess());
        var result = await controller.ReplaceRoles(targetId,
            new InstanceAccessController.ReplaceInstanceRolesRequest([], version),
            CancellationToken.None);
        return result.Result;
    }

    private static AppUser NewAdmin(string email) => new()
    {
        Email = email,
        DisplayName = email,
        PasswordHash = "hash",
        InstanceRoles = InstanceRole.Administrator
    };

    private sealed class NonLocalAdministratorAccess : IInstanceAccessService
    {
        public Guid? CurrentUserId => null;
        public Task<InstanceRole> GetRolesAsync(CancellationToken ct) => Task.FromResult(InstanceRole.Administrator);
        public Task<bool> HasPermissionAsync(InstancePermission permission, CancellationToken ct) => Task.FromResult(true);
    }
}
