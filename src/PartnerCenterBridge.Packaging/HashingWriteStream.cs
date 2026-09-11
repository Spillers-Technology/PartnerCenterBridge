using System.Security.Cryptography;

namespace PartnerCenterBridge.Packaging;

/// <summary>
/// Write-only pass-through that feeds every byte written to an <see cref="IncrementalHash"/> on
/// its way to the inner stream -- used to MAC ciphertext as <see cref="CryptoStream"/> emits it.
/// Reports <see cref="CanSeek"/> = false on purpose: a writer that seeks back to patch earlier
/// bytes would make the running hash wrong. If a write throws, the hash already includes bytes
/// the inner stream may not have persisted; the caller must abandon the build, never finalize it.
/// Never disposes the inner stream; the caller owns it.
/// </summary>
internal sealed class HashingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash;
    private long _written;

    public HashingWriteStream(Stream inner, IncrementalHash hash)
    {
        _inner = inner;
        _hash = hash;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        _inner.Write(buffer);
        _written += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _hash.AppendData(buffer.Span);
        await _inner.WriteAsync(buffer, ct);
        _written += buffer.Length;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
