using System.Text.Json;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

public record WorkflowRemediatePayload(string WorkflowId, Dictionary<string, string> Inputs);

/// <summary>
/// Runs the same WorkflowCatalog.Find(...).RemediateAsync(...) call WorkflowsController.Remediate
/// makes -- this is the "approval invokes the same service call a direct controller action would"
/// half of the design. Queued executions after approval or retry intentionally create only
/// PendingAction audit events, not WorkflowRun records: resolving the approving user's display
/// name at this later execution point is a separate attribution design concern.
/// </summary>
public class WorkflowRemediateExecutor : IPendingActionExecutor
{
    private readonly WorkflowCatalog _catalog;
    private readonly BridgeDbContext _db;

    public WorkflowRemediateExecutor(WorkflowCatalog catalog, BridgeDbContext db)
    {
        _catalog = catalog;
        _db = db;
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
        await workflow.RemediateAsync(tenant, payload.Inputs, ct);
    }
}
