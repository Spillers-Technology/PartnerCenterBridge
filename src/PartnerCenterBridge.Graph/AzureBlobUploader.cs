using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace PartnerCenterBridge.Graph;

/// <summary>
/// Uploads the encrypted payload to the Intune-provided Azure Blob SAS URI as a block blob:
/// stream the file in fixed-size blocks (PUT block), then commit the ordered block list.
/// </summary>
internal sealed class AzureBlobUploader
{
    // Intune's documented guidance is to stay at/below ~6 MiB per block.
    private const int BlockSize = 6 * 1024 * 1024;

    private readonly HttpClient _http;

    public AzureBlobUploader(HttpClient http) => _http = http;

    /// <summary>
    /// Upload <paramref name="payload"/> to <paramref name="sasUri"/>. <paramref name="renewSasAsync"/>
    /// is invoked to obtain a fresh SAS if the current one nears expiry mid-upload.
    /// </summary>
    public async Task UploadAsync(
        Stream payload,
        string sasUri,
        Func<CancellationToken, Task<string>> renewSasAsync,
        CancellationToken ct)
    {
        var blockIds = new List<string>();
        var buffer = new byte[BlockSize];
        var index = 0;
        var sasIssued = DateTimeOffset.UtcNow;

        int read;
        while ((read = await ReadBlockAsync(payload, buffer, ct)) > 0)
        {
            // Refresh the SAS proactively for long uploads (SAS lifetime is short).
            if (DateTimeOffset.UtcNow - sasIssued > TimeSpan.FromMinutes(5))
            {
                sasUri = await renewSasAsync(ct);
                sasIssued = DateTimeOffset.UtcNow;
            }

            var blockId = Convert.ToBase64String(Encoding.UTF8.GetBytes($"block-{index:D6}"));
            blockIds.Add(blockId);

            var putBlockUri = $"{sasUri}&comp=block&blockid={Uri.EscapeDataString(blockId)}";
            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var req = new HttpRequestMessage(HttpMethod.Put, putBlockUri) { Content = content };
            req.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            index++;
        }

        await CommitBlockListAsync(sasUri, blockIds, ct);
    }

    private static async Task<int> ReadBlockAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private async Task CommitBlockListAsync(string sasUri, IEnumerable<string> blockIds, CancellationToken ct)
    {
        var xml = new XElement("BlockList", blockIds.Select(id => new XElement("Latest", id)));
        var body = new XDeclaration("1.0", "utf-8", null) + xml.ToString();

        var uri = $"{sasUri}&comp=blocklist";
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");
        using var req = new HttpRequestMessage(HttpMethod.Put, uri) { Content = content };
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}
