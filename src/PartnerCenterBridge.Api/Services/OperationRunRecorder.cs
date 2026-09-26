using System.Diagnostics;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Services;

/// <summary>
/// Persists planned-operation runs (Access Parity plan/apply, offboarding plan/apply) as
/// <see cref="WorkflowRun"/> rows carrying their evidence, so they share history, per-person
/// filtering, evidence export and failure notification with classic workflow runs.
/// </summary>
public class OperationRunRecorder
{
    private readonly BridgeDbContext _db;
    private readonly IRunNotifier _notifier;
    private readonly ICurrentActor _actor;

    public OperationRunRecorder(BridgeDbContext db, IRunNotifier notifier, ICurrentActor actor)
    {
        _db = db;
        _notifier = notifier;
        _actor = actor;
    }

    public sealed class Session
    {
        internal Session(WorkflowRun run) => Run = run;
        public WorkflowRun Run { get; }
        internal Stopwatch Clock { get; } = Stopwatch.StartNew();
    }

    public Session Start(string operationId, string operationName, Tenant tenant, WorkflowRunKind kind,
        Dictionary<string, string> inputs, string? targetId = null) => new(new WorkflowRun
    {
        WorkflowId = operationId,
        WorkflowName = operationName,
        TenantId = tenant.Id,
        Tenant = tenant,
        Kind = kind,
        Operator = _actor.Name,
        Inputs = new(inputs),
        TargetId = WorkflowEvidenceAdapter.NormalizeTargetId(targetId),
        Succeeded = true
    });

    /// <summary>
    /// Completes and saves the run. Pass the evidence on success, or the exception on failure
    /// (the run is then recorded as Failed with the error). Uses CancellationToken.None so an
    /// aborted request still leaves the audit trail.
    /// </summary>
    public async Task CompleteAsync(Session session, OperationEvidence? evidence, Exception? error = null)
    {
        var run = session.Run;
        run.DurationMs = session.Clock.ElapsedMilliseconds;
        if (error is not null)
        {
            run.Succeeded = false;
            run.Error = error.Message;
        }
        if (evidence is not null)
        {
            // Resolved target (object id) replaces whatever the caller typed (id or UPN).
            if (evidence.Target is not null)
            {
                run.TargetId = WorkflowEvidenceAdapter.NormalizeTargetId(evidence.Target.Id);
                run.TargetDisplayName = evidence.Target.DisplayName;
            }
            if (run.Kind == WorkflowRunKind.Plan)
            {
                run.Findings = evidence.Preflight.ToList();
                run.Healthy = evidence.Plan.All(i => !i.Eligible);
            }
            else
            {
                var checks = evidence.Verification;
                run.Steps = evidence.Changes.Where(c => c.Attempted)
                    .Select(c => new ProvisioningStep($"{c.Action}: {c.ObjectName}", c.Succeeded, c.Detail)).ToList();
                run.Findings = checks.Select(v => new Finding(v.Name, v.Passed ? FindingStatus.Ok : FindingStatus.Blocker, v.Detail)).ToList();
                run.Healthy = checks.All(v => v.Passed);
                run.Succeeded = error is null && OutcomeRules.IsSuccess(evidence.Outcome);
            }
        }
        WorkflowEvidenceAdapter.Finalize(run, evidence);
        _db.WorkflowRuns.Add(run);
        await _db.SaveChangesAsync(CancellationToken.None);
        await _notifier.NotifyAsync(run, CancellationToken.None);
    }
}
