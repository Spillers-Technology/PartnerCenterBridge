using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Workflows;

public enum FindingStatus { Ok, Info, Warning, Blocker }

/// <summary>One diagnostic observation about the target — surfaced verbatim for transparency.</summary>
public record Finding(string Name, FindingStatus Status, string? Detail = null);

/// <summary>The outcome of a workflow's diagnose pass.</summary>
public class DiagnosisResult
{
    public List<Finding> Findings { get; set; } = new();
    /// <summary>True when nothing needs fixing (only Ok/Info findings).</summary>
    public bool Healthy => Findings.All(f => f.Status is FindingStatus.Ok or FindingStatus.Info);
}

/// <summary>The outcome of a workflow's remediate pass: the steps taken plus a fresh diagnosis.</summary>
public class WorkflowRunResult
{
    public List<ProvisioningStep> Steps { get; set; } = new();
    public DiagnosisResult? PostState { get; set; }

    /// <summary>
    /// Show-once secrets for the operator (e.g. a generated temporary password). Returned to the
    /// UI but deliberately excluded from run-history persistence and notifications.
    /// </summary>
    public Dictionary<string, string> Ephemeral { get; set; } = new();

    /// <summary>
    /// Native evidence from a planned operation run through the classic remediate path. Null for
    /// classic workflows; their evidence is adapted from steps and post-state when the run is recorded.
    /// </summary>
    public Operations.OperationEvidence? Evidence { get; set; }

    /// <summary>
    /// Desired-state verification for the steps, each check linked to its step with
    /// <see cref="StepId"/>: a re-read of exactly what the step was meant to change (not a general
    /// health diagnosis). A step with no passing check is never reported as verified. Null when the
    /// workflow does not verify its steps; its evidence then records every change as unverified.
    /// </summary>
    public List<Operations.VerificationCheck>? Verification { get; set; }

    /// <summary>0-based indexes of steps that changed nothing (already in place, read-only).</summary>
    public List<int> UnchangedSteps { get; set; } = new();

    /// <summary>Plan-item id of the step at <paramref name="index"/> in the adapted evidence.</summary>
    public static string StepId(int index) => $"step-{index + 1}";

    /// <summary>Records a desired-state check for the step at <paramref name="stepIndex"/>.</summary>
    public void Verify(int stepIndex, string name, bool passed, string? detail) =>
        (Verification ??= new()).Add(new Operations.VerificationCheck(name, passed, detail, StepId(stepIndex)));

    /// <summary>Records that the step's change was acknowledged but cannot be confirmed by a read-back.</summary>
    public void CannotVerify(int stepIndex, string name, string detail) =>
        (Verification ??= new()).Add(Operations.VerificationCheck.NotVerifiable(name, detail, StepId(stepIndex)));

    public bool Succeeded => Steps.Count > 0 && Steps.All(s => s.Success);
}

/// <summary>Describes an input the workflow needs, so the UI can render a form generically.</summary>
public record WorkflowInput(string Key, string Label, string? Placeholder = null, bool Required = true, string? Default = null, string Type = "text");

/// <summary>
/// A "known-fix" helpdesk workflow: diagnose a target's state transparently, then apply an
/// idempotent remediation and re-diagnose. Implementations live in the backend project they use
/// (Graph or Exchange); the catalog exposes them uniformly to the API and UI.
/// </summary>
public interface IWorkflow
{
    /// <summary>Stable id used in routes, e.g. <c>license-repair</c>.</summary>
    string Id { get; }
    string Name { get; }
    string Description { get; }
    /// <summary>Grouping for the UI, e.g. "Identity" or "Mailbox".</summary>
    string Category { get; }
    IReadOnlyList<WorkflowInput> Inputs { get; }

    Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default);
    Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default);
}
