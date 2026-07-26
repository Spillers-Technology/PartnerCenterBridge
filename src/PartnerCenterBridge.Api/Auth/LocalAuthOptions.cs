namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// Configuration for <c>Auth:Mode=Local</c> -- self-registered email/password accounts instead of
/// an external OIDC provider. Bound from the <c>Auth:Local</c> config section; <see cref="SigningKey"/>
/// is a secret and must come from protected config / SOPS, never committed.
/// </summary>
public class LocalAuthOptions
{
    public const string SectionName = "Auth:Local";

    /// <summary>
    /// Symmetric key (base64, 32+ bytes) used to sign locally issued access tokens. Generate with
    /// e.g. <c>openssl rand -base64 32</c>. Rotating it invalidates every outstanding token.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenLifetimeHours { get; set; } = 12;

    /// <summary>Consecutive failed logins before an account is locked out.</summary>
    public int MaxFailedLogins { get; set; } = 10;

    public int LockoutMinutes { get; set; } = 15;

    public int MinPasswordLength { get; set; } = 12;
}
