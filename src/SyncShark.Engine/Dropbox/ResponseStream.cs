using System.Buffers;
using System.Runtime.ExceptionServices;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Owns a download response and forwards source failures to its existing staging attempt.
/// EOF is not validated staging success; the transfer owner verifies size and hash.</summary>
public sealed class ResponseStream
{
    // Established transport allowance for one pending read, not a limit on the lifetime of a file.
    private const int ReadAllowanceSeconds = 120;
    internal static readonly TimeSpan OrdinaryAllowance = TimeSpan.FromSeconds(ReadAllowanceSeconds);
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

    // Connection-level progress: one pending read, independently bounded write slices and a shared disposal gate.
    internal sealed class ProviderActivityStream : Stream
    {
        private readonly Stream inner;
        private readonly TimeSpan readAllowance;
        private readonly TimeSpan writeAllowance;
        private readonly int maximumWriteSliceBytes;
        private readonly TimeProvider time;
        private readonly object gate = new();
        private PendingOperation? read;
        private PendingOperation? write;
        private bool disposed;
        private bool faulted;

        internal ProviderActivityStream(Stream inner, TimeSpan readAllowance,
            TimeSpan writeAllowance, int maximumWriteSliceBytes, TimeProvider time)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(time);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readAllowance, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(writeAllowance, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWriteSliceBytes);
            this.inner = inner;
            this.readAllowance = readAllowance;
            this.writeAllowance = writeAllowance;
            this.maximumWriteSliceBytes = maximumWriteSliceBytes;
            this.time = time;
        }

