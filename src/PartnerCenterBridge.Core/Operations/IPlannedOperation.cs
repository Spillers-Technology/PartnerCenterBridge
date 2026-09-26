using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Core.Operations;

/// <summary>
/// A workflow that works in explicit Plan -> Apply -> Verify steps. Plan reads live state and
/// lists every candidate change (eligible or not, with reasons). Apply re-plans server-side,
/// applies only the selected items that are still eligible, re-reads state and verifies each one.
/// It remains an <see cref="IWorkflow"/> so the catalog, generic diagnose/remediate endpoints and
/// the MCP approval queue keep working: diagnose returns the plan as findings, remediate applies
/// every eligible item.
/// </summary>
public interface IPlannedOperation : IWorkflow
{
    Task<OperationPlan> PlanAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default);

    /// <summary>
    /// Applies the selected items. The returned evidence has operation-specific fields filled in
    /// (plan, changes, verification, outcome, notes); the caller stamps run id, operator, tenant
    /// and timing before persisting.
    /// </summary>
    Task<OperationEvidence> ApplyAsync(
        Tenant tenant, IReadOnlyDictionary<string, string> inputs, IReadOnlyCollection<string> selectedItemIds,
        CancellationToken ct = default);
}
