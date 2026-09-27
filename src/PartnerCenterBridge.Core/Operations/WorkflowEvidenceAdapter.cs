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

    /// <summary>
    /// Copies a finished remediation's result onto the run: steps, post-run diagnosis, success flag,
    /// and evidence -- the planned operation's own, or, when the workflow verified its steps against
    /// their desired state, evidence built from those checks. (Without either, evidence is adapted
    /// from the run when it is finalized, and records the changes as unverified.)
    /// </summary>
    public static void ApplyRemediation(WorkflowRun run, WorkflowRunResult result)
    {
        run.Steps = result.Steps;
        run.Findings = result.PostState?.Findings ?? new();
        run.Healthy = result.PostState?.Healthy;
        run.Succeeded = result.Succeeded;
        run.Evidence = result.Evidence
                       ?? (result.Verification is null ? null : BuildRemediation(run, result.Verification, result.UnchangedSteps));
    }

    /// <summary>Adapts a classic diagnose/remediate run (new or legacy) into evidence.</summary>
    public static OperationEvidence Build(WorkflowRun run)
    {
        if (run.Evidence is not null) return run.Evidence;
        var isDiagnosis = run.Kind is WorkflowRunKind.Diagnose or WorkflowRunKind.Plan;
        if (!isDiagnosis) return BuildRemediation(run, null, Array.Empty<int>());

        var e = new OperationEvidence
        {
            OperationId = run.WorkflowId,
            OperationName = run.WorkflowName,
            Target = TargetFrom(run)
        };
        if (!string.IsNullOrEmpty(run.Error)) e.Failures.Add(run.Error!);
        e.Preflight = run.Findings.ToList();
        e.Warnings.AddRange(run.Findings
            .Where(f => f.Status is FindingStatus.Warning or FindingStatus.Blocker)
            .Select(f => f.Detail is null ? f.Name : $"{f.Name}: {f.Detail}"));
        e.Outcome = !string.IsNullOrEmpty(run.Error)
            ? Outcome.Failed
            : run.Healthy == true ? Outcome.NoChangeNeeded : Outcome.Planned;
        e.TicketNotes = EvidenceRenderer.GenericTicketNotes(e, isDiagnosis: true);
        return e;
    }

    /// <summary>
    /// Remediation evidence. With <paramref name="checks"/> (desired-state verification linked to
    /// steps by plan-item id) a step counts as verified only when its linked checks all passed;
    /// a failed check is a verification failure; a step with no check, or only unverifiable ones, is
    /// unverified. Without checks (workflows that do not verify, legacy rows) every change is
    /// unverified: the post-run diagnosis describes general health, not whether each change took
    /// effect, so it is recorded as observations only.
    /// </summary>
    private static OperationEvidence BuildRemediation(WorkflowRun run, IReadOnlyList<VerificationCheck>? checks, IReadOnlyCollection<int> unchanged)
    {
        var target = TargetFrom(run);
        var e = new OperationEvidence
        {
            OperationId = run.WorkflowId,
            OperationName = run.WorkflowName,
            Target = target
        };
        if (!string.IsNullOrEmpty(run.Error)) e.Failures.Add(run.Error!);

        var targetName = target?.DisplayName ?? "";
        var items = new List<ItemOutcome>();
        for (var i = 0; i < run.Steps.Count; i++)
        {
            var step = run.Steps[i];
            var id = WorkflowRunResult.StepId(i);
            var noChange = step.Success && unchanged.Contains(i);
            e.Plan.Add(new PlanItem
            {
                Id = id,
                Action = step.Name,
                ObjectType = target?.Kind ?? "",
                ObjectId = target?.Id ?? "",
                ObjectName = targetName,
                Eligible = true,
                Category = "WorkflowStep"
            });
            e.Changes.Add(new ChangeResult
            {
                PlanItemId = id,
                Action = step.Name,
                ObjectName = targetName,
                Attempted = !noChange,
                Succeeded = step.Success,
                Detail = step.Detail
            });
            if (noChange) continue;
            if (!step.Success) { items.Add(new ItemOutcome(true, false, false)); continue; }

            var linked = checks?.Where(c => c.PlanItemId == id).ToList() ?? new List<VerificationCheck>();
            if (linked.Any(c => !c.Passed && !c.Unverifiable))
                items.Add(new ItemOutcome(true, true, false));
            else if (linked.Count > 0 && linked.All(c => c.Passed))
                items.Add(new ItemOutcome(true, true, true));
            else
                items.Add(new ItemOutcome(true, true, false, Unverifiable: true));
        }

        if (checks is not null)
        {
            e.Verification = checks.ToList();
            // A whole-run check that failed still contradicts the reported changes.
            if (checks.Any(c => c.PlanItemId is null && !c.Passed && !c.Unverifiable))
                items.Add(new ItemOutcome(true, true, false));
        }
        else
        {
            e.Limitations.Add("This workflow does not verify its changes individually; the post-run diagnosis is recorded as observations, not as proof that each change took effect.");
            e.Warnings.AddRange(run.Findings
                .Where(f => f.Status is FindingStatus.Warning or FindingStatus.Blocker)
                .Select(f => "Post-run diagnosis: " + (f.Detail is null ? f.Name : $"{f.Name}: {f.Detail}")));
        }

        if (!string.IsNullOrEmpty(run.Error))
            items.Add(new ItemOutcome(false, false, false, RequiredButNotDone: true));
        e.Outcome = !string.IsNullOrEmpty(run.Error) && e.Changes.Count == 0 ? Outcome.Failed : OutcomeRules.Derive(items);
        e.TicketNotes = EvidenceRenderer.GenericTicketNotes(e, isDiagnosis: false);
        return e;
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
