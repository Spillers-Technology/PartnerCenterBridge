using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Graph.ConfigSections;

internal sealed class ConditionalAccessPoliciesSection : IConfigSection
{
    private readonly TenantGraphRest _graph;
    public ConditionalAccessPoliciesSection(TenantGraphRest graph) => _graph = graph;

    public string Id => "conditional-access-policies";
    public string Name => "Conditional Access Policies";
    public string Category => "Identity";

    public async Task<string> CaptureAsync(Tenant tenant, CancellationToken ct)
    {
        var graph = await _graph.CreateAsync(tenant, ct);
        var items = await graph.GetAllAsync("/identity/conditionalAccess/policies", ct);
        return ConfigCaptureSupport.Normalize(items);
    }
}
