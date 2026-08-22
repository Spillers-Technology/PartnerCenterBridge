using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>Locks one tenant while its access grants are changed, preserving the last-Owner invariant.</summary>
public sealed class TenantAuthorizationLock : IAsyncDisposable
{
    private readonly IDbContextTransaction _transaction;
    public Tenant Tenant { get; }

    private TenantAuthorizationLock(IDbContextTransaction transaction, Tenant tenant)
    {
        _transaction = transaction;
        Tenant = tenant;
    }

    public static async Task<TenantAuthorizationLock?> AcquireAsync(
        BridgeDbContext db, Guid tenantId, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var tenant = db.Database.IsNpgsql()
                ? await db.Tenants.FromSqlRaw(
                    "SELECT * FROM \"Tenants\" WHERE \"Id\" = {0} FOR UPDATE", tenantId)
                    .SingleOrDefaultAsync(ct)
                : await db.Tenants.SingleOrDefaultAsync(candidate => candidate.Id == tenantId, ct);
            if (tenant is null)
            {
                await transaction.DisposeAsync();
                return null;
            }
            return new TenantAuthorizationLock(transaction, tenant);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public Task CommitAsync(CancellationToken ct) => _transaction.CommitAsync(ct);
    public ValueTask DisposeAsync() => _transaction.DisposeAsync();
}
