using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>Database-backed serialization point for bootstrap and instance-role replacement.</summary>
public sealed class InstanceAuthorizationLock : IAsyncDisposable
{
    private readonly IDbContextTransaction _transaction;
    public InstanceAuthorizationState State { get; }

    private InstanceAuthorizationLock(IDbContextTransaction transaction, InstanceAuthorizationState state)
    {
        _transaction = transaction;
        State = state;
    }

    public static async Task<InstanceAuthorizationLock> AcquireAsync(BridgeDbContext db, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var state = db.Database.IsNpgsql()
                ? await db.InstanceAuthorizationStates
                    .FromSqlRaw("SELECT * FROM \"InstanceAuthorizationStates\" WHERE \"Id\" = 1 FOR UPDATE")
                    .SingleAsync(ct)
                : await db.InstanceAuthorizationStates.SingleAsync(state => state.Id == 1, ct);
            return new InstanceAuthorizationLock(transaction, state);
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
