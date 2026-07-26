using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Graph.ConfigSections;

internal sealed class DeviceCompliancePoliciesSection : IConfigSection
{
    private readonly TenantGraphRest _graph;
    public DeviceCompliancePoliciesSection(TenantGraphRest graph) => _graph = graph;

    public string Id => "device-compliance-policies";
    public string Name => "Device Compliance Policies";
    public string Category => "Devices";

    public async Task<string> CaptureAsync(Tenant tenant, CancellationToken ct)
    {
        var graph = await _graph.CreateAsync(tenant, ct);
        var items = await graph.GetAllAsync("/deviceManagement/deviceCompliancePolicies", ct);
        return ConfigCaptureSupport.Normalize(items);
    }
}
