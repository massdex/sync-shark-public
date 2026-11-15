using System.Buffers;

namespace Massdex.SyncShark.Core;

// Over the 300-line review prompt, not split: every member serves one job, the Stream overrides and
// timers of a single transport lifetime, whose read, write and disposal races share one gate.
/// <summary>Owns a transport stream and bounds pending reads and individual write slices.</summary>
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
                await inner.WriteAsync(buffer[..count], operation.Cancellation.Token).ConfigureAwait(false);
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
        new(Massdex.SyncShark.V2.MessageCatalog.Default.Message("provider-transport-faulted"));

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
        throw TimedOut(operation);
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
        throw TimedOut(operation);
    }

    private static ProviderTransportTimeoutException TimedOut(PendingOperation operation) =>
        new(string.Format(Massdex.SyncShark.V2.MessageCatalog.Default.Message(operation.Reading
            ? "provider-transport-read-timeout" : "provider-transport-write-timeout"),
            operation.Allowance.TotalSeconds));

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
