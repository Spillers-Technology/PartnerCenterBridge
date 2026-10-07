namespace PartnerCenterBridge.Core.TenantAudits;

// Read models and provider contracts for audit checks. Implementations live with the API they
// call (Graph project: directory/devices/security; Exchange project: mailboxes), fetch each
// dataset once per tenant per run via AuditCheckContext.Cache, and throw AuditUnavailableException
// when PCB cannot read the data here. Checks depend only on these interfaces, so check logic is
// unit-testable with in-memory fakes and never sees an HTTP client.

/// <summary>
/// Microsoft Entra sign-in activity as reported on the user object (signInActivity). These are
/// different facts and are kept apart on purpose:
/// <list type="bullet">
/// <item><see cref="LastSuccessful"/> -- lastSuccessfulSignInDateTime: the last <em>successful</em>
/// interactive or non-interactive sign-in. Only recorded since December 2023.</item>
/// <item><see cref="LastInteractive"/> -- lastSignInDateTime: the last interactive sign-in
/// <em>attempt</em>, successful or not.</item>
/// <item><see cref="LastNonInteractive"/> -- lastNonInteractiveSignInDateTime: the last
/// non-interactive attempt (token refresh by an app or device), successful or not.</item>
/// </list>
/// All are null for an account that has never signed in since Microsoft started recording them.
/// </summary>
public sealed record AuditSignInActivity(DateTimeOffset? LastSuccessful, DateTimeOffset? LastInteractive, DateTimeOffset? LastNonInteractive)
{
    public DateTimeOffset? LastAttempt =>
        LastInteractive is null ? LastNonInteractive
        : LastNonInteractive is null ? LastInteractive
        : LastInteractive > LastNonInteractive ? LastInteractive : LastNonInteractive;

    public bool IsEmpty => LastSuccessful is null && LastInteractive is null && LastNonInteractive is null;
}

