using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Abstractions;

/// <summary>Input for creating a new user in a customer tenant.</summary>
public class NewHireRequest
{
    public required string DisplayName { get; set; }
    public string? GivenName { get; set; }
    public string? Surname { get; set; }
    public required string UserPrincipalName { get; set; }
    public required string MailNickname { get; set; }

    /// <summary>Initial password; if null the service generates a strong one and returns it once.</summary>
    public string? Password { get; set; }
    public bool ForceChangePasswordNextSignIn { get; set; } = true;

    public string UsageLocation { get; set; } = "US";
    public string? JobTitle { get; set; }
    public string? Department { get; set; }

    /// <summary>Optional manager object id to link.</summary>
    public string? ManagerId { get; set; }

    public List<string> LicenseSkuIds { get; set; } = new();
    public List<string> GroupIds { get; set; } = new();
}

/// <summary>Options for offboarding a user.</summary>
public class TerminationRequest
{
    /// <summary>Object id (or UPN) of the user to offboard.</summary>
    public required string UserId { get; set; }
    public bool BlockSignIn { get; set; } = true;
    public bool RevokeSessions { get; set; } = true;
    public bool RemoveLicenses { get; set; } = true;
    public bool RemoveFromGroups { get; set; } = true;

    /// <summary>Convert the user's mailbox to shared via Exchange Online (Phase 3).</summary>
    public bool ConvertMailboxToShared { get; set; }
    /// <summary>Optional SMTP address to forward the mailbox to during offboarding.</summary>
    public string? ForwardingSmtpAddress { get; set; }
}

/// <summary>One step in a multi-step provisioning/offboarding run, recorded for the UI.</summary>
public record ProvisioningStep(string Name, bool Success, string? Detail = null);

public class ProvisioningResult
{
    public string? UserId { get; set; }
    public string? UserPrincipalName { get; set; }
    /// <summary>The generated password, surfaced once when the caller did not supply one.</summary>
    public string? InitialPassword { get; set; }
    public List<ProvisioningStep> Steps { get; set; } = new();
    public bool Succeeded => Steps.All(s => s.Success);
}

/// <summary>A directory license SKU available in a tenant.</summary>
public record SkuSummary(string SkuId, string SkuPartNumber, int Enabled, int Consumed);

/// <summary>A directory principal used to populate pickers.</summary>
public record DirectoryObject(string Id, string DisplayName, string? UserPrincipalName = null);

/// <summary>
/// Per-tenant identity operations over Microsoft Graph: create/offboard users plus the directory
/// lookups (SKUs, groups, users) the provisioning UI needs.
/// </summary>
public interface IGraphUserService
{
    Task<ProvisioningResult> CreateUserAsync(Tenant tenant, NewHireRequest request, CancellationToken ct = default);
    Task<ProvisioningResult> TerminateUserAsync(Tenant tenant, TerminationRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<SkuSummary>> ListSkusAsync(Tenant tenant, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryObject>> ListGroupsAsync(Tenant tenant, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryObject>> ListUsersAsync(Tenant tenant, string? search = null, CancellationToken ct = default);
}
