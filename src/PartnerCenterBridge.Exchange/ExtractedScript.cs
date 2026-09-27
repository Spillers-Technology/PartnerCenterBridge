using System.Reflection;
using System.Security.Cryptography;

namespace PartnerCenterBridge.Exchange;

/// <summary>
/// A copy of an embedded script on disk for exactly one pwsh run. The file gets an unpredictable
/// name, is created exclusively (never opened if something already sits at that path, never
/// following a planted link), is owner-only on Unix, and is re-read and compared against the
/// embedded resource's SHA-256 before use. While the instance is alive it holds a read-only handle
/// that denies writers and deleters (enforced on Windows), so the verified content is what pwsh
/// executes. Disposing releases the handle and deletes the file.
/// </summary>
internal sealed class ExtractedScript : IDisposable
{
    private readonly FileStream _guard;

    public string Path { get; }

    private ExtractedScript(string path, FileStream guard)
    {
        Path = path;
        _guard = guard;
    }

    /// <summary>Extract embedded resource <paramref name="resourceName"/> of this assembly.</summary>
    public static ExtractedScript FromEmbeddedResource(string resourceName, string prefix, string? directory = null)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded script {resourceName} not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var content = buffer.ToArray();
        return Extract(content, SHA256.HashData(content), directory ?? System.IO.Path.GetTempPath(), prefix);
    }

    /// <summary>
    /// Write <paramref name="content"/> to a new private file in <paramref name="directory"/> and
    /// verify it against <paramref name="expectedSha256"/>; throws (and removes the file) on mismatch.
    /// </summary>
    internal static ExtractedScript Extract(byte[] content, byte[] expectedSha256, string directory, string prefix = "script")
    {
        var path = System.IO.Path.Combine(directory, $"pcb-{prefix}-{Guid.NewGuid():N}.ps1");
        var create = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var file = new FileStream(path, create))
            file.Write(content);

        FileStream? guard = null;
        try
        {
            // Read-only, shared only with readers: pwsh can open it, nothing can rewrite or delete it
            // until this handle is released.
            guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = SHA256.HashData(guard);
            if (!CryptographicOperations.FixedTimeEquals(actual, expectedSha256))
                throw new InvalidOperationException(
                    $"The extracted script '{path}' does not match the embedded copy; refusing to run it.");
            return new ExtractedScript(path, guard);
        }
        catch
        {
            guard?.Dispose();
            TryDelete(path);
            throw;
        }
    }

    public void Dispose()
    {
        _guard.Dispose();
        TryDelete(Path);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
