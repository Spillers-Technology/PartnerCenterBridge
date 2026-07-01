namespace PartnerCenterBridge.Core.Abstractions;

/// <summary>
/// Persists the Secure Application Model refresh token at rest (encrypted). The token is seeded
/// once by an interactive, MFA'd admin bootstrap and then rotated on every use (MSAL returns a
/// fresh refresh token with each access token) well before its 90-day expiry.
/// </summary>
public interface ISamTokenStore
{
    /// <summary>The current SAM refresh token, or null if the bridge has not been bootstrapped.</summary>
    Task<string?> GetRefreshTokenAsync(CancellationToken ct = default);

    /// <summary>Persist a newly issued refresh token, replacing any prior value.</summary>
    Task SaveRefreshTokenAsync(string refreshToken, CancellationToken ct = default);
}
