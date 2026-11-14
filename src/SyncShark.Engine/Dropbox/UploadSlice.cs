namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>A read-only, seekable window over a seekable stream; no buffering (section 5.5).</summary>
public sealed class UploadSlice : Stream
{
    private readonly Stream _inner;
    private readonly long _offset;
    private readonly long _length;
    private long _position;

    /// <summary>Wraps inner from offset for length bytes and validates that the window fits the source.</summary>
    public UploadSlice(Stream inner, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead || !inner.CanSeek) throw new ArgumentException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > inner.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length > inner.Length - offset) throw new ArgumentOutOfRangeException(nameof(length));
        _inner = inner;
        _offset = offset;
        _length = length;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    /// <summary>Always false: the window is read-only.</summary>
    public override bool CanWrite => false;
    /// <summary>Size of the window, not of the inner stream.</summary>
    public override long Length => _length;
    /// <summary>Position within the window; setting seeks from the window start.</summary>
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    /// <summary>Does nothing: nothing is buffered here.</summary>
    public override void Flush() { }

    private int WindowCount(int requested) => (int)Math.Min(requested, _length - _position);
    private long InnerPosition => _offset + _position;

    /// <summary>Reads up to the window end at the matching inner offset, validating the buffer arguments.</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (_position >= _length || count == 0) return default;
        count = WindowCount(count);
        _inner.Position = InnerPosition;
        int read = _inner.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    /// <summary>Seeks within the window; seeking before its start throws IOException.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long start = origin switch
        {
            SeekOrigin.Begin => default,
            SeekOrigin.Current => _position,
            SeekOrigin.End => _length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (offset < -start) throw new IOException();
        return _position = checked(start + offset);
    }

    /// <summary>Not supported: the window is read-only.</summary>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>Not supported: the window is read-only.</summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Reads asynchronously within the window boundary, without buffering.</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_position >= _length || buffer.IsEmpty) return default;
        int count = WindowCount(buffer.Length);
        _inner.Position = InnerPosition;
        int read = await _inner.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    /// <summary>Validates the buffer and forwards to the memory overload.</summary>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
