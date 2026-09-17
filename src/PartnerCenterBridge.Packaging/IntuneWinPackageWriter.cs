using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace PartnerCenterBridge.Packaging;

/// <summary>
/// What a build produced: the values written into <c>Detection.xml</c> plus the artifact's own
/// digest. Deliberately not <c>Win32ContentInfo</c> -- this project takes no dependency on Core
/// so it can be lifted out whole (spec §9, corporate-strategy D-0043).
/// </summary>
public sealed record IntuneWinBuildResult(
    string SetupFile,
    long UnencryptedContentSize,
    long EncryptedPayloadSize,
    string EncryptionKey,
    string MacKey,
    string InitializationVector,
    string Mac,
    string ProfileIdentifier,
    string FileDigest,
    string FileDigestAlgorithm,
    string ArtifactSha256,
    string BuilderVersion);

/// <summary>
/// Produces a <c>.intunewin</c> package from a source folder -- the in-process replacement for
/// Microsoft's Win32 Content Prep Tool. See <c>docs/specs/win32-package-lifecycle.md</c> §3.
/// </summary>
public interface IIntuneWinPackageWriter
{
    /// <summary>
    /// Package every file under <paramref name="sourceFolder"/> and write the resulting
    /// <c>.intunewin</c> to <paramref name="output"/>. <paramref name="setupFile"/> is a file name
    /// at the root of <paramref name="sourceFolder"/>. <paramref name="output"/> is only ever
    /// written asynchronously and is left open.
    /// </summary>
    Task<IntuneWinBuildResult> WriteAsync(
        string sourceFolder, string setupFile, Stream output, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IIntuneWinPackageWriter"/>. Payload layout (spec §3, matching Microsoft's
/// published LOB encryption sample): <c>MAC[32] || IV[16] || AES-256-CBC(PKCS7, innerZip)</c>,
/// where the HMAC-SHA256 covers <c>IV || ciphertext</c> and <c>FileDigest</c> is SHA-256 of the
/// unencrypted inner zip.
///
/// Every build draws fresh keys, so two builds of identical inputs are different artifacts
/// (spec §3.1). Media is streamed through scratch files rather than buffered, since install
/// media runs to gigabytes.
///
/// <para><b>Trust boundary (spec §7).</b> The source folder must be a staging directory PCB
/// itself created and populated with regular files -- not a directory anyone else can write to.
/// The writer rejects symbolic links, Windows-invalid or colliding names, and any group- or
/// other-writable directory, but it is not a sandbox for a hostile filesystem. The managed
/// FileSystemInfo API does not expose file type or link count, so FIFOs and hardlinks pass; a
/// concurrent writer could swap a file between the walk and the open. Defending against those would
/// take openat/O_NOFOLLOW/fstat interop on the opened descriptor -- deliberately not done here. They
/// are excluded by who can write to staging, and the mode-bit check only partly enforces that (it
/// does not verify ownership, ancestors, or same-user writers).</para>
/// </summary>
public sealed class IntuneWinPackageWriter : IIntuneWinPackageWriter
{
    /// <summary>
    /// Identifies this builder's output format. Part of a release's identity (spec §4), so bump it
    /// whenever the bytes this class produces for the same inputs would change in kind.
    /// </summary>
    public const string Version = "pcb-intunewin/1";

    public const string ProfileIdentifier = "ProfileVersion1";
    public const string DigestAlgorithm = "SHA256";
    public const string MetadataEntry = "IntuneWinPackage/Metadata/Detection.xml";
    public const string PayloadEntry = "IntuneWinPackage/Contents/IntunePackage.intunewin";

    /// <summary>
    /// Upper bound on entries (files and directories) in one source tree; bounds memory and time
    /// spent walking, including a tree made only of empty directories.
    /// </summary>
    public const int MaxEntries = 100_000;

    private const string PayloadFileName = "IntunePackage.intunewin";
    private const int KeySize = 32;
    private const int IvSize = 16;
    private const int MacSize = 32;
    private const int BufferSize = 81920;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode SharedWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private static readonly char[] InvalidNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    // Windows' documented reserved device names, including COM0/LPT0 and the superscript-digit
    // forms (U+00B9, U+00B2, U+00B3), which Windows also treats as devices. Escaped to keep the
    // literals ASCII-only (CLAUDE.md).
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    private readonly string _scratchRoot;

    /// <summary>
    /// Test seam: called with the path of each scratch file right after it is created, before any
    /// plaintext is written to it. Lets tests observe scratch permissions and the peak number of
    /// live scratch files without a filesystem watcher.
    /// </summary>
    internal Action<string>? ScratchCreated { get; init; }

    /// <summary>Test seam: lowers <see cref="MaxEntries"/> so the bound can be exercised cheaply.</summary>
    internal int EntryLimit { get; init; } = MaxEntries;

    /// <param name="scratchPath">
    /// Root under which each build creates its own owner-only (0700) working directory. Those hold
    /// customer install media in the clear while a build runs (spec §5, §7).
    /// </param>
    public IntuneWinPackageWriter(string? scratchPath = null)
    {
        _scratchRoot = scratchPath ?? Path.GetTempPath();
        Directory.CreateDirectory(_scratchRoot);
    }

    public async Task<IntuneWinBuildResult> WriteAsync(
        string sourceFolder, string setupFile, Stream output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var root = ResolveSourceRoot(sourceFolder);
        ValidateSetupFile(root, setupFile);
        var files = CollectFiles(root, EntryLimit, ct);

        var buildDir = CreatePrivateDirectory(Path.Combine(_scratchRoot, $"pcb-build-{Guid.NewGuid():N}"));
        FileStream? outer = null;
        try
        {
            // Each scratch file is disposed (and so deleted) as soon as the next stage has consumed
            // it, so no more than two source-sized files are ever on disk at once (spec §5 budgets
            // 2x): inner + payload while encrypting, payload + outer while assembling, then outer
            // alone while copying out.
            IntuneWinBuildResult result;
            await using (var payload = OpenScratch(buildDir, "payload"))
            {
                var key = RandomNumberGenerator.GetBytes(KeySize);
                var iv = RandomNumberGenerator.GetBytes(IvSize);
                var macKey = RandomNumberGenerator.GetBytes(KeySize);
                long unencryptedSize;
                byte[] mac, fileDigest;

                await using (var innerZip = OpenScratch(buildDir, "inner"))
                {
                    await WriteInnerZipAsync(files, innerZip, ct);
                    unencryptedSize = innerZip.Length;
                    innerZip.Position = 0;
                    (mac, fileDigest) = await EncryptAsync(innerZip, payload, key, iv, macKey, ct);
                }

                result = new IntuneWinBuildResult(
                    SetupFile: setupFile,
                    UnencryptedContentSize: unencryptedSize,
                    EncryptedPayloadSize: payload.Length,
                    EncryptionKey: Convert.ToBase64String(key),
                    MacKey: Convert.ToBase64String(macKey),
                    InitializationVector: Convert.ToBase64String(iv),
                    Mac: Convert.ToBase64String(mac),
                    ProfileIdentifier: ProfileIdentifier,
                    FileDigest: Convert.ToBase64String(fileDigest),
                    FileDigestAlgorithm: DigestAlgorithm,
                    ArtifactSha256: string.Empty,
                    BuilderVersion: Version);

                payload.Position = 0;
                outer = OpenScratch(buildDir, "outer");
                await WriteOuterZipAsync(result, payload, outer, ct);
            }

            outer.Position = 0;
            var artifactDigest = await CopyHashedAsync(outer, output, ct);
            return result with { ArtifactSha256 = Convert.ToHexString(artifactDigest).ToLowerInvariant() };
        }
        finally
        {
            try
            {
                if (outer is not null)
                    await outer.DisposeAsync();
            }
            finally
            {
                // Scratch files delete themselves on dispose; this removes the per-build directory,
                // even if disposing the outer zip threw while flushing.
                try { Directory.Delete(buildDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string ResolveSourceRoot(string sourceFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        var root = Path.GetFullPath(sourceFolder);
        var info = new DirectoryInfo(root);
        if (!info.Exists)
            throw new DirectoryNotFoundException($"Source folder '{root}' does not exist.");
        if (info.LinkTarget is not null)
            throw new InvalidOperationException($"Source folder '{root}' is a symbolic link.");
        return root;
    }

    /// <summary>
    /// The setup file must be a single valid Windows file name at the root of the source folder.
    /// Nested setup paths are rejected rather than guessed at: how the Content Prep Tool records a
    /// nested <c>SetupFile</c> is unverified (spec §11), and almost every package has setup at root.
    /// </summary>
    private static void ValidateSetupFile(string root, string setupFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupFile);
        var problem = WindowsNameProblem(setupFile);
        if (problem is not null)
            throw new ArgumentException(
                $"Setup file '{setupFile}' must be a single file name at the root of the source folder: {problem}.",
                nameof(setupFile));
        if (!File.Exists(Path.Combine(root, setupFile)))
            throw new FileNotFoundException($"Setup file '{setupFile}' was not found in the source folder.");
    }

    /// <summary>
    /// Walk the source folder by hand so every entry is seen before it is packaged. Entry names are
    /// built from validated path segments -- never by rewriting separators -- because the package
    /// is extracted on Windows: a Linux file literally named <c>..\x.exe</c> must be refused, not
    /// turned into a traversal entry.
    /// </summary>
    private static List<(string FullPath, string EntryName)> CollectFiles(string root, int entryLimit, CancellationToken ct)
    {
        var files = new List<(string, string)>();
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(DirectoryInfo Dir, string Prefix)>();
        pending.Push((new DirectoryInfo(root), ""));
        var entries = 0;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, prefix) = pending.Pop();
            RejectSharedWritable(dir, root);

            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > entryLimit)
                    throw new InvalidOperationException(
                        $"Source folder has more than {entryLimit} entries (files and directories).");
                var relative = prefix + entry.Name;
                if (entry.LinkTarget is not null)
                    throw new InvalidOperationException(
                        $"Source folder contains a symbolic link '{relative}'; refusing to package it.");
                if (WindowsNameProblem(entry.Name) is { } problem)
                    throw new InvalidOperationException(
                        $"Source folder contains '{relative}', which is not a valid Windows name: {problem}.");

                if (entry is DirectoryInfo sub)
                {
                    if (fileNames.Contains(relative) || !dirNames.Add(relative))
                        throw new InvalidOperationException(
                            $"Source folder contains names that collide on Windows (case-insensitive): '{relative}'.");
                    pending.Push((sub, relative + "/"));
                }
                else
                {
                    if (dirNames.Contains(relative) || !fileNames.Add(relative))
                        throw new InvalidOperationException(
                            $"Source folder contains names that collide on Windows (case-insensitive): '{relative}'.");
                    files.Add((entry.FullName, relative));
                }
            }
        }

        if (files.Count == 0)
            throw new InvalidOperationException("Source folder contains no files.");
        files.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        return files;
    }

    /// <summary>
    /// Enforces the trust boundary this class relies on: a directory anyone but the owner can write
    /// to is not a staging area PCB controls, so files in it could be swapped mid-build.
    /// </summary>
    private static void RejectSharedWritable(DirectoryInfo dir, string root)
    {
        if (OperatingSystem.IsWindows())
            return;
        if ((dir.UnixFileMode & SharedWrite) != 0)
            throw new InvalidOperationException(
                $"Source directory '{Path.GetRelativePath(root, dir.FullName)}' is writable by group or other; " +
                "packaging requires a private staging directory.");
    }

    /// <summary>
    /// Why <paramref name="name"/> is not a valid single Windows path segment, or null. Control
    /// characters follow Windows' own rule (U+0000-U+001F only), so DEL and C1 controls are
    /// accepted exactly as NTFS accepts them.
    /// </summary>
    private static string? WindowsNameProblem(string name)
    {
        if (name.Length == 0 || name is "." or "..")
            return "empty or a relative-directory name";
        if (name.Length > 255)
            return "longer than 255 characters";
        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(c => c < ' '))
            return "contains a path separator or a character Windows does not allow";
        if (name.EndsWith('.') || name.EndsWith(' '))
            return "ends with a dot or a space";
        // Windows' shell and many installers strip leading spaces, so " a.ini" and "a.ini" can
        // collide on extraction even though the case-insensitive set treats them as distinct.
        if (name.StartsWith(' '))
            return "starts with a space";
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem))
            return "is a reserved Windows device name";
        return null;
    }

    private static async Task WriteInnerZipAsync(
        List<(string FullPath, string EntryName)> files, Stream destination, CancellationToken ct)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (fullPath, entryName) in files)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = ClampToZipRange(File.GetLastWriteTime(fullPath));
            await using var source = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            await using var target = entry.Open();
            await source.CopyToAsync(target, BufferSize, ct);
        }
    }

    /// <summary>
    /// Zip timestamps cannot represent dates before 1980 or after 2107, and <see cref="ZipArchive"/>
    /// throws on them. Media extracted from ISOs and old vendor archives does carry such dates.
    /// </summary>
    private static DateTimeOffset ClampToZipRange(DateTime value)
    {
        var min = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var max = new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Local);
        return value < min ? min : value > max ? max : value;
    }

    /// <summary>
    /// Stream the inner zip through AES-256-CBC into <paramref name="payload"/>, hashing the
    /// plaintext (FileDigest) and <c>IV || ciphertext</c> (MAC) in the same pass, then write the
    /// MAC into the 32 bytes reserved at the front.
    /// </summary>
    private static async Task<(byte[] Mac, byte[] FileDigest)> EncryptAsync(
        Stream innerZip, Stream payload, byte[] key, byte[] iv, byte[] macKey, CancellationToken ct)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        payload.SetLength(0);
        await payload.WriteAsync(new byte[MacSize], ct);
        await payload.WriteAsync(iv, ct);
        hmac.AppendData(iv);

        using (var aes = Aes.Create())
        {
            aes.KeySize = KeySize * 8;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor(key, iv);

            var ciphertextSink = new HashingWriteStream(payload, hmac);
            await using var crypto = new CryptoStream(ciphertextSink, encryptor, CryptoStreamMode.Write, leaveOpen: true);

            var buffer = new byte[BufferSize];
            int read;
            while ((read = await innerZip.ReadAsync(buffer, ct)) > 0)
            {
                digest.AppendData(buffer, 0, read);
                await crypto.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await crypto.FlushFinalBlockAsync(ct);
        }

        var mac = hmac.GetHashAndReset();
        payload.Position = 0;
        await payload.WriteAsync(mac, ct);
        await payload.FlushAsync(ct);
        return (mac, digest.GetHashAndReset());
    }

    /// <summary>
    /// Assemble the outer zip in scratch. <see cref="ZipArchive"/> writes headers and the central
    /// directory synchronously; keeping that on a local scratch file means the caller's stream only
    /// ever sees asynchronous writes (<see cref="CopyHashedAsync"/>).
    /// </summary>
    private static async Task WriteOuterZipAsync(
        IntuneWinBuildResult result, Stream payload, Stream destination, CancellationToken ct)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var metadata = zip.CreateEntry(MetadataEntry, CompressionLevel.Optimal);
        await using (var s = metadata.Open())
            await BuildDetectionXml(result).SaveAsync(s, SaveOptions.None, ct);

        // Ciphertext does not compress; storing it avoids burning CPU on gigabytes for nothing.
        var content = zip.CreateEntry(PayloadEntry, CompressionLevel.NoCompression);
        await using (var s = content.Open())
            await payload.CopyToAsync(s, BufferSize, ct);
    }

    /// <summary>
    /// Copy the finished artifact to the caller, hashing exactly the bytes handed to it. If a write
    /// fails the exception propagates and no digest is returned -- a partially written destination
    /// must be discarded by the caller, never trusted.
    /// </summary>
    private static async Task<byte[]> CopyHashedAsync(Stream source, Stream output, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await output.FlushAsync(ct);
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Emits every field the repo's own reader requires, plus <c>Name</c> and <c>FileName</c>, which
    /// Microsoft's Win32 samples read (spec §3). <c>MsiInfo</c> is omitted: MSI property extraction
    /// is deferred past v1 (spec §8).
    /// </summary>
    private static XDocument BuildDetectionXml(IntuneWinBuildResult r) => new(
        new XDeclaration("1.0", "utf-8", null),
        new XElement("ApplicationInfo",
            new XElement("Name", r.SetupFile),
            new XElement("UnencryptedContentSize", r.UnencryptedContentSize),
            new XElement("FileName", PayloadFileName),
            new XElement("SetupFile", r.SetupFile),
            new XElement("EncryptionInfo",
                new XElement("EncryptionKey", r.EncryptionKey),
                new XElement("MacKey", r.MacKey),
                new XElement("InitializationVector", r.InitializationVector),
                new XElement("Mac", r.Mac),
                new XElement("ProfileIdentifier", r.ProfileIdentifier),
                new XElement("FileDigest", r.FileDigest),
                new XElement("FileDigestAlgorithm", r.FileDigestAlgorithm))));

    /// <summary>
    /// Each build's working directory is created owner-only (0700) atomically, so plaintext media
    /// staged in it is not readable by other local users even under a shared root like /tmp.
    /// </summary>
    private static string CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, OwnerOnlyDirectory);
        return path;
    }

    /// <summary>
    /// A scratch file created owner-only (0600) that deletes itself when disposed, including on the
    /// failure path. A hard process kill can still strand one; sweeping the scratch root at startup
    /// is the build queue's job (spec §5).
    /// </summary>
    private FileStream OpenScratch(string buildDir, string purpose)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.DeleteOnClose | FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnlyFile;
        var path = Path.Combine(buildDir, $"{purpose}.tmp");
        var stream = new FileStream(path, options);
        try
        {
            ScratchCreated?.Invoke(path);
        }
        catch
        {
            // The caller never receives the stream, so nothing else would dispose (and delete) it.
            stream.Dispose();
            throw;
        }
        return stream;
    }
}
