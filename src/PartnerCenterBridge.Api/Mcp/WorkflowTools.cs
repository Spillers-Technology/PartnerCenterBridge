using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Controllers; // WorkflowSummaryDto lives here (defined in WorkflowsController.cs)
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

    public WorkflowTools(WorkflowCatalog catalog, BridgeDbContext db, IRunNotifier notifier, ITenantAccessService access, ICurrentActor actor)
    {
        _catalog = catalog;
        _db = db;
        _notifier = notifier;
        _access = access;
        _actor = actor;
    }

    [McpServerTool, Description("Lists the known-fix workflow catalog (MFA reset, password reset, license repair, mailbox archive, etc.) with their required inputs.")]
    public IReadOnlyList<WorkflowSummaryDto> ListWorkflows() =>
        _catalog.All.OrderBy(w => w.Category).ThenBy(w => w.Name)
            .Select(w => new WorkflowSummaryDto(w.Id, w.Name, w.Description, w.Category, w.Inputs))
            .ToList();

    [McpServerTool, Description("Runs a workflow's read-only diagnosis against a tenant. Never mutates anything -- safe to call regardless of the tenant's MCP approval mode.")]
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
}
