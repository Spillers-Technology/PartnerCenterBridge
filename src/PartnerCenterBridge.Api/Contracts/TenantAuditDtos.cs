using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.TenantAudits;

namespace PartnerCenterBridge.Api.Contracts;

public record AuditCategoryDto(string Id, string Label);

public record AuditCheckInfoDto(
    string Id, string Name, string Category, string Description, string BusinessImpact, string Recommendation,
    IReadOnlyList<string> SeverityRules, IReadOnlyList<AuditRequirement> Requirements, IReadOnlyList<string> ParameterKeys,
    IReadOnlyList<string> Limitations, int Version)
{
    public static AuditCheckInfoDto From(AuditCheckDescriptor d) => new(
        d.Id, d.Name, d.Category, d.Description, d.BusinessImpact, d.Recommendation, d.SeverityRules, d.Requirements,
        d.Parameters.Select(p => p.Key).ToList(), d.Limitations, d.Version);
}

public record AuditCatalogDto(
    IReadOnlyList<AuditCategoryDto> Categories, IReadOnlyList<AuditPreset> Presets,
    IReadOnlyList<AuditParameterDefinition> Parameters, IReadOnlyList<AuditCheckInfoDto> Checks, int SchemaVersion);

/// <summary>Run request: checks by id and/or by category (union); neither means every check.</summary>
public record RunTenantAuditRequest(List<string>? CheckIds, List<string>? Categories, Dictionary<string, int>? Parameters);

public record TenantAuditRunSummaryDto(
    Guid Id, Guid TenantId, string? TenantName, Guid? BatchId, string AuditName, string Operator,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, int SchemaVersion, string EngineVersion, AuditSummaryCounts Summary)
{
    public static TenantAuditRunSummaryDto From(TenantAuditRun r, string? tenantName = null) => new(
        r.Id, r.TenantId, tenantName ?? r.Tenant?.DisplayName, r.BatchId, r.AuditName, r.Operator,
        r.StartedAt, r.CompletedAt, r.SchemaVersion, r.EngineVersion, r.ToSummary());
}

public record AuditEstateTenantDto(Guid TenantId, string TenantName, TenantAuditRunSummaryDto? LatestRun);

/// <summary>Latest audit per tenant the caller can see, with the rollup over those that have one.</summary>
public record AuditEstateDto(AuditEstateSummary Summary, int TenantsWithoutAudit, IReadOnlyList<AuditEstateTenantDto> Tenants);
