using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Api.Contracts;

public record HireApiRequest(Guid TenantId, NewHireRequest Hire);
public record TerminateApiRequest(Guid TenantId, TerminationRequest Termination);

public record ProvisioningTemplateDto(
    Guid ContractId, string UsageLocation, string? UpnDomain,
    string? DefaultJobTitle, string? DefaultDepartment,
    IReadOnlyList<string> LicenseSkuIds, IReadOnlyList<string> GroupIds)
{
    public static ProvisioningTemplateDto From(ProvisioningTemplate t) => new(
        t.ContractId, t.UsageLocation, t.UpnDomain, t.DefaultJobTitle, t.DefaultDepartment,
        t.LicenseSkuIds, t.GroupIds);
}

public record UpsertProvisioningTemplateRequest(
    string UsageLocation, string? UpnDomain, string? DefaultJobTitle, string? DefaultDepartment,
    List<string>? LicenseSkuIds, List<string>? GroupIds);
