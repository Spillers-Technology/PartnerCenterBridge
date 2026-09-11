using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Xml.Linq;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Packaging;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// Acceptance tests for the .intunewin writer, per docs/specs/win32-package-lifecycle.md §3.0.
///
/// The repo's own <see cref="IntuneWinPackageReader"/> never decrypts or authenticates anything,
/// so a round trip through it cannot tell a correct writer from one that emits plaintext. Every
/// correctness claim here is instead checked by <see cref="IndependentDecryptor"/>, written from
/// the format contract rather than from the writer, and sharing no code with it.
///
/// Still open under §3.0: <see cref="Reference_tool_fixture_decrypts_with_the_same_oracle"/> is
/// written but skipped until a package produced by Microsoft's IntuneWinAppUtil.exe is checked in;
/// until then the oracle is independent of the writer in implementation but not calibrated
/// against the reference tool. A real Graph commit + Windows endpoint install is separately
/// required before Stable. Offline tests prove construction, not deployability. Tests that need
/// names only Unix filesystems permit, or Unix permission APIs, use <see cref="UnixFactAttribute"/>
/// and <see cref="UnixTheoryAttribute"/> and report as skipped on Windows rather than failing.
/// </summary>
public sealed class IntuneWinPackageWriterTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("pcb-writer-test-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string NewSourceFolder(params (string RelativePath, byte[] Content)[] files)
    {
        var dir = Path.Combine(_work, "src-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
        return dir;
    }

    private async Task<(byte[] Package, IntuneWinBuildResult Result)> BuildAsync(string source, string setup = "setup.exe")
    {
        var writer = new IntuneWinPackageWriter(Path.Combine(_work, "scratch"));
        using var output = new MemoryStream();
        var result = await writer.WriteAsync(source, setup, output);
        return (output.ToArray(), result);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    // ---- Correctness, checked independently ------------------------------------------------

    [Fact]
    public async Task Package_decrypts_authenticates_and_reproduces_every_source_file()
    {
        var files = new (string, byte[])[]
        {
            ("setup.exe", Bytes(300_000, 1)),
            ("config/settings.json", "{\"silent\":true}"u8.ToArray()),
            ("config/nested/deep.bin", Bytes(4_097, 2)),
            ("empty.txt", Array.Empty<byte>()),
        };
        var (package, _) = await BuildAsync(NewSourceFolder(files));

        var opened = IndependentDecryptor.Open(package);

        Assert.Equal(files.Length, opened.Files.Count);
        foreach (var (rel, content) in files)
            Assert.Equal(content, opened.Files[rel]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_024)]
    [InlineData(65_537)]
    public async Task Ciphertext_length_follows_pkcs7_for_any_content_size(int size)
    {
        // PKCS7 always adds 1-16 bytes, so an aligned plaintext still gains a full block. A writer
        // that skipped padding, or used zero padding, fails this for every size, not just aligned ones.
        var (package, _) = await BuildAsync(NewSourceFolder(("setup.exe", Bytes(size, size))));
        var (_, payload) = IndependentDecryptor.ReadRaw(package);
        var plainLength = IndependentDecryptor.Open(package).InnerZip.Length;

        Assert.Equal((plainLength / 16 + 1) * 16, payload.Length - 48);
    }

    [Fact]
    public async Task Payload_layout_is_mac_then_iv_then_ciphertext_with_correct_key_lengths()
    {
        var (package, _) = await BuildAsync(NewSourceFolder(("setup.exe", Bytes(5_000, 3))));
        var (meta, payload) = IndependentDecryptor.ReadRaw(package);

        var key = Convert.FromBase64String(meta.EncryptionKey);
        var macKey = Convert.FromBase64String(meta.MacKey);
        var iv = Convert.FromBase64String(meta.InitializationVector);
        var mac = Convert.FromBase64String(meta.Mac);

        Assert.Equal(32, key.Length);
        Assert.Equal(32, macKey.Length);
        Assert.Equal(16, iv.Length);
        Assert.Equal(32, mac.Length);
        Assert.Equal(mac, payload[..32]);
        Assert.Equal(iv, payload[32..48]);
        Assert.Equal(0, (payload.Length - 48) % 16);
    }

    [Fact]
    public async Task Detection_xml_carries_the_full_metadata_contract()
    {
        var source = NewSourceFolder(("setup.exe", Bytes(2_000, 4)));
        var (package, result) = await BuildAsync(source);
        var (meta, payload) = IndependentDecryptor.ReadRaw(package);
        var plaintext = IndependentDecryptor.Open(package).InnerZip;

        Assert.Equal("setup.exe", meta.Name);
        Assert.Equal("setup.exe", meta.SetupFile);
        Assert.Equal("IntunePackage.intunewin", meta.FileName);
        Assert.Equal("ProfileVersion1", meta.ProfileIdentifier);
        Assert.Equal("SHA256", meta.FileDigestAlgorithm);
        Assert.Equal(plaintext.Length, meta.UnencryptedContentSize);
        Assert.Equal(payload.Length, result.EncryptedPayloadSize);
    }

    // ---- Tamper evidence -------------------------------------------------------------------

    public static TheoryData<string> Corruptions => new() { "ciphertext", "iv", "mac", "truncated" };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task Any_payload_corruption_fails_authentication(string corruption)
    {
        var (package, _) = await BuildAsync(NewSourceFolder(("setup.exe", Bytes(10_000, 5))));
        IndependentDecryptor.Open(package); // the untampered package must be valid, or this proves nothing
        var tampered = IndependentDecryptor.RewritePayload(package, payload => corruption switch
        {
            "ciphertext" => Flip(payload, payload.Length - 20),
            "iv" => Flip(payload, 40),
            "mac" => Flip(payload, 3),
            "truncated" => payload[..^16],
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        });

        var ex = Assert.Throws<CryptographicException>(() => IndependentDecryptor.Open(tampered));
        Assert.Equal(IndependentDecryptor.MacFailure, ex.Message); // rejected by authentication, not by luck later
    }

    private static byte[] Flip(byte[] data, int index)
    {
        var copy = (byte[])data.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    // ---- Non-reproducibility and artifact identity (spec §3.1) -----------------------------

    [Fact]
    public async Task Every_build_uses_fresh_keys_so_identical_inputs_give_different_artifacts()
    {
        var source = NewSourceFolder(("setup.exe", Bytes(8_000, 6)));
        var (first, a) = await BuildAsync(source);
        var (second, b) = await BuildAsync(source);

        Assert.NotEqual(a.EncryptionKey, a.MacKey);
        Assert.NotEqual(a.EncryptionKey, b.EncryptionKey);
        Assert.NotEqual(a.MacKey, b.MacKey);
        Assert.NotEqual(a.InitializationVector, b.InitializationVector);
        Assert.NotEqual(a.ArtifactSha256, b.ArtifactSha256);
        // Both are still correct representations of the same content.
        Assert.Equal(IndependentDecryptor.Open(first).Files["setup.exe"], IndependentDecryptor.Open(second).Files["setup.exe"]);
    }

    [Fact]
    public async Task Artifact_digest_matches_the_bytes_actually_written()
    {
        var (package, result) = await BuildAsync(NewSourceFolder(("setup.exe", Bytes(50_000, 7))));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(), result.ArtifactSha256);
        Assert.Equal(IntuneWinPackageWriter.Version, result.BuilderVersion);
    }

    [Fact]
    public async Task Writing_to_a_seekable_file_gives_the_same_digest_as_the_bytes_on_disk()
    {
        // Guards the non-seekable wrapping: if ZipArchive ever seeks back on a FileStream, the
        // running digest and the file contents would disagree.
        var source = NewSourceFolder(("setup.exe", Bytes(20_000, 8)));
        var target = Path.Combine(_work, "out.intunewin");
        IntuneWinBuildResult result;
        await using (var fs = File.Create(target))
            result = await new IntuneWinPackageWriter(Path.Combine(_work, "scratch")).WriteAsync(source, "setup.exe", fs);

        var onDisk = await File.ReadAllBytesAsync(target);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(onDisk)).ToLowerInvariant(), result.ArtifactSha256);
        IndependentDecryptor.Open(onDisk);
    }

    // ---- Compatibility with the existing consumer ------------------------------------------

    [Fact]
    public async Task Existing_reader_parses_writer_output_consistently()
    {
        var (package, result) = await BuildAsync(NewSourceFolder(("setup.exe", Bytes(12_345, 9))));

        using var stream = new MemoryStream(package);
        var info = await new IntuneWinPackageReader().ReadMetadataAsync(stream);

        Assert.Equal(result.SetupFile, info.FileName);
        Assert.Equal(result.UnencryptedContentSize, info.Size);
        Assert.Equal(result.EncryptedPayloadSize, info.SizeEncrypted);
        Assert.Equal(result.EncryptionKey, info.EncryptionKey);
        Assert.Equal(result.MacKey, info.MacKey);
        Assert.Equal(result.InitializationVector, info.InitializationVector);
        Assert.Equal(result.Mac, info.Mac);
        Assert.Equal(result.ProfileIdentifier, info.ProfileIdentifier);
        Assert.Equal(result.FileDigest, info.FileDigest);
        Assert.Equal(result.FileDigestAlgorithm, info.FileDigestAlgorithm);
    }

    // ---- Input validation (spec §7) --------------------------------------------------------

    [Theory]
    [InlineData("../setup.exe")]
    [InlineData("sub/setup.exe")]
    [InlineData("..")]
    [InlineData("sub\\setup.exe")]
    public async Task Setup_file_must_be_a_plain_name_at_the_source_root(string setup)
    {
        var source = NewSourceFolder(("setup.exe", Bytes(10, 10)), ("sub/setup.exe", Bytes(10, 11)));
        await Assert.ThrowsAsync<ArgumentException>(() => BuildAsync(source, setup));
    }

    [Fact]
    public async Task Missing_setup_file_is_rejected()
    {
        var source = NewSourceFolder(("other.exe", Bytes(10, 12)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => BuildAsync(source));
    }

    [UnixFact] // creating a symlink on Windows needs admin or Developer Mode
    public async Task Symbolic_links_in_the_source_are_refused()
    {
        var outside = Path.Combine(_work, "outside-secret.txt");
        await File.WriteAllTextAsync(outside, "not part of the upload");
        var source = NewSourceFolder(("setup.exe", Bytes(10, 13)));
        File.CreateSymbolicLink(Path.Combine(source, "link.txt"), outside);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(source));
        Assert.Contains("symbolic link", ex.Message);
    }

    [Fact]
    public async Task Scratch_files_are_removed_on_success_and_on_failure()
    {
        var scratch = Path.Combine(_work, "scratch-cleanup");
        var writer = new IntuneWinPackageWriter(scratch);

        await writer.WriteAsync(NewSourceFolder(("setup.exe", Bytes(1_000, 14))), "setup.exe", new MemoryStream());
        Assert.Empty(Directory.GetFileSystemEntries(scratch));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteAsync(NewSourceFolder(("setup.exe", Bytes(1_000, 15))), "setup.exe", new MemoryStream(), cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(scratch));
    }

    [UnixTheory] // these names cannot be created on Windows at all
    [InlineData("..\\outside.exe")]   // becomes a traversal entry if separators are rewritten
    [InlineData("a\\b.txt")]
    [InlineData("con.txt")]
    [InlineData("NUL")]
    [InlineData("trailing.")]
    [InlineData("bad:name")]
    public async Task Names_windows_would_misinterpret_are_refused(string hostileName)
    {
        var source = NewSourceFolder(("setup.exe", Bytes(10, 20)));
        await File.WriteAllBytesAsync(Path.Combine(source, hostileName), Bytes(10, 21));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(source));
        Assert.Contains("not a valid Windows name", ex.Message);
    }

    [UnixTheory] // a case-insensitive filesystem cannot hold both names
    [InlineData("Setup.exe", "setup.exe")]      // two files, one Windows name
    [InlineData("Tools", "tools/inner.bin")]  // a file and a directory, one Windows name
    public async Task Names_that_collide_case_insensitively_are_refused(string first, string second)
    {
        var source = NewSourceFolder(("setup.exe", Bytes(10, 22)), (first, Bytes(10, 23)), (second, Bytes(10, 24)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(source));
        Assert.Contains("collide", ex.Message);
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_source_directory_others_can_write_to_is_refused()
    {
        var source = NewSourceFolder(("setup.exe", Bytes(10, 25)), ("sub/file.bin", Bytes(10, 26)));
        File.SetUnixFileMode(Path.Combine(source, "sub"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(source));
        Assert.Contains("writable by group or other", ex.Message);
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Scratch_is_owner_only_while_plaintext_is_on_disk()
    {
        var scratch = Path.Combine(_work, "scratch-private");
        var observed = new List<(string Path, UnixFileMode Mode)>();
        var output = new ProbeStream(onFirstWrite: () =>
        {
            // The artifact is copied out while every scratch file still exists.
            foreach (var dir in Directory.GetDirectories(scratch))
            {
                observed.Add((dir, File.GetUnixFileMode(dir)));
                foreach (var file in Directory.GetFiles(dir))
                    observed.Add((file, File.GetUnixFileMode(file)));
            }
        });

        await new IntuneWinPackageWriter(scratch).WriteAsync(
            NewSourceFolder(("setup.exe", Bytes(4_000, 27))), "setup.exe", output);

        Assert.Equal(4, observed.Count); // one build directory + inner, payload, outer
        const UnixFileMode groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                          UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.All(observed, o => Assert.Equal((UnixFileMode)0, o.Mode & groupOrOther));
        Assert.Empty(Directory.GetFileSystemEntries(scratch));
    }

    [Fact]
    public async Task Scratch_is_removed_when_the_output_write_fails_or_is_cancelled_midway()
    {
        var scratch = Path.Combine(_work, "scratch-faults");
        var writer = new IntuneWinPackageWriter(scratch);

        var failing = new ProbeStream(onFirstWrite: () => throw new IOException("disk full"));
        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(NewSourceFolder(("setup.exe", Bytes(200_000, 28))), "setup.exe", failing));
        Assert.Empty(Directory.GetFileSystemEntries(scratch));

        using var cts = new CancellationTokenSource();
        var cancelling = new ProbeStream(onFirstWrite: cts.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteAsync(NewSourceFolder(("setup.exe", Bytes(200_000, 29))), "setup.exe", cancelling, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(scratch));
    }

    [Fact]
    public async Task Output_is_written_only_asynchronously_and_left_open()
    {
        var output = new ProbeStream(onFirstWrite: null, rejectSyncWrites: true);
        var result = await new IntuneWinPackageWriter(Path.Combine(_work, "scratch")).WriteAsync(
            NewSourceFolder(("setup.exe", Bytes(30_000, 30))), "setup.exe", output);

        Assert.True(output.CanWrite, "writer must not dispose the caller's stream");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant(), result.ArtifactSha256);
        IndependentDecryptor.Open(output.ToArray());
    }

    [Fact(Skip = "Needs a package produced by Microsoft's IntuneWinAppUtil.exe checked in at " +
                 "Fixtures/IntuneWinAppUtil/reference.intunewin (spec §3.0). Un-skip when it lands.")]
    public void Reference_tool_fixture_decrypts_with_the_same_oracle()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "IntuneWinAppUtil", "reference.intunewin");
        var opened = IndependentDecryptor.Open(File.ReadAllBytes(path));
        var (meta, _) = IndependentDecryptor.ReadRaw(File.ReadAllBytes(path));

        Assert.NotEmpty(opened.Files);
        Assert.Contains(meta.SetupFile, opened.Files.Keys);
    }

    /// <summary>
    /// Write-only capture stream with a hook on the first write, optionally refusing synchronous
    /// writes so a test can prove the writer never uses them on the caller's stream. Wraps rather
    /// than derives from MemoryStream: MemoryStream implements WriteAsync by calling the virtual
    /// synchronous Write, which would make the probe accuse the writer of its own behavior.
    /// </summary>
    private sealed class ProbeStream(Action? onFirstWrite, bool rejectSyncWrites = false) : Stream
    {
        private readonly MemoryStream _data = new();
        private bool _fired;
        private bool _disposed;

        public byte[] ToArray() => _data.ToArray();

        private void Fire()
        {
            if (_fired) return;
            _fired = true;
            onFirstWrite?.Invoke();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (rejectSyncWrites) throw new NotSupportedException("synchronous write");
            Fire();
            _data.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Fire();
            ct.ThrowIfCancellationRequested();
            _data.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => _data.Length;
        public override long Position { get => _data.Length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    }

    // ---- The independent oracle ------------------------------------------------------------

    /// <summary>
    /// Opens a .intunewin from the published format alone: exact entry paths (not basenames),
    /// constant-time MAC check over IV||ciphertext, AES-256-CBC/PKCS7 decrypt, SHA-256 digest
    /// check, then the inner zip. Throws <see cref="CryptographicException"/> on any mismatch.
    /// Intentionally shares no code with the writer.
    /// </summary>
    private static class IndependentDecryptor
    {
        public sealed record Metadata(
            string Name, string SetupFile, string FileName, long UnencryptedContentSize,
            string EncryptionKey, string MacKey, string InitializationVector, string Mac,
            string ProfileIdentifier, string FileDigest, string FileDigestAlgorithm);

        public sealed record Opened(byte[] InnerZip, Dictionary<string, byte[]> Files);

        public const string MacFailure = "MAC verification failed.";

        public static (Metadata Meta, byte[] Payload) ReadRaw(byte[] package)
        {
            using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
            var metaEntry = zip.GetEntry("IntuneWinPackage/Metadata/Detection.xml")
                ?? throw new InvalidDataException("Detection.xml missing at its exact path.");
            var payloadEntry = zip.GetEntry("IntuneWinPackage/Contents/IntunePackage.intunewin")
                ?? throw new InvalidDataException("Payload missing at its exact path.");

            XElement root;
            using (var s = metaEntry.Open()) root = XDocument.Load(s).Root!;
            if (root.Name.LocalName != "ApplicationInfo")
                throw new InvalidDataException($"Detection.xml root is '{root.Name.LocalName}', expected ApplicationInfo.");
            var enc = root.Element("EncryptionInfo")!;
            string V(XElement e, string n) => e.Element(n)?.Value ?? throw new InvalidDataException($"{n} missing");

            var meta = new Metadata(
                V(root, "Name"), V(root, "SetupFile"), V(root, "FileName"), long.Parse(V(root, "UnencryptedContentSize")),
                V(enc, "EncryptionKey"), V(enc, "MacKey"), V(enc, "InitializationVector"), V(enc, "Mac"),
                V(enc, "ProfileIdentifier"), V(enc, "FileDigest"), V(enc, "FileDigestAlgorithm"));

            using var ms = new MemoryStream();
            using (var s = payloadEntry.Open()) s.CopyTo(ms);
            return (meta, ms.ToArray());
        }

        public static Opened Open(byte[] package)
        {
            var (meta, payload) = ReadRaw(package);
            if (payload.Length < 48 + 16)
                throw new CryptographicException("Payload shorter than MAC + IV + one block.");

            var macKey = Convert.FromBase64String(meta.MacKey);
            var recordedMac = Convert.FromBase64String(meta.Mac);
            var computed = HMACSHA256.HashData(macKey, payload.AsSpan(32));
            if (!CryptographicOperations.FixedTimeEquals(computed, payload.AsSpan(0, 32)) ||
                !CryptographicOperations.FixedTimeEquals(computed, recordedMac))
                throw new CryptographicException(MacFailure);

            var iv = payload[32..48];
            if (!iv.AsSpan().SequenceEqual(Convert.FromBase64String(meta.InitializationVector)))
                throw new CryptographicException("IV in payload does not match Detection.xml.");

            using var aes = Aes.Create();
            aes.Key = Convert.FromBase64String(meta.EncryptionKey);
            var plaintext = aes.DecryptCbc(payload.AsSpan(48), iv, PaddingMode.PKCS7);

            if (!SHA256.HashData(plaintext).AsSpan().SequenceEqual(Convert.FromBase64String(meta.FileDigest)))
                throw new CryptographicException("FileDigest does not match decrypted content.");

            var files = new Dictionary<string, byte[]>();
            using var inner = new ZipArchive(new MemoryStream(plaintext), ZipArchiveMode.Read);
            foreach (var e in inner.Entries)
            {
                using var s = e.Open();
                using var buf = new MemoryStream();
                s.CopyTo(buf);
                if (!files.TryAdd(e.FullName, buf.ToArray()))
                    throw new InvalidDataException($"Duplicate inner entry '{e.FullName}'.");
            }
            return new Opened(plaintext, files);
        }

        public static byte[] RewritePayload(byte[] package, Func<byte[], byte[]> mutate)
        {
            var (_, payload) = ReadRaw(package);
            var output = new MemoryStream();
            using (var src = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
            using (var dst = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var e in src.Entries)
                {
                    var copy = dst.CreateEntry(e.FullName);
                    using var w = copy.Open();
                    if (e.FullName.EndsWith("IntunePackage.intunewin", StringComparison.Ordinal))
                        w.Write(mutate(payload));
                    else
                        using (var r = e.Open()) r.CopyTo(w);
                }
            }
            return output.ToArray();
        }
    }
}

/// <summary>A fact that only runs on Unix; reported as skipped on Windows rather than failing.</summary>
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix-only: needs Unix file names, symlinks, or permission modes.";
    }
}

/// <summary>A theory that only runs on Unix; reported as skipped on Windows rather than failing.</summary>
internal sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix-only: needs Unix file names, symlinks, or permission modes.";
    }
}
