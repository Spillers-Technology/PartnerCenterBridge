using System.Diagnostics;
using System.Text.Json;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

public record WorkflowRemediatePayload(string WorkflowId, Dictionary<string, string> Inputs);

/// <summary>
/// Runs the same WorkflowCatalog.Find(...).RemediateAsync(...) call WorkflowsController.Remediate
/// makes -- this is the "approval invokes the same service call a direct controller action would"
/// half of the design. Queued executions persist and notify the same WorkflowRun history as direct
/// executions, and unsuccessful returned results are surfaced as executor failures so they remain
/// explicitly retryable PendingActions.
/// </summary>
public class WorkflowRemediateExecutor : IPendingActionExecutor
{
    private readonly WorkflowCatalog _catalog;
    private readonly BridgeDbContext _db;
    private readonly IRunNotifier _notifier;
    private readonly ICurrentActor _actor;

    public WorkflowRemediateExecutor(
        WorkflowCatalog catalog, BridgeDbContext db, IRunNotifier notifier, ICurrentActor actor)
    {
        _catalog = catalog;
        _db = db;
        _notifier = notifier;
        _actor = actor;
    }

    public string ActionType => "workflow.remediate";

    public async Task ExecuteAsync(PendingAction action, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<WorkflowRemediatePayload>(action.PayloadJson)
            ?? throw new InvalidOperationException("Malformed workflow.remediate payload.");
        var workflow = _catalog.Find(payload.WorkflowId)
            ?? throw new InvalidOperationException($"Unknown workflow '{payload.WorkflowId}'.");
        var tenant = await _db.Tenants.FindAsync([action.TenantId], ct)
            ?? throw new InvalidOperationException("Tenant not found.");
        var run = new WorkflowRun
        {
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            TenantId = tenant.Id,
            Tenant = tenant,
            Kind = WorkflowRunKind.Remediate,
            Operator = _actor.Name,
            Inputs = new(payload.Inputs),
            Succeeded = true
        };
        var sw = Stopwatch.StartNew();
        WorkflowRunResult? result = null;
        try
        {
            result = await workflow.RemediateAsync(tenant, payload.Inputs, ct);
            run.Steps = result.Steps;
            run.Findings = result.PostState?.Findings ?? new();
            run.Healthy = result.PostState?.Healthy;
            run.Succeeded = result.Succeeded;
        }
        catch (Exception ex)
        {
            run.Succeeded = false;
            run.Error = ex.Message;
            throw;
        }
        finally
        {
            run.DurationMs = sw.ElapsedMilliseconds;
            _db.WorkflowRuns.Add(run);
            await _db.SaveChangesAsync(CancellationToken.None);
            await _notifier.NotifyAsync(run, CancellationToken.None);
        }

        if (!result.Succeeded)
            throw new InvalidOperationException("Workflow remediation completed unsuccessfully.");
    }
}