public sealed record AuditUser(
    string Id,
    string DisplayName,
    string? UserPrincipalName,
    // "Member" or "Guest".
    string UserType,
    bool AccountEnabled,
    DateTimeOffset? CreatedAt,
    bool OnPremisesSynced,
    // Guest invitation state: "PendingAcceptance", "Accepted", or null.
    string? ExternalUserState,
    DateTimeOffset? ExternalUserStateChangedAt,
    IReadOnlyList<string> AssignedSkuIds,
    // Null when sign-in activity could not be read for this tenant (see <see cref="AuditUserSet.SignInUnavailableReason"/>).
    AuditSignInActivity? SignIn)
{
    public bool IsGuest => string.Equals(UserType, "Guest", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The tenant's users. Sign-in activity needs extra permission (AuditLog.Read.All) and an Entra ID
/// P1/P2 license; when it cannot be read, the users still load and the reason is recorded here so
/// checks that need it can report themselves unavailable while the rest still run.
/// </summary>
public sealed class AuditUserSet
{
    public required IReadOnlyList<AuditUser> Users { get; init; }
    public string? SignInUnavailableReason { get; init; }
    public IReadOnlyList<string> SignInMissing { get; init; } = [];

    public bool SignInAvailable => SignInUnavailableReason is null;

    /// <summary>Throws <see cref="AuditUnavailableException"/> when sign-in activity is not readable.</summary>
    public void RequireSignIn()
    {
        if (SignInUnavailableReason is not null) throw new AuditUnavailableException(SignInUnavailableReason, SignInMissing);
    }
}

/// <summary>A subscribed SKU with seat counts (subscribedSkus).</summary>
public sealed record AuditSku(
    string SkuId, string SkuPartNumber, string? CapabilityStatus, string? AppliesTo,
    int Enabled, int Warning, int Suspended, int LockedOut, int Consumed);

/// <summary>
/// One active member of a directory role. Group members are expanded to the users in the group
/// (<see cref="ViaGroup"/> names the group). PIM-eligible assignments that are not activated are
/// not members and are not included.
/// </summary>
public sealed record AuditRoleMember(
    string RoleTemplateId, string RoleName, bool IsPrivileged,
    // "user", "servicePrincipal", or "group" (a group whose members could not be expanded).
    string PrincipalType, string PrincipalId, string PrincipalName, string? UserPrincipalName, string? ViaGroup);

/// <summary>A user's registered authentication methods (authentication methods registration report).</summary>
public sealed record AuditAuthRegistration(
    string UserId, string? UserPrincipalName, string? DisplayName, string? UserType,
    bool IsAdmin, bool IsMfaCapable, bool IsMfaRegistered, bool IsPasswordlessCapable,
    IReadOnlyList<string> MethodsRegistered);

public sealed record AuditDevice(
    string Id, string? DeviceName, string? OperatingSystem, string? OsVersion,
    // Intune complianceState: compliant, noncompliant, inGracePeriod, unknown, error, conflict, configManager.
    string? ComplianceState,
    DateTimeOffset? LastSync, bool? IsEncrypted, string? UserPrincipalName,
    // "company", "personal" or "unknown".
    string? OwnerType, DateTimeOffset? EnrolledAt, string? SerialNumber, string? ManagementAgent);

/// <summary>A device compliance policy and the platform its type targets.</summary>
public sealed record AuditCompliancePolicy(string Id, string Name, string Platform, int AssignmentCount);

/// <summary>
/// Intune tenant setting "Mark devices with no compliance policy assigned as": true means
/// "Not compliant" (secure by default); false means such devices report compliant.
/// </summary>
public sealed record AuditDeviceManagementSettings(bool? SecureByDefault);

/// <summary>A Conditional Access policy, trimmed to what the checks grade.</summary>
public sealed record AuditConditionalAccessPolicy(
    string Id, string DisplayName,
    // "enabled", "disabled", or "enabledForReportingButNotEnforced".
    string State,
    IReadOnlyList<string> IncludeUsers, IReadOnlyList<string> ExcludeUsers,
    IReadOnlyList<string> IncludeGroups, IReadOnlyList<string> ExcludeGroups,
    IReadOnlyList<string> IncludeRoles, IReadOnlyList<string> ExcludeRoles,
    bool ExcludesGuestsOrExternalUsers,
    IReadOnlyList<string> IncludeApplications, IReadOnlyList<string> ClientAppTypes,
    IReadOnlyList<string> BuiltInControls, bool RequiresAuthenticationStrength)
{
    public bool IsEnabled => State == "enabled";
    public bool IsReportOnly => State == "enabledForReportingButNotEnforced";
    public bool TargetsAllUsers => IncludeUsers.Contains("All", StringComparer.OrdinalIgnoreCase);
    public bool TargetsAllApps => IncludeApplications.Contains("All", StringComparer.OrdinalIgnoreCase);
    public bool RequiresMfa => RequiresAuthenticationStrength || BuiltInControls.Contains("mfa", StringComparer.OrdinalIgnoreCase);
    public bool Blocks => BuiltInControls.Contains("block", StringComparer.OrdinalIgnoreCase);
}

/// <summary>Conditional Access policies plus display names for the users/groups/roles they reference.</summary>
public sealed class AuditConditionalAccess
{
    public required IReadOnlyList<AuditConditionalAccessPolicy> Policies { get; init; }
    /// <summary>Object id -> display name for referenced users, groups and roles that could be resolved.</summary>
    public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();
}

/// <summary>The tenant authorization policy (directory-wide defaults for ordinary users and guests).</summary>
public sealed record AuditAuthorizationPolicy(
    // "none", "adminsAndGuestInviters", "adminsGuestInvitersAndAllMembers", or "everyone".
    string? AllowInvitesFrom,
    bool? UsersCanRegisterApps, bool? UsersCanCreateTenants, bool? UsersCanCreateSecurityGroups,
    // permissionGrantPolicyIdsAssignedToDefaultUserRole -- empty means users cannot consent to apps.
    IReadOnlyList<string> UserConsentPolicies,
    string? GuestUserRoleId);

/// <summary>A delegated permission grant (OAuth2 consent) with its client app resolved.</summary>
public sealed record AuditPermissionGrant(
    string Id, string ClientId, string ClientName, string? ClientPublisher, bool ClientIsMicrosoft,
    // "AllPrincipals" (admin consent for everyone) or "Principal" (one user consented).
    string ConsentType, string? PrincipalId, string ResourceName, IReadOnlyList<string> Scopes);

public interface IAuditDirectoryData
{
    Task<AuditUserSet> GetUsersAsync(AuditCheckContext context);
    Task<IReadOnlyList<AuditSku>> GetSubscribedSkusAsync(AuditCheckContext context);
    Task<IReadOnlyList<AuditRoleMember>> GetDirectoryRoleMembersAsync(AuditCheckContext context);
    Task<IReadOnlyList<AuditAuthRegistration>> GetAuthMethodRegistrationsAsync(AuditCheckContext context);
}

public interface IAuditDeviceData
{
    Task<IReadOnlyList<AuditDevice>> GetManagedDevicesAsync(AuditCheckContext context);
    Task<IReadOnlyList<AuditCompliancePolicy>> GetCompliancePoliciesAsync(AuditCheckContext context);
    Task<AuditDeviceManagementSettings> GetDeviceManagementSettingsAsync(AuditCheckContext context);
}

public interface IAuditSecurityData
{
    Task<AuditConditionalAccess> GetConditionalAccessAsync(AuditCheckContext context);
    /// <summary>Whether Microsoft Entra security defaults are enabled.</summary>
    Task<bool> GetSecurityDefaultsEnabledAsync(AuditCheckContext context);
    Task<AuditAuthorizationPolicy> GetAuthorizationPolicyAsync(AuditCheckContext context);
    Task<IReadOnlyList<AuditPermissionGrant>> GetDelegatedPermissionGrantsAsync(AuditCheckContext context);
}

// --- Exchange Online --------------------------------------------------------------------------

public sealed record AuditMailbox(
    // Entra object id of the mailbox's user object (ExternalDirectoryObjectId), for joining with Graph data.
    string? ObjectId,
    string UserPrincipalName, string DisplayName, string? PrimarySmtpAddress,
    // UserMailbox, SharedMailbox, RoomMailbox, EquipmentMailbox, ...
    string RecipientTypeDetails,
    string? ForwardingSmtpAddress,
    // ForwardingAddress (an Exchange recipient), resolved to its SMTP address when possible.
    string? ForwardingAddress, string? ForwardingAddressSmtp,
    bool DeliverToMailboxAndForward,
    bool ArchiveEnabled, string? ArchiveStatus, bool AutoExpandingArchiveEnabled, bool LitigationHoldEnabled,
    IReadOnlyList<string> GrantSendOnBehalfTo)
{
    public bool IsShared => RecipientTypeDetails.Equals("SharedMailbox", StringComparison.OrdinalIgnoreCase);
    public bool IsResource => RecipientTypeDetails is "RoomMailbox" or "EquipmentMailbox";
}

/// <summary>A non-inherited mailbox permission: FullAccess (mailbox permission) or SendAs (recipient permission).</summary>
public sealed record AuditMailboxPermission(string Mailbox, string Trustee, string Right);

/// <summary>Everything the Exchange checks read, from one read-only EXO session.</summary>
public sealed class AuditMailboxReport
{
    public required IReadOnlyList<AuditMailbox> Mailboxes { get; init; }
    public IReadOnlyList<string> AcceptedDomains { get; init; } = [];
    /// <summary>Default outbound spam policy AutoForwardingMode: "Automatic", "On" or "Off". Null if unreadable.</summary>
    public string? AutoForwardingMode { get; init; }
    public IReadOnlyList<AuditMailboxPermission> Permissions { get; init; } = [];
    /// <summary>Mailboxes whose FullAccess permissions were read (the read is bounded in large tenants).</summary>
    public int FullAccessEvaluated { get; init; }
    /// <summary>False when FullAccess was not read for every mailbox.</summary>
    public bool FullAccessComplete { get; init; }
    /// <summary>Parts of the read that failed while the rest succeeded, e.g. "SendAs permissions: access denied".</summary>
    public IReadOnlyList<string> PartialErrors { get; init; } = [];
}

public interface IAuditMailboxData
{
    /// <summary>
    /// Whether Exchange Online audit data can be read on this PCB instance at all (cheap, local).
    /// Null when available, otherwise the reason. Lets other checks enrich with mailbox types only
    /// when doing so will not just fail.
    /// </summary>
    string? UnavailableReason { get; }

    Task<AuditMailboxReport> GetMailboxReportAsync(AuditCheckContext context);
}
