using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PartnerCenterBridge.Data.Sqlite;

/// <summary>
/// Design-time factory for the SQLite migration set. Used when this project is the <c>dotnet ef</c>
/// startup project (see the csproj for the command); scaffolding only needs the provider, no file is
/// opened.
/// </summary>
public class SqliteBridgeDbContextFactory : IDesignTimeDbContextFactory<BridgeDbContext>
{
    public BridgeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite("Data Source=pcb-design-time.db",
                sqlite => sqlite.MigrationsAssembly(SqliteBridgePersistence.MigrationsAssembly))
            .Options;
        return new BridgeDbContext(options);
    }
}
