using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Core.Operations;

/// <summary>
/// Gives classic diagnose/remediate workflow runs the same evidence shape as planned operations:
/// diagnosis findings become preflight, remediation steps become changes, and the post-fix
/// re-diagnosis becomes verification. Built purely from the persisted <see cref="WorkflowRun"/>
/// fields, so rows written before evidence existed can be rendered on read too.
/// </summary>
public static class WorkflowEvidenceAdapter
{
    /// <summary>Input keys that name the run's target, in priority order.</summary>
    private static readonly string[] TargetKeys = ["targetUserId", "userUpn", "userId", "identity"];

    /// <summary>
    /// Stamps a finished run with target, outcome and evidence. Pass <paramref name="native"/> when
    /// the workflow produced its own evidence (planned operations); otherwise it is adapted from
    /// the run. Call after <see cref="WorkflowRun.DurationMs"/> is set.
    /// </summary>
    public static void Finalize(WorkflowRun run, OperationEvidence? native = null)
    {
        var evidence = native ?? Build(run);
        Stamp(evidence, run);
        run.Evidence = evidence;
        run.Outcome = evidence.Outcome;
        run.TargetId ??= NormalizeTargetId(evidence.Target?.Id);
        run.TargetDisplayName ??= evidence.Target?.DisplayName;
    }

    /// <summary>Fills the run-level fields (id, tenant, operator, timing) every evidence record carries.</summary>
    public static void Stamp(OperationEvidence evidence, WorkflowRun run)
    {
        evidence.RunId = run.Id.ToString();
        if (string.IsNullOrEmpty(evidence.OperationId)) evidence.OperationId = run.WorkflowId;
        if (string.IsNullOrEmpty(evidence.OperationName)) evidence.OperationName = run.WorkflowName;
        evidence.Tenant = new OperationTenantRef
        {
            Id = run.TenantId.ToString(),
            DisplayName = run.Tenant?.DisplayName ?? "",
            TenantId = run.Tenant?.TenantId ?? ""
        };
        evidence.Operator = run.Operator;
        evidence.StartedAt = run.StartedAt;
        evidence.CompletedAt = run.StartedAt.AddMilliseconds(run.DurationMs);
    }

    /// <summary>Lowercased so per-person filtering is a plain equality match (UPNs are case-insensitive).</summary>
    public static string? NormalizeTargetId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();

    /// <summary>Evidence for a recorded plan: nothing applied, outcome <see cref="Outcome.Planned"/>.</summary>
    public static OperationEvidence FromPlan(OperationPlan plan)
    {
        var eligible = plan.Items.Count(i => i.Eligible);
        var ineligible = plan.Items.Count - eligible;
        var notes = new System.Text.StringBuilder();
        notes.Append($"Planned {plan.OperationName} for {plan.Target.DisplayName}: ");
        notes.Append($"{EvidenceRenderer.Count(eligible, "eligible item")}");
        if (ineligible > 0) notes.Append($" and {EvidenceRenderer.Count(ineligible, "item")} listed as not eligible (with reasons)");
        notes.Append(". Nothing was applied.");
        return new OperationEvidence
        {
            OperationId = plan.OperationId,
            OperationName = plan.OperationName,
            Target = plan.Target,
            Outcome = Outcome.Planned,
            Preflight = plan.Preflight,
            Plan = plan.Items,
            Warnings = plan.Warnings.ToList(),
            Limitations = plan.Limitations.ToList(),
            TicketNotes = notes.ToString()
        };
    }

    /// <summary>Adapts a classic diagnose/remediate run (new or legacy) into evidence.</summary>
    public static OperationEvidence Build(WorkflowRun run)
    {
        if (run.Evidence is not null) return run.Evidence;

        var target = TargetFrom(run);
        var e = new OperationEvidence
        {
            OperationId = run.WorkflowId,
            OperationName = run.WorkflowName,
            Target = target
        };
        if (!string.IsNullOrEmpty(run.Error)) e.Failures.Add(run.Error!);

        var isDiagnosis = run.Kind is WorkflowRunKind.Diagnose or WorkflowRunKind.Plan;
        if (isDiagnosis)
        {
            e.Preflight = run.Findings.ToList();
            e.Warnings.AddRange(run.Findings
                .Where(f => f.Status is FindingStatus.Warning or FindingStatus.Blocker)
                .Select(f => f.Detail is null ? f.Name : $"{f.Name}: {f.Detail}"));
            e.Outcome = !string.IsNullOrEmpty(run.Error)
                ? Outcome.Failed
                : run.Healthy == true ? Outcome.NoChangeNeeded : Outcome.Planned;
        }
        else
        {
            var targetName = target?.DisplayName ?? "";
            for (var i = 0; i < run.Steps.Count; i++)
            {
                var step = run.Steps[i];
                e.Plan.Add(new PlanItem
                {
                    Id = $"step-{i + 1}",
                    Action = step.Name,
                    ObjectType = target?.Kind ?? "",
                    ObjectId = target?.Id ?? "",
                    ObjectName = targetName,
                    Eligible = true,
                    Category = "WorkflowStep"
                });
                e.Changes.Add(new ChangeResult
                {
                    PlanItemId = $"step-{i + 1}",
                    Action = step.Name,
                    ObjectName = targetName,
                    Attempted = true,
                    Succeeded = step.Success,
                    Detail = step.Detail
                });
            }
            // Post-fix re-diagnosis is the verification; Info findings are observations, not checks.
            e.Verification = run.Findings
                .Where(f => f.Status != FindingStatus.Info)
                .Select(f => new VerificationCheck(f.Name, f.Status == FindingStatus.Ok, f.Detail))
                .ToList();
            e.Outcome = DeriveRemediationOutcome(run, e);
        }

        e.TicketNotes = EvidenceRenderer.GenericTicketNotes(e, isDiagnosis);
        return e;
    }

    /// <summary>
    /// Remediation outcome from steps + post-diagnosis. Workflow verification is whole-run (a fresh
    /// diagnosis), not per step, so: any step reported ok while the re-diagnosis is unhealthy or
    /// missing counts as unverified.
    /// </summary>
    private static Outcome DeriveRemediationOutcome(WorkflowRun run, OperationEvidence e)
    {
        if (!string.IsNullOrEmpty(run.Error) && e.Changes.Count == 0) return Outcome.Failed;
        var verified = run.Healthy == true && e.Verification.All(v => v.Passed);
        var items = e.Changes.Select(c => new ItemOutcome(
            Attempted: true, ReportedOk: c.Succeeded, Verified: c.Succeeded && verified)).ToList();
        if (!string.IsNullOrEmpty(run.Error))
            items.Add(new ItemOutcome(false, false, false, RequiredButNotDone: true));
        return OutcomeRules.Derive(items);
    }

    private static OperationTarget? TargetFrom(WorkflowRun run)
    {
        foreach (var key in TargetKeys)
            if (run.Inputs.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return new OperationTarget(key == "identity" ? "mailbox" : "user", v.Trim(),
                    run.TargetDisplayName ?? v.Trim());
        return null;
    }
}
