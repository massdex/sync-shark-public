using System.Buffers;
using System.Runtime.ExceptionServices;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Owns a download response and forwards source failures to its existing staging attempt.
/// EOF is not validated staging success; the transfer owner verifies size and hash.</summary>
public sealed class ResponseStream
{
    /// <summary>How one response body ended: a complete read, the caller's cancellation, a source failure,
    /// or an abandoned body released before EOF (never counted as success).</summary>
    public enum BodyOutcome { Completed, Cancelled, Failed, Abandoned }

    /// <summary>Opens the body over the response; disposing the returned stream releases both.</summary>
    public Stream Open(HttpResponseMessage response, Stream content) =>
        Open(response, content, CancellationToken.None, null);

    /// <summary>Opens the body and reports exactly one terminal outcome (its completion, cancellation,
    /// failure, or abandonment on dispose), invoked outside source-read classification.</summary>
    public Stream Open(HttpResponseMessage response, Stream content, CancellationToken callerToken,
        Action<BodyOutcome>? ended)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(content);
        return new Body(response, content, callerToken, ended);
    }

    private sealed class Body : Stream
    {
        private const int SingleByteCount = 1;

        private readonly HttpResponseMessage _response;
        private readonly Stream _content;
        private readonly CancellationToken _callerToken;
        private readonly Action<BodyOutcome>? _ended;
        private int _disposed;
        private int _endedOnce;

        internal Body(HttpResponseMessage response, Stream content, CancellationToken callerToken,
            Action<BodyOutcome>? ended)
        {
            _response = response;
            _content = content;
            _callerToken = callerToken;
            _ended = ended;
        }

        public override bool CanRead => Volatile.Read(ref _disposed) == 0 && _content.CanRead;
        public override bool CanSeek => Volatile.Read(ref _disposed) == 0 && _content.CanSeek;
        public override bool CanWrite => false;

        public override long Length { get { ThrowIfDisposed(); return _content.Length; } }

        public override long Position
        {
            get { ThrowIfDisposed(); return _content.Position; }
            set { ThrowIfDisposed(); _content.Position = value; }
        }

        public override void Flush() { ThrowIfDisposed(); _content.Flush(); }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            return _content.Seek(offset, origin);
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();
        }

        public override int Read(Span<byte> buffer)
        {
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
            Span<byte> single = stackalloc byte[SingleByteCount];
            return Read(single) == 0 ? -1 : single[0];
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            using var linked = _callerToken.CanBeCanceled && cancellationToken.CanBeCanceled &&
                _callerToken != cancellationToken
                ? CancellationTokenSource.CreateLinkedTokenSource(_callerToken, cancellationToken) : null;
            var token = linked?.Token ?? (_callerToken.CanBeCanceled ? _callerToken : cancellationToken);
            int count;
            try
            {
                token.ThrowIfCancellationRequested();
                count = await _content.ReadAsync(buffer, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            catch (Exception error) when (error is not (ArgumentException or ObjectDisposedException or NotSupportedException))
            {
                var stopped = _callerToken.IsCancellationRequested ? _callerToken : cancellationToken;
                if (stopped.IsCancellationRequested)
                {
                    var cancellation = error as OperationCanceledException;
                    if (cancellation is null || cancellation.CancellationToken != stopped)
                        cancellation = new OperationCanceledException(null, error, stopped);
                    End(BodyOutcome.Cancelled);
                    throw cancellation;
                }
                End(BodyOutcome.Failed);
                throw;
            }
            // The observer runs outside the source-read classifier so its own throw is never misclassified.
            if (count == 0 && buffer.Length > 0) End(BodyOutcome.Completed);
            return count;
        }

        // One terminal notification; set the guard before invoking so a throwing callback cannot re-run it.
        private void End(BodyOutcome outcome)
        {
            if (Interlocked.Exchange(ref _endedOnce, 1) == 0) _ended?.Invoke(outcome);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // One cleanup path: a source-dispose failure never blocks response release and is rethrown after cleanup (not
        // silently lost); an abandoned body (disposed before EOF) ends as Abandoned so it is never credited as success.
        protected override void Dispose(bool disposing)
        {
            Exception? closeError = null;
            try
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    try { _content.Dispose(); }
                    catch (Exception error) { closeError = error; }
                    try { _response.Dispose(); }
                    finally { End(BodyOutcome.Abandoned); }
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
            if (closeError is not null) ExceptionDispatchInfo.Capture(closeError).Throw();
        }

        /// <summary>Releases the body asynchronously and always releases the response.</summary>
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Exception? closeError = null;
            try
            {
                try { await _content.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { closeError = error; }
                try { _response.Dispose(); }
                finally { End(BodyOutcome.Abandoned); }
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
            if (closeError is not null) ExceptionDispatchInfo.Capture(closeError).Throw();
        }
    }
}
