using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Controllers; // WorkflowSummaryDto lives here (defined in WorkflowsController.cs)
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

[McpServerToolType]
public class WorkflowTools
{
    private readonly WorkflowCatalog _catalog;
    private readonly BridgeDbContext _db;
    private readonly IRunNotifier _notifier;
    private readonly ITenantAccessService _access;
    private readonly ICurrentActor _actor;
    private readonly PendingActionService _pending;

    public WorkflowTools(WorkflowCatalog catalog, BridgeDbContext db, IRunNotifier notifier, ITenantAccessService access, ICurrentActor actor, PendingActionService pending)
    {
        _catalog = catalog;
        _db = db;
        _notifier = notifier;
        _access = access;
        _actor = actor;
        _pending = pending;
    }

    [McpServerTool(ReadOnly = true, Destructive = false), Description("Lists the known-fix workflow catalog (MFA reset, password reset, license repair, mailbox archive, etc.) with their required inputs.")]
    public IReadOnlyList<WorkflowSummaryDto> ListWorkflows() =>
        _catalog.All.OrderBy(w => w.Category).ThenBy(w => w.Name)
            .Select(w => new WorkflowSummaryDto(w.Id, w.Name, w.Description, w.Category, w.Inputs))
            .ToList();

    [McpServerTool(ReadOnly = true, Destructive = false), Description("Runs a workflow's read-only diagnosis against a tenant. Never mutates anything -- safe to call regardless of the tenant's MCP approval mode.")]
    public async Task<DiagnosisResult> DiagnoseWorkflow(string workflowId, Guid tenantId, Dictionary<string, string> inputs, CancellationToken ct)
    {
        var workflow = _catalog.Find(workflowId) ?? throw new InvalidOperationException($"Unknown workflow '{workflowId}'.");
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Viewer, ct))
            throw new UnauthorizedAccessException("Caller does not have access to this tenant.");
        var tenant = await _db.Tenants.FindAsync([tenantId], ct) ?? throw new InvalidOperationException("Tenant not found.");
        var run = new WorkflowRun
        {
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            TenantId = tenant.Id,
            Tenant = tenant,
            Kind = WorkflowRunKind.Diagnose,
            Operator = _actor.Name,
            Inputs = new(inputs),
            Succeeded = true
        };
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await workflow.DiagnoseAsync(tenant, inputs, ct);
            run.Findings = result.Findings;
            run.Healthy = result.Healthy;
            return result;
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
    }

    [McpServerTool(ReadOnly = false, Destructive = true), Description(
        "Runs a workflow's fix. If the tenant is in Queue approval mode (the default), this stages " +
        "the fix for a human to approve in the Approvals tab and returns its pending-action id " +
        "instead of running anything -- call check_pending_action with that id to see whether it " +
        "has been approved yet. If the tenant is in ClientTrust mode, this runs the fix immediately.")]
    public async Task<string> RemediateWorkflow(string workflowId, Guid tenantId, Dictionary<string, string> inputs, CancellationToken ct)
    {
        var workflow = _catalog.Find(workflowId) ?? throw new InvalidOperationException($"Unknown workflow '{workflowId}'.");
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Operator, ct))
            throw new UnauthorizedAccessException("Caller does not have Operator+ access to this tenant.");
        var tenant = await _db.Tenants.FindAsync([tenantId], ct) ?? throw new InvalidOperationException("Tenant not found.");

        if (tenant.McpApprovalMode == McpApprovalMode.ClientTrust)
        {
            var run = new WorkflowRun
            {
                WorkflowId = workflow.Id,
                WorkflowName = workflow.Name,
                TenantId = tenant.Id,
                Tenant = tenant,
                Kind = WorkflowRunKind.Remediate,
                Operator = _actor.Name,
                Inputs = new(inputs),
                Succeeded = true
            };
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await workflow.RemediateAsync(tenant, inputs, ct);
                run.Steps = result.Steps;
                run.Findings = result.PostState?.Findings ?? new();
                run.Healthy = result.PostState?.Healthy;
                run.Succeeded = result.Succeeded;
                var outcome = $"Executed immediately (tenant is in ClientTrust mode). Succeeded={result.Succeeded}.";
                if (result.Ephemeral.Count > 0)
                    outcome += " One-time values: " + string.Join(", ", result.Ephemeral.Select(pair => $"{pair.Key}={pair.Value}")) + ".";
                return outcome;
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
        }

        var diagnosis = await workflow.DiagnoseAsync(tenant, inputs, ct);
        var preview = $"Remediate '{workflow.Name}' on {tenant.DisplayName}. Current diagnosis: " +
            string.Join("; ", diagnosis.Findings.Select(f => $"{f.Name}={f.Status}"));
        if (inputs.Count > 0)
            preview += " Inputs: " + string.Join(", ", inputs.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}={pair.Value}")) + ".";
        // Interim restriction: Queue mode has no safe approval-time channel for this workflow's one-time value.
        if (workflow.Id == "password-reset")
            throw new InvalidOperationException("This workflow produces a one-time value that Queue-mode staging cannot safely deliver yet. Use ClientTrust mode or the SPA directly for this workflow until a proper approval-time secret-delivery design exists.");
        var payload = new WorkflowRemediatePayload(workflowId, inputs);
        var staged = await _pending.StageAsync(tenantId, "workflow.remediate", _access.CurrentUserId ?? Guid.Empty, payload, preview, ct);
        return $"Staged for approval (tenant is in Queue mode, the default). PendingActionId={staged.Id}. Preview: {preview}";
    }
}
