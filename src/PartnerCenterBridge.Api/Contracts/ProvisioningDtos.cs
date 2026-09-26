using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;

namespace PartnerCenterBridge.Api.Contracts;

public record HireApiRequest(Guid TenantId, NewHireRequest Hire);

/// <summary>
/// Per-run offboarding options, wire-compatible with the original terminate body. Every flag is
/// optional: a flag that is sent overrides the effective policy for this run, one that is omitted
/// inherits it (contract policy, else the built-in defaults).
/// </summary>
public class TerminationOptions
{
    public required string UserId { get; set; }
    public bool? BlockSignIn { get; set; }
    public bool? RevokeSessions { get; set; }
    public bool? RemoveLicenses { get; set; }
    /// <summary>false = groupCleanup None; true = keep the policy's cleanup mode (RemoveAll if the policy says None).</summary>
    public bool? RemoveFromGroups { get; set; }
    public bool? ConvertMailboxToShared { get; set; }
    public string? ForwardingSmtpAddress { get; set; }
}

/// <summary>
/// Body for <c>POST /api/provisioning/terminate</c> and <c>/terminate/plan</c>. <see cref="Policy"/>
/// optionally replaces the contract policy as the base for this run; <see cref="Termination"/>
/// flags still override on top of it.
/// </summary>
public record TerminateApiRequest(Guid TenantId, TerminationOptions Termination, OffboardingPolicy? Policy = null);

/// <summary>The original terminate response fields plus the structured evidence and the policy actually applied.</summary>
public record TerminateResultDto(
    string? UserId, string? UserPrincipalName, string? InitialPassword,
    List<ProvisioningStep> Steps, bool Succeeded, OperationEvidence? Evidence, OffboardingPolicy Policy);

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
