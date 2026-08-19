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
/// access with it. The one exception is <see cref="IsSystemAdmin"/>, granted automatically to the
/// very first account created on a fresh database so there is someone able to start granting
/// access to everyone else.
/// </remarks>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>ASP.NET Core <c>PasswordHasher&lt;AppUser&gt;</c> output (algorithm + salt + hash, one field).</summary>
    public required string PasswordHash { get; set; }

    /// <summary>
    /// Bypasses per-tenant grants entirely and can manage MSP-wide config (contracts, app
    /// templates, SAM bootstrap) and grant/revoke any user's tenant access. Set automatically for
    /// the first account created; every later admin promotion is a deliberate, audited action.
    /// </summary>
    public bool IsSystemAdmin { get; set; }

    public bool IsActive { get; set; } = true;

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
