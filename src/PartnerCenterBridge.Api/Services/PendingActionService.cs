using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Services;

public class PendingActionService
{
    private readonly BridgeDbContext _db;
    private readonly ICurrentActor _actor;

    public PendingActionService(BridgeDbContext db, ICurrentActor actor)
    {
        _db = db;
        _actor = actor;
    }

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
    /// <see cref="PendingAction.ExecutionError"/> and rethrows if it fails. A failed execution is
    /// deliberately not auto-retried because an executor may not be idempotent. A human can explicitly
    /// retry a failed execution through <see cref="RetryAsync"/>. The caller (Task 6's controller)
    /// supplies <paramref name="execute"/> so this service never itself knows how to run any specific
    /// ActionType.
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
        await AuditTransitionAsync(action, "approved");
        try
        {
            await execute(action);
        }
        catch (Exception ex)
        {
            var updated = await _db.PendingActions
                .Where(candidate => candidate.Id == id && candidate.Status == PendingActionStatus.Approved)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.ExecutionError, ex.Message), CancellationToken.None);
            action.ExecutionError = ex.Message;
            if (updated != 0)
                await AuditTransitionAsync(action, $"execution failed: {ex.Message}");
            throw;
        }

        var executedAt = DateTimeOffset.UtcNow;
        var executed = await _db.PendingActions
            .Where(candidate => candidate.Id == id && candidate.Status == PendingActionStatus.Approved)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, PendingActionStatus.Executed)
                .SetProperty(candidate => candidate.ExecutedAt, executedAt), CancellationToken.None);
        action.Status = PendingActionStatus.Executed;
        action.ExecutedAt = executedAt;
        if (executed != 0)
            await AuditTransitionAsync(action, "executed");
        return action;
    }

    public async Task<PendingAction> RejectAsync(Guid id, Guid decidedByUserId, CancellationToken ct)
    {
        var decidedAt = DateTimeOffset.UtcNow;
        var claimed = await ClaimAsync(
            id, PendingActionStatus.Rejected, decidedByUserId, decidedAt, ct);

        if (claimed == 0)
            await ThrowNotActionableAsync(id, ct);

        var action = await _db.PendingActions.AsNoTracking()
            .SingleAsync(action => action.Id == id, CancellationToken.None);
        await AuditTransitionAsync(action, "rejected");
        return action;
    }

    /// <summary>
    /// Claims a previously failed approved action, re-runs its executor, then records either its
    /// successful execution or its replacement failure. Clearing the old error in the atomic claim
    /// prevents concurrent retry attempts from running the same executor twice.
    /// </summary>
    public async Task<PendingAction> RetryAsync(Guid id, Func<PendingAction, Task> execute, CancellationToken ct)
    {
        var claimed = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""PendingActions""
            SET ""ExecutionError"" = NULL
            WHERE ""Id"" = {id}
              AND ""Status"" = {(int)PendingActionStatus.Approved}
              AND ""ExecutionError"" IS NOT NULL", ct);

        if (claimed == 0)
            await ThrowNotRetryableAsync(id, ct);

        var action = await _db.PendingActions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == id, CancellationToken.None);
        await AuditTransitionAsync(action, "retried");
        try
        {
            await execute(action);
        }
        catch (Exception ex)
        {
            var updated = await _db.PendingActions
                .Where(candidate => candidate.Id == id
                                    && candidate.Status == PendingActionStatus.Approved
                                    && candidate.ExecutionError == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.ExecutionError, ex.Message), CancellationToken.None);
            action.ExecutionError = ex.Message;
            if (updated != 0)
                await AuditTransitionAsync(action, $"retried, failed again: {ex.Message}");
            throw;
        }

        var executedAt = DateTimeOffset.UtcNow;
        var executed = await _db.PendingActions
            .Where(candidate => candidate.Id == id
                                && candidate.Status == PendingActionStatus.Approved
                                && candidate.ExecutionError == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, PendingActionStatus.Executed)
                .SetProperty(candidate => candidate.ExecutedAt, executedAt), CancellationToken.None);
        action.Status = PendingActionStatus.Executed;
        action.ExecutedAt = executedAt;
        if (executed != 0)
            await AuditTransitionAsync(action, "retried, succeeded");
        return action;
    }

    private async Task ThrowNotActionableAsync(Guid id, CancellationToken ct)
    {
        var action = await GetWithAtomicExpiryAsync(id, ct)
            ?? throw new InvalidOperationException("Pending action not found.");
        throw new InvalidOperationException($"Pending action is {action.Status}, not Pending.");
    }

    private async Task ThrowNotRetryableAsync(Guid id, CancellationToken ct)
    {
        var action = await _db.PendingActions.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, ct)
            ?? throw new InvalidOperationException("Pending action not found.");
        throw new InvalidOperationException(
            $"Pending action is {action.Status} with {(action.ExecutionError is null ? "no execution error" : "an execution error")}, not an approved failed action.");
    }

    private async Task<PendingAction?> GetWithAtomicExpiryAsync(Guid id, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""PendingActions""
            SET ""Status"" = {(int)PendingActionStatus.Expired}
            WHERE ""Id"" = {id}
              AND ""Status"" = {(int)PendingActionStatus.Pending}
              AND ""ExpiresAt"" < {now}", ct);

        var action = await _db.PendingActions.AsNoTracking()
            .SingleOrDefaultAsync(action => action.Id == id, ct);
        if (expired != 0 && action is not null)
            await AuditTransitionAsync(action, "expired");
        return action;
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

    private async Task AuditTransitionAsync(PendingAction action, string detail)
    {
        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.EntityModified,
            EntityType = nameof(PendingAction),
            EntityId = action.Id.ToString(),
            TenantId = action.TenantId,
            ActorUserId = _actor.UserId,
            ActorName = _actor.Name,
            Detail = detail
        });
        await _db.SaveChangesAsync(CancellationToken.None);
    }
}
