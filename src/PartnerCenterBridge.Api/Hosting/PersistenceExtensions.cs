using Microsoft.EntityFrameworkCore;
using Npgsql;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Data;
using PartnerCenterBridge.Data.Sqlite;

namespace PartnerCenterBridge.Api.Hosting;

public static class PersistenceProviders
{
    public const string Postgres = "Postgres";
    public const string Sqlite = "Sqlite";
}

/// <summary>The active store, for diagnostics. <see cref="Target"/> never contains a password.</summary>
public sealed record PersistenceInfo(string Provider, string Target, string? DatabaseFilePath);

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers <see cref="BridgeDbContext"/> for <c>Persistence:Provider</c> (Postgres, the
    /// default, or Sqlite). This is the only place the provider is chosen; everything else just
    /// injects the context.
    /// </summary>
    public static IServiceCollection AddBridgePersistence(this IServiceCollection services, IConfiguration cfg)
    {
        var provider = cfg["Persistence:Provider"];
        PersistenceInfo info;
        if (string.IsNullOrWhiteSpace(provider) || provider.Equals(PersistenceProviders.Postgres, StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = cfg.GetConnectionString("Postgres");
            info = new PersistenceInfo(PersistenceProviders.Postgres, DescribePostgres(connectionString), null);
            services.AddDbContext<BridgeDbContext>((sp, o) =>
                o.UseNpgsql(connectionString)
                 .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
        }
        else if (provider.Equals(PersistenceProviders.Sqlite, StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = cfg.GetConnectionString("Sqlite");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Persistence:Provider=Sqlite needs ConnectionStrings:Sqlite (e.g. \"Data Source=pcb.db\").");
            var path = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
            var fullPath = string.IsNullOrEmpty(path) || path == ":memory:" ? null : Path.GetFullPath(path);
            info = new PersistenceInfo(PersistenceProviders.Sqlite, $"SQLite at {fullPath ?? path}", fullPath);
            services.AddDbContext<BridgeDbContext>((sp, o) =>
                o.UseBridgeSqlite(connectionString)
                 .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown Persistence:Provider '{provider}'. Expected '{PersistenceProviders.Postgres}' or '{PersistenceProviders.Sqlite}'.");
        }

        services.AddSingleton(info);
        return services;
    }

    /// <summary>
    /// Applies the active provider's migrations, then refuses to start a Local-auth instance whose
    /// registered users include no active Administrator.
    /// </summary>
    public static async Task MigrateBridgeDatabaseAsync(this WebApplication app, string authMode)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        await db.Database.MigrateAsync();
        if (authMode == AuthModeInfo.Local
            && await db.AppUsers.AnyAsync()
            && !await db.AppUsers.AnyAsync(user => user.IsActive
                && (user.InstanceRoles & PartnerCenterBridge.Core.InstanceRole.Administrator) != 0))
        {
            throw new InvalidOperationException(
                "Local authentication has registered users but no active Administrator. Restore an Administrator before starting the service.");
        }
    }

    private static string DescribePostgres(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "PostgreSQL (ConnectionStrings:Postgres is not set)";
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"PostgreSQL at {builder.Host}:{builder.Port}/{builder.Database}";
        }
        catch (ArgumentException)
        {
            return "PostgreSQL (unparseable connection string)";
        }
    }
}
