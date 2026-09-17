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
/// The oracle is calibrated against the reference tool by
/// <see cref="Reference_tool_package_opens_with_the_same_oracle"/>, which decrypts a package built by
/// Microsoft's IntuneWinAppUtil.exe 1.8.7. A real Graph commit + Windows endpoint install is still
/// separately required before Stable. Offline tests prove construction, not deployability. Tests that need
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
    [InlineData("COM0.log")]
    [InlineData("COM\u00B9.txt")]      // superscript digits are reserved device names too
    [InlineData("lpt\u00B3")]
    [InlineData(" leading.ini")]       // Windows tooling strips leading spaces
    [InlineData("ctl\u0001.txt")]      // U+0000-U+001F are invalid on Windows
    public async Task Names_windows_would_misinterpret_are_refused(string hostileName)
    {
        var source = NewSourceFolder(("setup.exe", Bytes(10, 20)));
        await File.WriteAllBytesAsync(Path.Combine(source, hostileName), Bytes(10, 21));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(source));
        Assert.Contains("not a valid Windows name", ex.Message);
    }

    [UnixTheory] // Windows' rule is U+0000-U+001F; DEL and C1 controls are valid NTFS names
    [InlineData("del\u007F.txt")]
    [InlineData("c1\u0085.txt")]
    public async Task Control_characters_outside_windows_rule_are_accepted(string name)
    {
        var source = NewSourceFolder(("setup.exe", Bytes(10, 40)));
        await File.WriteAllBytesAsync(Path.Combine(source, name), Bytes(10, 41));

        var (package, _) = await BuildAsync(source);

        Assert.Equal(Bytes(10, 41), IndependentDecryptor.Open(package).Files[name]);
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

    [Fact]
    public async Task Empty_directories_count_toward_the_entry_limit()
    {
        var source = NewSourceFolder(("setup.exe", Bytes(10, 42)));
        for (var i = 0; i < 10; i++)
            Directory.CreateDirectory(Path.Combine(source, $"empty-{i}"));
        var writer = new IntuneWinPackageWriter(Path.Combine(_work, "scratch")) { EntryLimit = 5 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(source, "setup.exe", new MemoryStream()));
        Assert.Contains("more than 5 entries", ex.Message);
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
        var writer = new IntuneWinPackageWriter(scratch)
        {
            // Called before any plaintext reaches the file, so the mode seen here is the mode the
            // plaintext is written under.
            ScratchCreated = path =>
            {
                var dir = Path.GetDirectoryName(path)!;
                observed.Add((dir, File.GetUnixFileMode(dir)));
                observed.Add((path, File.GetUnixFileMode(path)));
            },
        };

        await writer.WriteAsync(NewSourceFolder(("setup.exe", Bytes(4_000, 27))), "setup.exe", new MemoryStream());

        Assert.Equal(6, observed.Count); // payload, inner, outer -- each with its build directory
        const UnixFileMode groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                          UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.All(observed, o => Assert.Equal((UnixFileMode)0, o.Mode & groupOrOther));
        Assert.Empty(Directory.GetFileSystemEntries(scratch));
    }

    [Fact]
    public async Task At_most_two_scratch_files_exist_at_any_point()
    {
        // Spec §5 budgets ~2x the source size in scratch. Each scratch file is as large as the
        // source, so the bound is on how many are live at once: the inner zip must be gone before
        // the outer zip exists, and only the outer zip may remain while the artifact is copied out.
        var scratch = Path.Combine(_work, "scratch-peak");
        var live = new List<string[]>();
        string[] Snapshot() => Directory.GetDirectories(scratch)
            .SelectMany(d => Directory.GetFiles(d)).Select(f => Path.GetFileName(f)).Order().ToArray();
        var writer = new IntuneWinPackageWriter(scratch) { ScratchCreated = _ => live.Add(Snapshot()) };
        string[]? atCopyOut = null;
        var output = new ProbeStream(onWrite: i => { if (i == 0) atCopyOut = Snapshot(); });

        await writer.WriteAsync(NewSourceFolder(("setup.exe", Bytes(50_000, 43))), "setup.exe", output);

        Assert.Equal(
            new[] { new[] { "payload.tmp" }, new[] { "inner.tmp", "payload.tmp" }, new[] { "outer.tmp", "payload.tmp" } },
            live);
        Assert.Equal(new[] { "outer.tmp" }, atCopyOut);
    }

    [Theory]
    [InlineData("payload.tmp")]
    [InlineData("inner.tmp")]
    [InlineData("outer.tmp")]
    public async Task A_throwing_scratch_observer_still_leaves_scratch_empty(string failOn)
    {
        var scratch = Path.Combine(_work, $"scratch-observer-{failOn}");
        var writer = new IntuneWinPackageWriter(scratch)
        {
            ScratchCreated = path =>
            {
                if (Path.GetFileName(path) == failOn) throw new InvalidOperationException("observer failed");
            },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(NewSourceFolder(("setup.exe", Bytes(1_000, 44))), "setup.exe", new MemoryStream()));
        Assert.Empty(Directory.GetFileSystemEntries(scratch));
    }

    public static TheoryData<string, int> OutputFaults => new()
    {
        // Write index 0 fails before any byte reaches the caller; index 2 fails after a prefix has.
        { "io", 0 }, { "io", 2 }, { "cancel", 0 }, { "cancel", 2 }, { "flush", -1 },
    };

    [Theory]
    [MemberData(nameof(OutputFaults))]
    public async Task Output_faults_propagate_and_remove_scratch(string fault, int atWrite)
    {
        var scratch = Path.Combine(_work, $"scratch-fault-{fault}-{atWrite}");
        var writer = new IntuneWinPackageWriter(scratch);
        using var cts = new CancellationTokenSource();
        var output = new ProbeStream(
            onWrite: i =>
            {
                if (i != atWrite) return;
                if (fault == "io") throw new IOException("disk full");
                cts.Cancel();
            },
            failFlush: fault == "flush");
        // Random bytes do not compress, so the ~200 KB artifact takes several 80 KB writes.
        var source = NewSourceFolder(("setup.exe", Bytes(200_000, 28)));

        var ex = await Record.ExceptionAsync(() => writer.WriteAsync(source, "setup.exe", output, cts.Token));

        if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(ex);
        else Assert.IsType<IOException>(ex);
        if (fault == "flush")
        {
            Assert.True(output.Writes >= 3, "flush fails only after every write");
        }
        else
        {
            // Exactly the writes before the fault persisted: index 2 leaves a real prefix behind.
            Assert.Equal(atWrite, output.Writes);
            Assert.Equal(atWrite > 0, output.Length > 0);
        }
        Assert.Empty(Directory.GetFileSystemEntries(scratch));
    }

    [Fact]
    public async Task Output_is_written_only_asynchronously_and_left_open()
    {
        var output = new ProbeStream(rejectSyncWrites: true);
        var result = await new IntuneWinPackageWriter(Path.Combine(_work, "scratch")).WriteAsync(
            NewSourceFolder(("setup.exe", Bytes(30_000, 30))), "setup.exe", output);

        Assert.True(output.CanWrite, "writer must not dispose the caller's stream");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant(), result.ArtifactSha256);
        IndependentDecryptor.Open(output.ToArray());
    }

    /// <summary>
    /// Calibrates the oracle against the reference tool (spec §3.0). The fixture was built on
    /// Windows 11 by IntuneWinAppUtil.exe 1.8.7 (sha256 c1ba45b5...) from a three-file folder:
    /// <c>IntuneWinAppUtil.exe -c src -s setup.cmd -o out -q</c>. It is a test artifact, not an
    /// installer: setup.cmd only writes a marker file.
    /// </summary>
    [Fact]
    public void Reference_tool_package_opens_with_the_same_oracle()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "IntuneWinAppUtil", "reference.intunewin");
        var package = File.ReadAllBytes(path);

        var (meta, _) = IndependentDecryptor.ReadRaw(package);
        var opened = IndependentDecryptor.Open(package);

        Assert.Equal("setup.cmd", meta.SetupFile);
        Assert.Equal("setup.cmd", meta.Name);
        Assert.Equal("IntunePackage.intunewin", meta.FileName);
        Assert.Equal("ProfileVersion1", meta.ProfileIdentifier);
        Assert.Equal("SHA256", meta.FileDigestAlgorithm);
        Assert.Equal(opened.InnerZip.Length, meta.UnencryptedContentSize);

        // The reference tool writes Windows separators into inner entry names; the PCB writer
        // writes '/', as the ZIP specification requires. Recorded here so a change in either is
        // noticed. Whether the Intune Management Extension extracts both is part of the real
        // endpoint check (spec §11, U4).
        Assert.Equal(new[] { "config\\blob.bin", "config\\settings.json", "setup.cmd" }, opened.Files.Keys.Order());
        Assert.Equal("{\"silent\":true}\r\n"u8.ToArray(), opened.Files["config\\settings.json"]);
        Assert.Equal("6e77dbd0825635b08aff8ed6fd93c75fcdb5ecbe7613d05542850bd4d897a9bb",
            Convert.ToHexString(SHA256.HashData(opened.Files["setup.cmd"])).ToLowerInvariant());
        Assert.Equal("ee938db28abdab2b90c7e101c1117cdff8e1073e6d17e0c48057c6c0e717bf2b",
            Convert.ToHexString(SHA256.HashData(opened.Files["config\\blob.bin"])).ToLowerInvariant());
    }

    [Fact]
    public void Reference_tool_package_fails_authentication_when_tampered()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "IntuneWinAppUtil", "reference.intunewin");
        var tampered = IndependentDecryptor.RewritePayload(File.ReadAllBytes(path), p => Flip(p, p.Length - 1));

        var ex = Assert.Throws<CryptographicException>(() => IndependentDecryptor.Open(tampered));
        Assert.Equal(IndependentDecryptor.MacFailure, ex.Message);
    }

    /// <summary>
    /// Write-only capture stream. <c>onWrite</c> runs before each write with its zero-based index,
    /// and may throw or cancel. Optionally refuses synchronous writes, so a test can prove the
    /// writer never uses them on the caller's stream, or fails every flush. Wraps rather than
    /// derives from MemoryStream: MemoryStream implements WriteAsync by calling the virtual
    /// synchronous Write, which would make the probe accuse the writer of its own behavior.
    /// </summary>
    private sealed class ProbeStream(
        Action<int>? onWrite = null, bool rejectSyncWrites = false, bool failFlush = false) : Stream
    {
        private readonly MemoryStream _data = new();
        private bool _disposed;

        /// <summary>Writes that completed and persisted.</summary>
        public int Writes { get; private set; }

        public byte[] ToArray() => _data.ToArray();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (rejectSyncWrites) throw new NotSupportedException("synchronous write");
            onWrite?.Invoke(Writes);
            _data.Write(buffer);
            Writes++;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            onWrite?.Invoke(Writes);
            ct.ThrowIfCancellationRequested();
            _data.Write(buffer.Span);
            Writes++;
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => _data.Length;
        public override long Position { get => _data.Length; set => throw new NotSupportedException(); }
        public override void Flush() { if (failFlush) throw new IOException("flush failed"); }
        public override Task FlushAsync(CancellationToken ct) =>
            failFlush ? Task.FromException(new IOException("flush failed")) : Task.CompletedTask;
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
