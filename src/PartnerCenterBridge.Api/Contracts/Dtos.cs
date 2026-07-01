using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Api.Contracts;

public record TenantDto(Guid Id, string TenantId, string DisplayName, string? DefaultDomain, TenantStatus Status, Guid? ContractId)
{
    public static TenantDto From(Tenant t) => new(t.Id, t.TenantId, t.DisplayName, t.DefaultDomain, t.Status, t.ContractId);
}

public record ContractDto(Guid Id, string Name, string? Notes, int TenantCount, int DesiredAppCount)
{
    public static ContractDto From(Contract c) => new(c.Id, c.Name, c.Notes, c.Tenants.Count, c.DesiredApps.Count);
}

public record CreateContractRequest(string Name, string? Notes);

public record AppTemplateDto(
    Guid Id, string DisplayName, string? Publisher, int ContentVersion,
    bool HasPackage, Guid? ContractId, IReadOnlyList<DetectionRule> DetectionRules, IReadOnlyList<AssignmentSpec> Assignments)
{
    public static AppTemplateDto From(AppTemplate a) => new(
        a.Id, a.DisplayName, a.Publisher, a.ContentVersion, a.Content is not null, a.ContractId,
        a.DetectionRules, a.Assignments);
}

public record CreateAppTemplateRequest(
    string DisplayName,
    string? Description,
    string? Publisher,
    string InstallCommandLine,
    string UninstallCommandLine,
    Guid? ContractId,
    List<DetectionRule>? DetectionRules,
    List<AssignmentSpec>? Assignments);

public record DeployRequest(Guid TemplateId, List<Guid> TenantIds);

public record DeploymentDto(
    Guid Id, Guid AppTemplateId, Guid TenantId, string? IntuneAppId,
    int DeployedTemplateVersion, DeploymentStatus Status, string? LastError, DateTimeOffset? LastSyncedAt)
{
    public static DeploymentDto From(Deployment d) => new(
        d.Id, d.AppTemplateId, d.TenantId, d.IntuneAppId, d.DeployedTemplateVersion, d.Status, d.LastError, d.LastSyncedAt);
}

public record ReconcilePlanItemDto(Guid TenantId, string TenantName, Guid TemplateId, string TemplateName, string Action);
