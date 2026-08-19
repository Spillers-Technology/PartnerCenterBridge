using System.Text.Json;
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
        await ExpireIfStaleAsync(await _db.PendingActions.FindAsync([id], ct), ct);

    /// <summary>
    /// Marks Approved, runs <paramref name="execute"/>, then marks Executed -- or records
    /// <see cref="PendingAction.ExecutionError"/> and rethrows if it fails. The caller (Task 6's
    /// controller) supplies <paramref name="execute"/> so this service never itself knows how to
    /// run any specific ActionType.
    /// </summary>
    public async Task<PendingAction> ApproveAsync(Guid id, Guid decidedByUserId, Func<PendingAction, Task> execute, CancellationToken ct)
    {
        var action = await RequireActionableAsync(id, ct);
        action.Status = PendingActionStatus.Approved;
        action.DecidedByUserId = decidedByUserId;
        action.DecidedAt = DateTimeOffset.UtcNow;
        try
        {
            await execute(action);
            action.Status = PendingActionStatus.Executed;
            action.ExecutedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            action.ExecutionError = ex.Message;
            throw;
        }
        finally
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        return action;
    }

    public async Task<PendingAction> RejectAsync(Guid id, Guid decidedByUserId, CancellationToken ct)
    {
        var action = await RequireActionableAsync(id, ct);
        action.Status = PendingActionStatus.Rejected;
        action.DecidedByUserId = decidedByUserId;
        action.DecidedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return action;
    }

    private async Task<PendingAction> RequireActionableAsync(Guid id, CancellationToken ct)
    {
        var action = await ExpireIfStaleAsync(await _db.PendingActions.FindAsync([id], ct), ct)
            ?? throw new InvalidOperationException("Pending action not found.");
        if (action.Status != PendingActionStatus.Pending)
            throw new InvalidOperationException($"Pending action is {action.Status}, not Pending.");
        return action;
    }

    private async Task<PendingAction?> ExpireIfStaleAsync(PendingAction? action, CancellationToken ct)
    {
        if (action is { Status: PendingActionStatus.Pending } && action.ExpiresAt < DateTimeOffset.UtcNow)
        {
            action.Status = PendingActionStatus.Expired;
            await _db.SaveChangesAsync(ct);
        }
        return action;
    }
}
