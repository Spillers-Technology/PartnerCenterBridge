using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Abstractions;

/// <summary>
/// Parses a .intunewin package (an encrypted zip produced by the Win32 Content Prep Tool):
/// reads <c>Metadata/Detection.xml</c> for the encryption/sizing info and exposes the encrypted
/// payload stream that must be uploaded to Azure Blob.
/// </summary>
public interface IIntuneWinPackageReader
{
    /// <summary>
    /// Read the package's Detection.xml into a <see cref="Win32ContentInfo"/>. Does not extract
    /// the (potentially large) payload.
    /// </summary>
    Task<Win32ContentInfo> ReadMetadataAsync(Stream intuneWinPackage, CancellationToken ct = default);

    /// <summary>
    /// Open the encrypted payload (<c>IntunePackage.intunewin</c> inside the outer zip) for upload.
    /// The caller owns disposing the returned stream.
    /// </summary>
    Task<Stream> OpenEncryptedPayloadAsync(Stream intuneWinPackage, CancellationToken ct = default);
}
