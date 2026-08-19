using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Api.Services;

/// <summary>
/// Runs the real mutation a PendingAction was staged for, once a human approves it. One
/// implementation per ActionType, resolved by PendingActionsController from all registered
/// IPendingActionExecutor instances -- this is the seam later tool-parity work adds to, not
/// PendingActionService itself.
/// </summary>
public interface IPendingActionExecutor
{
    string ActionType { get; }
    Task ExecuteAsync(PendingAction action, CancellationToken ct);
}
