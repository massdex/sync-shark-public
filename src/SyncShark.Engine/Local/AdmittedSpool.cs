namespace Massdex.SyncShark.Engine.Local;

/// <summary>A page spool whose every write is admitted first against its volume's shared pool and flushed before
/// that grant retires (#36). Synchronous writes are refused, so no byte reaches the file unadmitted.</summary>
internal sealed class AdmittedSpool(FileStream file, string directory, IWriteSpace space) : Stream
{
    public override bool CanRead => file.CanRead;
    public override bool CanSeek => file.CanSeek;
    public override bool CanWrite => file.CanWrite;
    public override long Length => file.Length;
    public override long Position { get => file.Position; set => file.Position = value; }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await using var grant = await space.ReserveAsync(directory, buffer.Length, ct).ConfigureAwait(false);
        await file.WriteAsync(buffer, ct).ConfigureAwait(false);
        // Not durable: a spool lives only for its own attempt.
        await file.FlushAsync(ct).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => file.Flush();
    public override Task FlushAsync(CancellationToken ct) => file.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => file.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => file.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => file.ReadAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => file.Seek(offset, origin);
    public override void SetLength(long value) => file.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) file.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await file.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
