using System.Security.Cryptography.X509Certificates;

namespace PartnerCenterBridge.Exchange;

/// <summary>Finds the app-only certificate named by <see cref="ExchangeOptions.CertificateThumbprint"/>.</summary>
public static class CertificateStoreLookup
{
    /// <summary>
    /// True when a certificate with <paramref name="thumbprint"/> and a private key is in the
    /// current user's (or, failing that, the local machine's) personal store. Windows only.
    /// </summary>
    public static bool Contains(string thumbprint)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(thumbprint)) return false;
        var normalized = thumbprint.Replace(" ", "").Trim();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
                try
                {
                    if (found.Any(c => c.HasPrivateKey)) return true;
                }
                finally
                {
                    foreach (var c in found) c.Dispose();
                }
            }
            catch (System.Security.Cryptography.CryptographicException) { /* store not accessible */ }
        }
        return false;
    }
}
