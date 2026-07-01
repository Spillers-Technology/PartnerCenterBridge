using System.IO.Compression;
using System.Text;
using PartnerCenterBridge.Graph;

namespace PartnerCenterBridge.Tests;

public class IntuneWinPackageReaderTests
{
    // A representative Detection.xml with the namespace the Content Prep Tool emits.
    private const string DetectionXml =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <ApplicationInfo xmlns="http://schemas.microsoft.com/Metadata" ToolVersion="1.8.4">
          <Name>7-Zip</Name>
          <UnencryptedContentSize>1048576</UnencryptedContentSize>
          <FileName>IntunePackage.intunewin</FileName>
          <SetupFile>7z.msi</SetupFile>
          <EncryptionInfo>
            <EncryptionKey>QUJD</EncryptionKey>
            <MacKey>REVG</MacKey>
            <InitializationVector>R0hJ</InitializationVector>
            <Mac>SktM</Mac>
            <ProfileIdentifier>ProfileVersion1</ProfileIdentifier>
            <FileDigest>TU5P</FileDigest>
            <FileDigestAlgorithm>SHA256</FileDigestAlgorithm>
          </EncryptionInfo>
        </ApplicationInfo>
        """;

    private static MemoryStream BuildPackage(byte[] payload)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var det = zip.CreateEntry("IntuneWinPackage/Metadata/Detection.xml");
            using (var s = det.Open()) s.Write(Encoding.UTF8.GetBytes(DetectionXml));

            var content = zip.CreateEntry("IntuneWinPackage/Contents/IntunePackage.intunewin");
            using (var s = content.Open()) s.Write(payload);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Reads_metadata_and_encryption_info()
    {
        var payload = Encoding.UTF8.GetBytes("encrypted-bytes-here");
        using var pkg = BuildPackage(payload);
        var reader = new IntuneWinPackageReader();

        var info = await reader.ReadMetadataAsync(pkg);

        Assert.Equal("7z.msi", info.FileName);
        Assert.Equal(1048576, info.Size);
        Assert.Equal(payload.Length, info.SizeEncrypted);
        Assert.Equal("QUJD", info.EncryptionKey);
        Assert.Equal("ProfileVersion1", info.ProfileIdentifier);
        Assert.Equal("SHA256", info.FileDigestAlgorithm);
    }

    [Fact]
    public async Task Opens_encrypted_payload_stream()
    {
        var payload = Encoding.UTF8.GetBytes("the-encrypted-payload");
        using var pkg = BuildPackage(payload);
        var reader = new IntuneWinPackageReader();

        await using var stream = await reader.OpenEncryptedPayloadAsync(pkg);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal(payload, copy.ToArray());
    }

    [Fact]
    public async Task Throws_on_non_intunewin_zip()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("readme.txt");
        ms.Position = 0;

        var reader = new IntuneWinPackageReader();
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadMetadataAsync(ms));
    }
}
