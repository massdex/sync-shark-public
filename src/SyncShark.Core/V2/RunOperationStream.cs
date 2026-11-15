namespace Massdex.SyncShark.V2;

/// <summary>
/// Tracks each outstanding response-body read with its route (RQ-090). Completing a read ends its wait, so a moving
/// download never inherits the age of earlier reads. A blocked read remains visible to the sweeper.
/// </summary>
internal sealed class RunOperationStream : Stream
{
    private readonly Stream _inner;
    private readonly string _label;
    private readonly Action? _touch;
    private readonly bool _leaveOpen;

    private RunOperationStream(Stream inner, string label, Action? touch = null, bool leaveOpen = false)
    {
        _inner = inner;
        _label = label;
        _touch = touch;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Wraps the body when a run tracks operations; returns it unchanged when no run is active.</summary>
    internal static Stream Track(Stream inner, HttpResponseMessage response) => RunOperations.Active
        ? new RunOperationStream(inner, (response.RequestMessage?.RequestUri?.AbsolutePath ?? "request") + " response body")
        : inner;

    internal static Stream Touching(Stream inner, Action touch) => new RunOperationStream(inner, "", touch, true);

    private RunOperations.Token BeginRead() => _touch is null ? RunOperations.Begin(RunOperationKind.Http, _label) : default;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken token) => _inner.FlushAsync(token);
    public override int Read(byte[] buffer, int offset, int count)
    { using var wait = BeginRead(); var read = _inner.Read(buffer, offset, count); ProgressGuard.Invoke(_touch); return read; }
    public override int Read(Span<byte> buffer)
    { using var wait = BeginRead(); var read = _inner.Read(buffer); ProgressGuard.Invoke(_touch); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { using var wait = BeginRead(); var read = await _inner.ReadAsync(buffer, token).ConfigureAwait(false); ProgressGuard.Invoke(_touch); return read; }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    { using var wait = BeginRead(); var read = await _inner.ReadAsync(buffer, offset, count, token).ConfigureAwait(false); ProgressGuard.Invoke(_touch); return read; }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); ProgressGuard.Invoke(_touch); }
    public override void Write(ReadOnlySpan<byte> buffer) { _inner.Write(buffer); ProgressGuard.Invoke(_touch); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    { await _inner.WriteAsync(buffer, token).ConfigureAwait(false); ProgressGuard.Invoke(_touch); }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
    { await _inner.WriteAsync(buffer, offset, count, token).ConfigureAwait(false); ProgressGuard.Invoke(_touch); }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen) await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
