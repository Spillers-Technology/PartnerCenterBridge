namespace PartnerCenterBridge.PartnerCenter;

/// <summary>
/// Configuration for the multi-tenant Entra app used under the Secure Application Model. Bound
/// from the <c>Partner</c> config section; the client secret and seed refresh token are supplied
/// via protected config / SOPS-managed secrets, never committed.
/// </summary>
public class PartnerOptions
{
    public const string SectionName = "Partner";

    /// <summary>Application (client) id of the multi-tenant SAM app registration.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Client secret for the confidential client.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>The partner (MSP) tenant id — the home tenant of the SAM app.</summary>
    public string PartnerTenantId { get; set; } = string.Empty;

    /// <summary>
    /// Optional seed refresh token used to bootstrap the store on first run if none is persisted.
    /// After bootstrap the token is rotated on every use and kept in the encrypted store.
    /// </summary>
    public string? SeedRefreshToken { get; set; }
}
