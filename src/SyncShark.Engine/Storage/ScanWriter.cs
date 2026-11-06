using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>One side's listing writer (section 3.3): a bounded queue drained by one loop that applies up to one store
/// write batch per transaction under the store's write lock. A producer waits while the queue is full, so a listing
/// streams under back-pressure instead of being retained whole.</summary>
public sealed class ScanWriter : IScanSink
{
    private readonly SqliteConnection _connection;
    private readonly object _writeLock;
    private readonly string _pairId;
    private readonly Side _side;
    private readonly bool _begunEmpty;
    private readonly bool _liveOverrides;
    private readonly long? _deletedNamePairRow;
    private readonly long? _deletedDeltaPairRow;
    private readonly Action? _attach;
    private readonly Action _abortScan;
    private readonly Channel<Listed> _queued;
    private readonly byte[] _scanId;
    private readonly Action? _release;
    private readonly Task _loop;
    private volatile bool _aborted;

    /// <summary>Starts one side's queued listing writer; only a fresh Local listing can skip descendant deletion.
    /// <paramref name="batchRows"/> bounds both the in-flight queue and one save transaction.</summary>
    public ScanWriter(SqliteConnection connection, object writeLock, string pairId, Side side, Action abortScan,
        int batchRows, bool begunEmpty = false, Action? attach = null, long? deletedNamePairRow = null, bool liveOverrides = false, long? deletedDeltaPairRow = null, byte[]? scanId = null, Action? release = null)
    {
        ArgumentNullException.ThrowIfNull(abortScan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchRows);
        _abortScan = abortScan;
        _scanId = scanId ?? Guid.Empty.ToByteArray();
        _release = release;
        _connection = connection;
        _writeLock = writeLock;
        _pairId = pairId;
        _side = side;
        _begunEmpty = begunEmpty;
        _liveOverrides = liveOverrides;
        _attach = attach;
        _deletedNamePairRow = deletedNamePairRow;
        _deletedDeltaPairRow = deletedDeltaPairRow;
        _queued = Channel.CreateBounded<Listed>(new BoundedChannelOptions(batchRows)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _loop = SaveLoopAsync(batchRows);
    }

    /// <summary>Queues one listing step, waiting while the bounded queue is full; refuses once the writer has
    /// finished. A consumer's save failure is surfaced as itself, never as a bare channel closure.</summary>
    public async Task AddAsync(Listed item, CancellationToken ct)
    {
        try
        {
            await _queued.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    /// <summary>Finishes queued writes and reports any save failure.</summary>
    public Task CompleteAsync()
    {
        _queued.Writer.TryComplete();
        return _loop;
    }

    /// <summary>Discards the uncommitted remainder and joins the writer; a batch already committed is kept, and the
    /// aborted operation's partition is discarded by EndScanAsync rather than rolled back here.</summary>
    public Task AbortAsync()
    {
        _aborted = true;
        lock (_writeLock) _abortScan();
        return CompleteAsync();
    }

    private async Task SaveLoopAsync(int batchRows)
    {
        try
        {
            var reader = _queued.Reader;
            // Wait for any item and drain up to one store write batch, so a listing-sized queue is committed in
            // chunks instead of being held whole (bug #80).
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new List<Listed>();
                while (batch.Count < batchRows && reader.TryRead(out var item)) batch.Add(item);
                lock (_writeLock)
                {
                    if (_aborted) return;
                    _attach?.Invoke();
                    using var transaction = _connection.BeginTransaction();
                    using var entries = new ScanEntries(_connection, _pairId, _side, _begunEmpty, _deletedNamePairRow, _liveOverrides, _deletedDeltaPairRow, _scanId);
                    entries.ApplyBatch(transaction, batch);
                    entries.SaveDeletedNames(transaction);
                    transaction.Commit();
                }
            }
        }
        catch (Exception error)
        {
            // Unblock producers waiting on a full queue with the save failure, not a bare closure.
            _queued.Writer.TryComplete(error);
            throw;
        }
        finally
        {
            // Close the producer side so a blocked producer wakes even when a consumer ends abruptly.
            _queued.Writer.TryComplete();
            lock (_writeLock) _release?.Invoke();
        }
    }
}
