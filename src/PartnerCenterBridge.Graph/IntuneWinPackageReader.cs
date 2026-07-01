using System.IO.Compression;
using System.Xml.Linq;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Graph;

/// <summary>
/// Reads a .intunewin package. The package is a plain zip produced by the Win32 Content Prep
/// Tool with two members of interest:
/// <list type="bullet">
///   <item><c>IntuneWinPackage/Metadata/Detection.xml</c> — sizing + AES/HMAC encryption info.</item>
///   <item><c>IntuneWinPackage/Contents/IntunePackage.intunewin</c> — the encrypted payload we upload.</item>
/// </list>
/// </summary>
public class IntuneWinPackageReader : IIntuneWinPackageReader
{
    private const string DetectionEntry = "Detection.xml";
    private const string PayloadEntry = "IntunePackage.intunewin";

    public Task<Win32ContentInfo> ReadMetadataAsync(Stream intuneWinPackage, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(intuneWinPackage, ZipArchiveMode.Read, leaveOpen: true);

        var detection = zip.Entries.FirstOrDefault(e => e.Name.Equals(DetectionEntry, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Not a valid .intunewin package: Detection.xml not found.");
        var payload = zip.Entries.FirstOrDefault(e => e.Name.Equals(PayloadEntry, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Not a valid .intunewin package: encrypted payload not found.");

        XDocument xml;
        using (var s = detection.Open())
            xml = XDocument.Load(s);

        var root = xml.Root ?? throw new InvalidDataException("Detection.xml is empty.");
        var ns = root.Name.Namespace;
        var enc = root.Element(ns + "EncryptionInfo")
            ?? throw new InvalidDataException("Detection.xml is missing EncryptionInfo.");

        string Req(XElement parent, string name) =>
            parent.Element(ns + name)?.Value
            ?? throw new InvalidDataException($"Detection.xml is missing {name}.");

        var info = new Win32ContentInfo
        {
            FileName = Req(root, "SetupFile"),
            Size = long.Parse(Req(root, "UnencryptedContentSize")),
            SizeEncrypted = payload.Length,
            EncryptionKey = Req(enc, "EncryptionKey"),
            MacKey = Req(enc, "MacKey"),
            InitializationVector = Req(enc, "InitializationVector"),
            Mac = Req(enc, "Mac"),
            ProfileIdentifier = Req(enc, "ProfileIdentifier"),
            FileDigest = Req(enc, "FileDigest"),
            FileDigestAlgorithm = Req(enc, "FileDigestAlgorithm"),
        };
        return Task.FromResult(info);
    }

    public async Task<Stream> OpenEncryptedPayloadAsync(Stream intuneWinPackage, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(intuneWinPackage, ZipArchiveMode.Read, leaveOpen: true);
        var payload = zip.Entries.FirstOrDefault(e => e.Name.Equals(PayloadEntry, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Not a valid .intunewin package: encrypted payload not found.");

        // Copy to a temp file that deletes itself on close so the stream can outlive the archive
        // without holding the whole (potentially large) payload in memory.
        var temp = new FileStream(
            Path.Combine(Path.GetTempPath(), $"pcb-{Guid.NewGuid():N}.bin"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        await using (var entryStream = payload.Open())
            await entryStream.CopyToAsync(temp, ct);

        temp.Position = 0;
        return temp;
    }
}
