using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Operations;

/// <summary>
/// Policy-driven offboarding as Plan -> Apply -> Verify. Not a catalog workflow on purpose: it is
/// destructive and reachable only through the provisioning endpoints (Operator grant), never the
/// generic workflow/MCP remediate path.
/// </summary>
public interface IOffboardingService
{
    /// <summary>Ordered plan for offboarding <paramref name="userId"/> under <paramref name="policy"/>. Reads only.</summary>
    Task<OperationPlan> PlanAsync(Tenant tenant, string userId, OffboardingPolicy policy, CancellationToken ct = default);

    /// <summary>Re-plans, runs every eligible item in plan order, then re-reads state to verify each one.</summary>
    Task<OperationEvidence> ApplyAsync(Tenant tenant, string userId, OffboardingPolicy policy, CancellationToken ct = default);
}
