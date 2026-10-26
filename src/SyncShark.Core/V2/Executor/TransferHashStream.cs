namespace Massdex.SyncShark.V2;

/// <summary>Records the bytes actually consumed by a transfer, without another content open.</summary>
internal sealed class TransferHashStream(Stream source) : Stream
{
    private readonly ContentBlockHasher _hash = new();
    internal string? CompletedHash(long expectedBytes) => _hash.TotalBytes == expectedBytes ? _hash.Finish() : null;
    protected override void Dispose(bool disposing)
    {
        if (disposing) source.Dispose();
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() => source.DisposeAsync();
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => source.Position; set => throw new NotSupportedException(); }
    public override void Flush() => source.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = source.Read(buffer);
        _hash.Append(buffer[..count]);
        return count;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var count = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
        _hash.Append(buffer.Span[..count]);
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
}
