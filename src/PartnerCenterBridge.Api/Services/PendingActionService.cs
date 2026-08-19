using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Services;

public class PendingActionService
{
    private readonly BridgeDbContext _db;

    public PendingActionService(BridgeDbContext db) => _db = db;

    public async Task<PendingAction> StageAsync(
        Guid tenantId, string actionType, Guid requestedByUserId, object payload, string previewSummary, CancellationToken ct)
    {
        var action = new PendingAction
        {
            TenantId = tenantId,
            ActionType = actionType,
            RequestedByUserId = requestedByUserId,
            PayloadJson = JsonSerializer.Serialize(payload),
            PreviewSummary = previewSummary
        };
        _db.PendingActions.Add(action);
        await _db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<PendingAction?> GetAsync(Guid id, CancellationToken ct) =>
        await GetWithAtomicExpiryAsync(id, ct);

    /// <summary>
    /// Marks Approved, runs <paramref name="execute"/>, then marks Executed -- or records
    /// <see cref="PendingAction.ExecutionError"/> and rethrows if it fails. The caller (Task 6's
    /// controller) supplies <paramref name="execute"/> so this service never itself knows how to
    /// run any specific ActionType.
    /// </summary>
    public async Task<PendingAction> ApproveAsync(Guid id, Guid decidedByUserId, Func<PendingAction, Task> execute, CancellationToken ct)
    {
        var decidedAt = DateTimeOffset.UtcNow;
        var claimed = await ClaimAsync(
            id, PendingActionStatus.Approved, decidedByUserId, decidedAt, ct);

        if (claimed == 0)
            await ThrowNotActionableAsync(id, ct);

        // Database-side updates bypass the change tracker. Reload without tracking both to get
        // the claimed values and to avoid returning a stale instance left over from StageAsync.
        var action = await _db.PendingActions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == id, CancellationToken.None);
        try
        {
            await execute(action);
        }
        catch (Exception ex)
        {
            await _db.PendingActions
                .Where(candidate => candidate.Id == id && candidate.Status == PendingActionStatus.Approved)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.ExecutionError, ex.Message), CancellationToken.None);
            action.ExecutionError = ex.Message;
            throw;
        }

        var executedAt = DateTimeOffset.UtcNow;
        await _db.PendingActions
            .Where(candidate => candidate.Id == id && candidate.Status == PendingActionStatus.Approved)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, PendingActionStatus.Executed)
                .SetProperty(candidate => candidate.ExecutedAt, executedAt), CancellationToken.None);
        action.Status = PendingActionStatus.Executed;
        action.ExecutedAt = executedAt;
        return action;
    }

    public async Task<PendingAction> RejectAsync(Guid id, Guid decidedByUserId, CancellationToken ct)
    {
        var decidedAt = DateTimeOffset.UtcNow;
        var claimed = await ClaimAsync(
            id, PendingActionStatus.Rejected, decidedByUserId, decidedAt, ct);

        if (claimed == 0)
            await ThrowNotActionableAsync(id, ct);

        return await _db.PendingActions.AsNoTracking()
            .SingleAsync(action => action.Id == id, CancellationToken.None);
    }

    private async Task ThrowNotActionableAsync(Guid id, CancellationToken ct)
    {
        var action = await GetWithAtomicExpiryAsync(id, ct)
            ?? throw new InvalidOperationException("Pending action not found.");
        throw new InvalidOperationException($"Pending action is {action.Status}, not Pending.");
    }

    private async Task<PendingAction?> GetWithAtomicExpiryAsync(Guid id, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""PendingActions""
            SET ""Status"" = {(int)PendingActionStatus.Expired}
            WHERE ""Id"" = {id}
              AND ""Status"" = {(int)PendingActionStatus.Pending}
              AND ""ExpiresAt"" < {now}", ct);

        return await _db.PendingActions.AsNoTracking()
            .SingleOrDefaultAsync(action => action.Id == id, ct);
    }

    private Task<int> ClaimAsync(
        Guid id,
        PendingActionStatus claimedStatus,
        Guid decidedByUserId,
        DateTimeOffset decidedAt,
        CancellationToken ct) =>
        _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""PendingActions""
            SET ""Status"" = {(int)claimedStatus},
                ""DecidedByUserId"" = {decidedByUserId},
                ""DecidedAt"" = {decidedAt}
            WHERE ""Id"" = {id}
              AND ""Status"" = {(int)PendingActionStatus.Pending}
              AND ""ExpiresAt"" >= {decidedAt}", ct);
}
