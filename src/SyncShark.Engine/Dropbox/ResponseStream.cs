using System.Buffers;
using System.Runtime.ExceptionServices;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Owns a download response and forwards source failures to its existing staging attempt.</summary>
public sealed class ResponseStream(HttpResponseMessage response, Stream inner, RequestAttempt? attempt,
    CancellationToken callerToken, TimeSpan? providerWait, long? providerWaitStartedTimestamp,
    Action<RequestResult>? bodyEnded, Action<RequestResult?>? requestEnded = null) : Stream
{
    /// <summary>Forwards a response for callers that have no provider observation or body-ended callback.</summary>
    public ResponseStream(HttpResponseMessage response, Stream inner, RequestAttempt? attempt,
        CancellationToken callerToken) : this(response, inner, attempt, callerToken, null, null, null) { }

    private const int SingleByteCount = 0 + 1;
    private const int EndOfStream = 0 - 1;
    private const int Claimed = 0 + 1;

    private readonly HttpResponseMessage _response = response ?? throw new ArgumentNullException(nameof(response));
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly RequestAttempt? _attempt = attempt;
    private readonly CancellationToken _callerToken = callerToken;
    private int _disposed;
    private int _bodyEnded;
    private RequestResult? _bodyResult;

    public override bool CanRead => Volatile.Read(ref _disposed) == 0 && _inner.CanRead;
    public override bool CanSeek => Volatile.Read(ref _disposed) == 0 && _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfDisposed(); return _inner.Length; } }
    public override long Position
    {
        get { ThrowIfDisposed(); return _inner.Position; }
        set { ThrowIfDisposed(); _inner.Position = value; }
    }

    public override void Flush() { ThrowIfDisposed(); _inner.Flush(); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var count = Read(rented, 0, buffer.Length);
            rented.AsSpan(0, count).CopyTo(buffer);
            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[SingleByteCount];
        return Read(buffer) == 0 ? EndOfStream : buffer[0];
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    // One read completes one attempt: the read outcome, cancellation class and body-ended signal share one attempt and one body-ended guard.
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = _callerToken.CanBeCanceled && cancellationToken.CanBeCanceled &&
            _callerToken != cancellationToken
            ? CancellationTokenSource.CreateLinkedTokenSource(_callerToken, cancellationToken) : null;
        var token = linked?.Token ?? (_callerToken.CanBeCanceled ? _callerToken : cancellationToken);
        var count = 0;
        RequestResult? ended = null;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            count = await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (error is not (ArgumentException or ObjectDisposedException or NotSupportedException))
        {
            var stopped = _callerToken.IsCancellationRequested ? _callerToken : cancellationToken;
            if (stopped.IsCancellationRequested)
            {
                var cancellation = error as OperationCanceledException;
                if (cancellation is null || cancellation.CancellationToken != stopped)
                {
                    cancellation = new OperationCanceledException(null, error, stopped);
                }
                _attempt?.TryComplete(RequestResult.CallerStopped, cancellation, providerWait, true, providerWaitStartedTimestamp);
                ended = RequestResult.CallerStopped;
                failure = cancellation;
            }
            else
            {
                var result = (error is IOException && error is not InvalidDataException) || ProviderTransientFailure.IsTransient(error)
                    ? RequestResult.TransientFailure : RequestResult.PermanentFailure;
                _attempt?.TryComplete(result, error, providerWait, true, providerWaitStartedTimestamp);
                ended = result;
                failure = error;
            }
        }
        // The body-ended callback sits outside the source-read classifier so its own throw is not a source failure.
        if (ended is { } bodyResult)
        {
            EndBody(bodyResult);
        }
        else if (count == 0 && buffer.Length > 0)
        {
            // EOF is not validated staging success; length/hash validation belongs to the transfer owner.
            EndBody(RequestResult.Success);
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
        return count;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    // Runs the body-ended callback once; the guard is set before the call so a throw cannot re-run it.
    private void EndBody(RequestResult result)
    {
        if (Interlocked.Exchange(ref _bodyEnded, Claimed) != 0) return;
        _bodyResult = result;
        bodyEnded?.Invoke(result);
    }

    private void ReleaseResponse(bool bodyReleased)
    {
        var released = false;
        try
        {
            _response.Dispose();
            released = bodyReleased;
        }
        finally { requestEnded?.Invoke(released ? _bodyResult : null); }
    }

    /// <summary>Releases both resources even if the inner stream fails to close.</summary>
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && Interlocked.Exchange(ref _disposed, Claimed) == 0)
            {
                var released = false;
                try
                {
                    _inner.Dispose();
                    released = true;
                }
                finally { ReleaseResponse(released); }
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    /// <summary>Releases the body asynchronously and always releases the response.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, Claimed) != 0) return;
        var released = false;
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            released = true;
        }
        finally
        {
            try
            {
                ReleaseResponse(released);
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}
