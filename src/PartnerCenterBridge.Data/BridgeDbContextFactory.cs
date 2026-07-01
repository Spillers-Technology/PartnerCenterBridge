using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PartnerCenterBridge.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> so migrations can be scaffolded without booting
/// the API host. The connection string is only used to pick the Npgsql provider; no DB is touched.
/// </summary>
public class BridgeDbContextFactory : IDesignTimeDbContextFactory<BridgeDbContext>
{
    public BridgeDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("PCB_POSTGRES")
                   ?? "Host=localhost;Port=5432;Database=pcbridge;Username=pcbridge;Password=pcbridge";
        var options = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseNpgsql(conn)
            .Options;
        return new BridgeDbContext(options);
    }
}
