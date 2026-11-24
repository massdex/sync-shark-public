namespace Massdex.SyncShark.Engine.Local;

/// <summary>A checked local read stream that names the failing item when a later read fails
/// (sections 4.2, 4.4).</summary>
public sealed class LocalReadStream(Stream inner, string path) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>True while the inner stream can read.</summary>
    public override bool CanRead => _inner.CanRead;
    /// <summary>Seekable only when the inner stream is.</summary>
    public override bool CanSeek => _inner.CanSeek;
    /// <summary>Always false: local reads are read-only.</summary>
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    /// <summary>Not supported: local reads are read-only.</summary>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>Not supported: local reads are read-only.</summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Reads synchronously, naming the item when the later read fails.</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return _inner.Read(buffer, offset, count); }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Failed(error);
        }
    }

    /// <summary>Reads synchronously, naming the item when the later read fails.</summary>
    public override int Read(Span<byte> buffer)
    {
        try { return _inner.Read(buffer); }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Failed(error);
        }
    }

    /// <summary>Reads asynchronously, naming the item when the later read fails.</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw Failed(error);
        }
    }

    /// <summary>Validates the buffer and forwards to the memory overload.</summary>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>Disposes the inner stream.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Disposes the inner stream.</summary>
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    private ItemFailedException Failed(Exception error) =>
        new(_path, LocalErrors.Cause(error, TextKey.ReadLocalFailed), error);
}
