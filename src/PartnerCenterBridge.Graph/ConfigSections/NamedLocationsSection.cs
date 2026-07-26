using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Graph.ConfigSections;

internal sealed class NamedLocationsSection : IConfigSection
{
    private readonly TenantGraphRest _graph;
    public NamedLocationsSection(TenantGraphRest graph) => _graph = graph;

    public string Id => "named-locations";
    public string Name => "Named Locations";
    public string Category => "Identity";

    public async Task<string> CaptureAsync(Tenant tenant, CancellationToken ct)
    {
        var graph = await _graph.CreateAsync(tenant, ct);
        var items = await graph.GetAllAsync("/identity/conditionalAccess/namedLocations", ct);
        return ConfigCaptureSupport.Normalize(items);
    }
}
