namespace Massdex.SyncShark.V2;

/// <summary>Hard disk and memory bound for cloud-native document export, including retry seeks.</summary>
internal sealed class ExportBoundedStream(Stream inner, IDisposable? reservation = null) : Stream
{
    internal const long Limit = 256L * 1024 * 1024;
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => inner.CanSeek;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set { Check(value); inner.Position = value; } }
    private static void Check(long value)
    {
        if (value < 0 || value > Limit) throw new IOException("The export exceeds the 256 MiB limit. Export it in Dropbox, or choose another format.");
    }
    public override void SetLength(long value) { Check(value); inner.SetLength(value); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var start = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        Position = checked(start + offset); return Position;
    }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) { Check(checked(Position + count)); inner.Write(buffer, offset, count); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    { Check(checked(Position + buffer.Length)); return inner.WriteAsync(buffer, ct); }
    protected override void Dispose(bool disposing)
    {
        // The spool file handle must be released even when this wrapper was created without a
        // reservation (for example a seekable stream over an in-memory buffer), and the
        // reservation must still be released if disposing the inner stream throws.
        if (disposing) { try { inner.Dispose(); } finally { reservation?.Dispose(); } }
        base.Dispose(disposing);
    }
}
