using System.ComponentModel;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

/// <summary>
/// Read-only MCP access to planned operations. There is deliberately no MCP apply tool here:
/// applying Access Parity from MCP goes through <c>remediate_workflow</c> ("access-parity"), which
/// stages a PendingAction for human approval in Queue mode exactly like every other workflow.
/// </summary>
[McpServerToolType]
public class OperationTools
{
    private readonly WorkflowCatalog _catalog;
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;
    private readonly OperationRunRecorder _recorder;

    public OperationTools(WorkflowCatalog catalog, BridgeDbContext db, ITenantAccessService access, OperationRunRecorder recorder)
    {
        _catalog = catalog;
        _db = db;
        _access = access;
        _recorder = recorder;
    }

    [McpServerTool(ReadOnly = true, Destructive = false), Description(
        "Plans Access Parity: compares a target user's direct group memberships against a source user's and lists " +
        "which missing groups could be added (cloud Security and Microsoft 365 groups) and why the rest would not be. " +
        "Read-only -- never changes anything.")]
    public async Task<OperationPlan> PlanAccessParity(Guid tenantId, string sourceUserId, string targetUserId, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Viewer, ct))
            throw new UnauthorizedAccessException("Caller does not have access to this tenant.");
        var tenant = await _db.Tenants.FindAsync([tenantId], ct) ?? throw new InvalidOperationException("Tenant not found.");
        var op = _catalog.Find("access-parity") as IPlannedOperation
                 ?? throw new InvalidOperationException("Access parity is not available.");

        var inputs = new Dictionary<string, string> { ["sourceUserId"] = sourceUserId, ["targetUserId"] = targetUserId };
        var session = _recorder.Start(op.Id, op.Name, tenant, WorkflowRunKind.Plan, inputs, targetUserId);
        OperationPlan? plan = null;
        Exception? error = null;
        try
        {
            plan = await op.PlanAsync(tenant, inputs, ct);
            return plan;
        }
        catch (Exception ex) { error = ex; throw; }
        finally
        {
            await _recorder.CompleteAsync(session, plan is null ? null : WorkflowEvidenceAdapter.FromPlan(plan), error);
        }
    }
}