        public override bool CanRead => !disposed && inner.CanRead;
        public override bool CanWrite => !disposed && inner.CanWrite;
        public override bool CanSeek => !disposed && inner.CanSeek;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override int Read(Span<byte> buffer)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                int count = Read(rented, 0, buffer.Length);
                rented.AsSpan(0, count).CopyTo(buffer);
                return count;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            CheckAvailable(cancellationToken);
            if (buffer.IsEmpty)
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            PendingOperation operation = BeginOperation(true, cancellationToken);
            try
            {
                int count = await inner.ReadAsync(buffer, operation.Cancellation.Token).ConfigureAwait(false);
                CompleteOperation(operation, cancellationToken, false);
                return count;
            }
            catch
            {
                FailOperation(operation, cancellationToken);
                throw;
            }
            finally
            {
                RetireOperation(operation);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length, maximumWriteSliceBytes));
            try
            {
                CheckAvailable(CancellationToken.None);
                if (buffer.IsEmpty)
                {
                    Write(Array.Empty<byte>(), 0, 0);
                }
                while (!buffer.IsEmpty)
                {
                    int count = Math.Min(buffer.Length, maximumWriteSliceBytes);
                    buffer[..count].CopyTo(rented);
                    Write(rented, 0, count);
                    buffer = buffer[count..];
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            CheckAvailable(cancellationToken);
            if (buffer.IsEmpty)
            {
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                return;
            }
            while (!buffer.IsEmpty)
            {
                int count = Math.Min(buffer.Length, maximumWriteSliceBytes);
                PendingOperation operation = BeginOperation(false, cancellationToken);
                try
                {
                    await inner.WriteAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
                    CompleteOperation(operation, cancellationToken, true);
                }
                catch
                {
                    FailOperation(operation, cancellationToken);
                    throw;
                }
                finally
                {
                    RetireOperation(operation);
                }
                buffer = buffer[count..];
            }
        }

        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            CheckAvailable(cancellationToken);
            PendingOperation operation = BeginOperation(false, cancellationToken);
            try
            {
                await inner.FlushAsync(operation.Cancellation.Token).ConfigureAwait(false);
                CompleteOperation(operation, cancellationToken, false);
            }
            catch
            {
                FailOperation(operation, cancellationToken);
                throw;
            }
            finally
            {
                RetireOperation(operation);
            }
        }

        private bool Refused(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            return faulted;
        }

        private void CheckAvailable(CancellationToken cancellationToken)
        {
            if (Refused(cancellationToken))
            {
                throw Faulted();
            }
        }

        private static IOException Faulted() =>
            new();

        private PendingOperation BeginOperation(bool reading, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (!Refused(cancellationToken))
                {
                    if ((reading ? read : write) is not null)
                    {
                        throw new InvalidOperationException();
                    }
                    var operation = new PendingOperation(reading, reading ? readAllowance : writeAllowance,
                        time.GetTimestamp(), cancellationToken);
                    if (reading)
                    {
                        read = operation;
                    }
                    else
                    {
                        write = operation;
                    }
                    try
                    {
                        operation.Timer = time.CreateTimer(_ => OnTimer(operation), null,
                            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                        operation.Timer.Change(operation.Allowance, Timeout.InfiniteTimeSpan);
                        return operation;
                    }
                    catch
                    {
                        RetireOperation(operation);
                        throw;
                    }
                }
            }
            throw Faulted();
        }

        private void OnTimer(PendingOperation operation)
        {
            lock (gate)
            {
                if (!operation.Active)
                {
                    return;
                }
                TimeSpan remaining = operation.Allowance - time.GetElapsedTime(operation.Started);
                if (remaining > TimeSpan.Zero)
                {
                    operation.Timer!.Change(remaining, Timeout.InfiniteTimeSpan);
                    return;
                }
                Expire(operation);
            }
        }

        private void Expire(PendingOperation operation)
        {
            operation.Expired = true;
            operation.Active = false;
            faulted = true;
            operation.Cancellation.Cancel();
        }

        private void CompleteOperation(PendingOperation operation, CancellationToken caller, bool progress)
        {
            lock (gate)
            {
                caller.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                if (operation.Active && time.GetElapsedTime(operation.Started) >= operation.Allowance)
                {
                    Expire(operation);
                }
                if (!operation.Expired)
                {
                    operation.Active = false;
                    if (progress && read is { Active: true } pendingRead)
                    {
                        // A delayed timer callback must not let a write revive an expired connection.
                        if (time.GetElapsedTime(pendingRead.Started) >= pendingRead.Allowance)
                        {
                            Expire(pendingRead);
                        }
                        else
                        {
                            pendingRead.Started = time.GetTimestamp();
                            pendingRead.Timer!.Change(pendingRead.Allowance, Timeout.InfiniteTimeSpan);
                        }
                    }
                    return;
                }
            }
            throw new TimeoutException();
        }

        private void FailOperation(PendingOperation operation, CancellationToken caller)
        {
            lock (gate)
            {
                faulted = true;
                caller.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!operation.Expired)
                {
                    return;
                }
            }
            throw new TimeoutException();
        }

        private void RetireOperation(PendingOperation operation)
        {
            lock (gate)
            {
                operation.Active = false;
                if (operation.Reading)
                {
                    read = null;
                }
                else
                {
                    write = null;
                }
            }
            operation.Timer?.Dispose();
            operation.Cancellation.Dispose();
        }

        private bool RetireStream()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return false;
                }
                disposed = true;
                foreach (PendingOperation? operation in new[] { read, write })
                {
                    if (operation is not null)
                    {
                        operation.Active = false;
                        operation.Timer?.Dispose();
                        operation.Cancellation.Cancel();
                    }
                }
                return true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && RetireStream())
                {
                    inner.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (RetireStream())
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }

        // Operation identity prevents queued callbacks from acquiring a successor's timer ownership.
        private sealed class PendingOperation(bool reading, TimeSpan allowance, long started,
            CancellationToken caller)
        {
            internal readonly bool Reading = reading;
            internal readonly TimeSpan Allowance = allowance;
            internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
            internal long Started = started;
            internal ITimer? Timer;
            internal bool Active = true;
            internal bool Expired;
        }
    }
}
