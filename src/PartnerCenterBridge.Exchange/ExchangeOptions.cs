namespace PartnerCenterBridge.Exchange;

/// <summary>
/// Configuration for Exchange Online app-only certificate auth (a certificate-store thumbprint or a
/// PFX file). Bound from the <c>Exchange</c> section. The username/password (<c>-Credential</c>)
/// path is intentionally unsupported — it is retired in the EXO module as of July 2026; app-only
/// certificate is the sanctioned automation path.
/// </summary>
public class ExchangeOptions
{
    public const string SectionName = "Exchange";

    /// <summary>Entra app (client) id that holds Exchange.ManageAsApp + the Exchange Administrator role.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// Thumbprint of the app-only certificate in the Windows certificate store: the personal store
    /// (<c>Cert:\CurrentUser\My</c>) of the account the bridge runs as. Preferred on Windows: when
    /// set, Exchange connects with -CertificateThumbprint, <see cref="CertificatePath"/> and
    /// <see cref="CertificatePassword"/> are ignored, and no certificate secret is handled at all.
    /// Windows only (an EXO module restriction).
    /// </summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>Path to the PFX certificate used for app-only auth (mounted secret). Ignored when <see cref="CertificateThumbprint"/> is set.</summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>
    /// Optional password protecting the PFX. Passed to pwsh only on standard input, never on the
    /// command line or in a file.
    /// </summary>
    public string? CertificatePassword { get; set; }

    /// <summary>Path to the pwsh 7 executable. Defaults to whatever is on PATH.</summary>
    public string PwshPath { get; set; } = "pwsh";

    /// <summary>Per-operation timeout; EXO connects can be slow.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}
