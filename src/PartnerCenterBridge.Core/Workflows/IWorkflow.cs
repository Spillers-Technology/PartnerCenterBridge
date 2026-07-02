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
