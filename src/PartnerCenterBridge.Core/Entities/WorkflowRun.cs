using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// Audit record of a single workflow diagnose or remediate run: who ran what against which
/// tenant, with which inputs, and the full findings/steps exactly as shown to the operator.
/// This is what turns a workflow run into a ticket-ready paper trail.
/// </summary>
public class WorkflowRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string WorkflowId { get; set; } = "";
    public string WorkflowName { get; set; } = "";

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public WorkflowRunKind Kind { get; set; }

    /// <summary>Operator identity (name claim) that triggered the run; "anonymous" if unauthenticated.</summary>
    public string Operator { get; set; } = "";

    public Dictionary<string, string> Inputs { get; set; } = new();

    /// <summary>Diagnosis findings (for remediate runs, the post-fix re-diagnosis).</summary>
    public List<Finding> Findings { get; set; } = new();

    /// <summary>Remediation steps taken (empty for diagnose runs).</summary>
    public List<ProvisioningStep> Steps { get; set; } = new();

    /// <summary>False when the run threw or any remediation step failed.</summary>
    public bool Succeeded { get; set; }

    /// <summary>Health of the (post-)diagnosis, when one was produced.</summary>
    public bool? Healthy { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public long DurationMs { get; set; }
}
