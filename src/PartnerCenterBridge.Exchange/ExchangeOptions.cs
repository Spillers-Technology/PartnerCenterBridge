namespace PartnerCenterBridge.Exchange;

/// <summary>
/// Configuration for Exchange Online app-only certificate auth. Bound from the <c>Exchange</c>
/// section. The username/password (<c>-Credential</c>) path is intentionally unsupported — it is
/// retired in the EXO module as of July 2026; app-only certificate is the sanctioned automation path.
/// </summary>
public class ExchangeOptions
{
    public const string SectionName = "Exchange";

    /// <summary>Entra app (client) id that holds Exchange.ManageAsApp + the Exchange Administrator role.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>Path to the PFX certificate used for app-only auth (mounted secret).</summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>Optional password protecting the PFX.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>Path to the pwsh 7 executable. Defaults to whatever is on PATH.</summary>
    public string PwshPath { get; set; } = "pwsh";

    /// <summary>Per-operation timeout; EXO connects can be slow.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}
