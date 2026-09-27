namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// A locally registered operator account (email + password), used when the deployment runs
/// without an external OIDC provider (<c>Auth:Mode=Local</c>). Independent of the Authentik/OIDC
/// operator plane described in the README — a deployment picks one mode, not both, per user.
/// </summary>
/// <remarks>
/// Registration itself is intentionally frictionless (no invite code, no admin approval) so a
/// solo technician can stand up the bridge and log in without provisioning an IdP first. What a
/// freshly registered account can *do* is the actual gate: it starts with zero
/// <see cref="TenantAccessGrant"/>s, so it cannot act against any tenant until an owner shares
/// access with it. The first account receives the fixed <see cref="InstanceRole.Administrator"/>
/// instance role so it can delegate instance configuration without gaining implicit tenant power.
/// </remarks>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>ASP.NET Core <c>PasswordHasher&lt;AppUser&gt;</c> output (algorithm + salt + hash, one field).</summary>
    public required string PasswordHash { get; set; }

    /// <summary>
    /// Instance-wide configuration roles. These never bypass <see cref="TenantAccessGrant"/>;
    /// tenant access is a separate authorization plane.
    /// </summary>
    public InstanceRole InstanceRoles { get; set; } = InstanceRole.None;

    /// <summary>Incremented after every role replacement so stale admin-editor writes can be rejected.</summary>
    public long AuthorizationVersion { get; set; } = 1;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Security epoch carried in every Local JWT (access tokens and MCP PATs) this user is issued.
    /// Incrementing it invalidates every credential issued before, on the next request: the token
    /// validator compares the claim with this column in the same lookup that checks
    /// <see cref="IsActive"/>. Tokens without the claim count as epoch 0.
    /// </summary>
    public int SessionEpoch { get; set; }

    /// <summary>
    /// The Local Workbench's built-in no-account owner ("Skip -- use without an account"): created
    /// on first run instead of an administrator account and signed in only through the launch link
    /// the exe opens (a DPAPI-protected launch secret in the data root) -- never by password or
    /// passkey. Cleared when the owner protects the workbench with an email and password, after
    /// which the row is an ordinary Local account. Never usable under the Server hosting profile.
    /// </summary>
    public bool IsWorkbenchOwner { get; set; }

    public int FailedLoginCount { get; set; }

    /// <summary>Set when <see cref="FailedLoginCount"/> crosses the lockout threshold; cleared on next successful login.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastLoginAt { get; set; }

    // --- TOTP (RFC 6238) second factor -----------------------------------------------------
    // TotpSecret is Data-Protection-encrypted at rest (same pattern as ProtectedSamTokenStore):
    // whoever holds it can generate valid codes, unlike a password hash. Recovery codes are
    // stored hashed (one-way, single-use) rather than encrypted -- there's nothing to decrypt
    // back to, they're just compared and consumed.
    public string? TotpSecretProtected { get; set; }
    public bool TotpEnabled { get; set; }
    public List<string> TotpRecoveryCodeHashes { get; set; } = new();

    public ICollection<TenantAccessGrant> TenantAccessGrants { get; set; } = new List<TenantAccessGrant>();
    public ICollection<PasskeyCredential> PasskeyCredentials { get; set; } = new List<PasskeyCredential>();
    public ICollection<McpToken> McpTokens { get; set; } = new List<McpToken>();
}
