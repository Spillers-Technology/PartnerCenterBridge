using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Core.Operations;

/// <summary>
/// Final outcome of an operation run. Always derived from post-change verification (see
/// <see cref="OutcomeRules"/>), never from HTTP success alone.
/// </summary>
public enum Outcome
{
    Succeeded,
    PartiallySucceeded,
    Failed,
    NoChangeNeeded,
    VerificationFailed,
    /// <summary>A plan (or read-only diagnosis) was produced; nothing was applied.</summary>
    Planned,
    /// <summary>
    /// Changes were applied and acknowledged by Microsoft but could not be independently verified:
    /// every attempted change succeeded, none failed verification, and at least one change has no
    /// read-back that can confirm it (for example a password value, or a device retire still pending).
    /// </summary>
    CompletedUnverified
}

/// <summary>The object an operation is about (usually a user).</summary>
public class OperationTarget
{
    public string Kind { get; set; } = "user";
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    public OperationTarget() { }
    public OperationTarget(string kind, string id, string displayName)
    {
        Kind = kind;
        Id = id;
        DisplayName = displayName;
    }
}

/// <summary>The tenant an operation ran against: PCB's own id, its name, and the Entra tenant id.</summary>
public class OperationTenantRef
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string TenantId { get; set; } = "";
}

/// <summary>
/// One candidate change in an <see cref="OperationPlan"/>. Ineligible items are still listed, with
/// <see cref="Reason"/> saying why PCB will not touch them -- nothing is silently dropped.
/// </summary>
public class PlanItem
{
    /// <summary>Stable id within the plan (e.g. <c>group:{objectId}</c>); the apply call selects by it.</summary>
    public string Id { get; set; } = "";
    public string Action { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public bool Destructive { get; set; }
    public bool Eligible { get; set; }
    public string Category { get; set; } = "";
    /// <summary>Why the item is ineligible, or a note about how/when it runs.</summary>
    public string? Reason { get; set; }
}

/// <summary>What an operation would do, computed from live state without changing anything.</summary>
public class OperationPlan
{
    public string OperationId { get; set; } = "";
    public string OperationName { get; set; } = "";
    /// <summary>PCB tenant id (the registry Guid, as used in routes).</summary>
    public string TenantId { get; set; } = "";
    public OperationTarget Target { get; set; } = new();
    public List<Finding> Preflight { get; set; } = new();
    public List<PlanItem> Items { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
}

/// <summary>What happened to one plan item during apply.</summary>
public class ChangeResult
{
    public string PlanItemId { get; set; } = "";
    public string Action { get; set; } = "";
    public string ObjectName { get; set; } = "";
    /// <summary>False when the item was skipped (already done, ineligible, or gated).</summary>
    public bool Attempted { get; set; }
    /// <summary>
    /// The change was reported as done (or was already in place). This is the service's report,
    /// not proof -- see the matching <see cref="VerificationCheck"/>.
    /// </summary>
    public bool Succeeded { get; set; }
    public string? Detail { get; set; }
}

/// <summary>A post-change check that re-read live state.</summary>
public class VerificationCheck
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string? Detail { get; set; }

    /// <summary>
    /// The plan item (change) this check verifies, e.g. <c>group:{id}</c> or <c>step-2</c>. Null for
    /// whole-run checks and for records written before checks were linked to items.
    /// </summary>
    public string? PlanItemId { get; set; }

    /// <summary>
    /// The change was acknowledged but nothing PCB can read back confirms it (yet). Such a check is
    /// neither passed nor failed: <see cref="Passed"/> is false and the outcome is at best
    /// <see cref="Outcome.CompletedUnverified"/>, never <see cref="Outcome.Succeeded"/>.
    /// </summary>
    public bool Unverifiable { get; set; }

    public VerificationCheck() { }
    public VerificationCheck(string name, bool passed, string? detail = null, string? planItemId = null)
    {
        Name = name;
        Passed = passed;
        Detail = detail;
        PlanItemId = planItemId;
    }

    /// <summary>A change that was acknowledged but cannot be confirmed by a read-back.</summary>
    public static VerificationCheck NotVerifiable(string name, string detail, string? planItemId = null) =>
        new(name, false, detail, planItemId) { Unverifiable = true };
}

/// <summary>
/// The durable record of an operation run: what was checked, planned, changed and verified, plus
/// ticket-ready notes generated from those facts. Never contains secrets (temporary passwords,
/// tokens): show-once values stay in <see cref="WorkflowRunResult.Ephemeral"/>.
/// </summary>
public class OperationEvidence
{
    public string RunId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string OperationName { get; set; } = "";
    public OperationTenantRef Tenant { get; set; } = new();
    public OperationTarget? Target { get; set; }
    public string Operator { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public Outcome Outcome { get; set; }
    public List<Finding> Preflight { get; set; } = new();
    public List<PlanItem> Plan { get; set; } = new();
    public List<ChangeResult> Changes { get; set; } = new();
    public List<VerificationCheck> Verification { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
    public List<string> Failures { get; set; } = new();
    public string TicketNotes { get; set; } = "";
}

/// <summary>Raised when operation inputs are invalid in a way the caller must fix (maps to HTTP 400).</summary>
public class OperationInputException(string message) : Exception(message);

/// <summary>
/// An apply was cut short (request cancelled, or an unexpected error) after it may already have
/// changed something. <see cref="Partial"/> records the completed changes and marks the rest as
/// interrupted, so callers persist it rather than a bare failure.
/// </summary>
public class OperationInterruptedException(OperationEvidence partial, Exception inner)
    : Exception($"The operation was interrupted after partial changes: {inner.Message}", inner)
{
    public OperationEvidence Partial { get; } = partial;
}
