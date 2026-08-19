namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// A mutating MCP tool call staged instead of executed, because its tenant is in the default
/// McpApprovalMode.Queue mode. Approving it (PendingActionsController.Approve) runs the same
/// service call a direct REST controller action would -- this record only carries the request
/// through to that point, it never carries its own copy of the orchestration logic.
/// </summary>
public class PendingAction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Matches an IPendingActionExecutor.ActionType, e.g. "workflow.remediate".</summary>
    public required string ActionType { get; set; }

    public Guid RequestedByUserId { get; set; }

    /// <summary>The staged tool call's arguments, serialized so the matching IPendingActionExecutor can deserialize and act on approval.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>Human-readable summary an operator reads before approving -- built from the same read/diagnose path the mutation itself would use.</summary>
    public required string PreviewSummary { get; set; }

    public PendingActionStatus Status { get; set; } = PendingActionStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Stale proposals can't be approved long after the LLM that requested them lost context on why.</summary>
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(24);

    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? ExecutionError { get; set; }
}
