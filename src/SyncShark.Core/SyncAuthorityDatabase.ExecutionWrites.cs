using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    // Only an awaited execution scope owns this queue. Grouping amortizes FULL
    // durable commits; it does not acknowledge an intent or receipt before commit.
    // One request's guard failure rolls back its savepoint, not unrelated files.
    private sealed class ExecutionWriteQueue(SyncAuthorityDatabase owner, bool planningReservations)
    {
        private const int MaximumBatch = 16;
        private readonly Channel<ExecutionWriteRequest> _queue = Channel.CreateBounded<ExecutionWriteRequest>(
            new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private Task _pump = Task.CompletedTask;
        private Exception? _failure;
        internal SqliteConnection? Connection { get; set; }

        internal void Start() => _pump = Task.Run(PumpAsync);

        internal bool AppendReadyRequests(List<ExecutionWriteRequest> batch)
        {
            var before = batch.Count;
            while (batch.Count < MaximumBatch && _queue.Reader.TryRead(out var request)) batch.Add(request);
            return batch.Count != before;
        }

        internal async Task EnqueueAsync(Func<SqliteConnection, SqliteTransaction, Task> action,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var request = new ExecutionWriteRequest(action, token);
            try { await _queue.Writer.WriteAsync(request, token).ConfigureAwait(false); }
            catch (ChannelClosedException)
            {
                throw new IOException("The execution database writer is no longer available.", Volatile.Read(ref _failure));
            }
            // Cancellation after admission is reconciled by the writer. Never
            // return early while an admitted transaction can still commit.
            await request.Completion.Task.ConfigureAwait(false);
        }

        internal async Task DrainAsync()
        {
            _queue.Writer.TryComplete();
            await _pump.ConfigureAwait(false);
        }

        private async Task PumpAsync()
        {
            List<ExecutionWriteRequest>? batch = null;
            try
            {
                while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    // Let already-ready workers enqueue, without a timer or an
                    // unbounded delay waiting for a batch to fill.
                    await Task.Yield();
                    batch = new List<ExecutionWriteRequest>(MaximumBatch);
                    AppendReadyRequests(batch);
                    if (batch.Count != 0) await owner.CommitExecutionWritesAsync(batch, planningReservations, this).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                Volatile.Write(ref _failure, error);
                _queue.Writer.TryComplete(error);
                if (batch is not null) foreach (var request in batch) request.Fail(error);
                while (_queue.Reader.TryRead(out var request)) request.Fail(error);
            }
            finally
            {
                if (Connection is not null)
                {
                    await owner._writer.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (ReferenceEquals(owner._executionReservationConnection, Connection))
                            owner.EndExecutionReservationTransaction();
                    }
                    finally { owner._writer.Release(); }
                    await Connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class ExecutionWriteRequest(
        Func<SqliteConnection, SqliteTransaction, Task> action, CancellationToken token)
    {
        internal Func<SqliteConnection, SqliteTransaction, Task> Action { get; } = action;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Error { get; set; }

        internal void Fail(Exception error)
        {
            if (error is OperationCanceledException && Token.IsCancellationRequested)
                Completion.TrySetCanceled(Token);
            else Completion.TrySetException(error);
        }
    }

    private async Task CommitExecutionWritesAsync(List<ExecutionWriteRequest> batch,
        bool planningReservations, ExecutionWriteQueue queue)
    {
        Exception? fatal = null;
        var committed = false;
        var started = TimingStart();
        await _writer.WaitAsync().ConfigureAwait(false);
        ReportTiming("writer-wait", started);
        try
        {
            var connection = queue.Connection;
            if (connection is null)
                queue.Connection = connection = await OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
            else
            {
                // Keep exactly the same schema/receipt and connection-setting
                // checks on each transaction, even though its held connection
                // avoids reconstructing the immutable membership index.
                await RequireExactCurrentSchemaAsync(connection, CancellationToken.None, verifyHealth: false).ConfigureAwait(false);
                await RequireConnectionSettingsAsync(connection, CancellationToken.None).ConfigureAwait(false);
            }
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            if (planningReservations)
                await BeginExecutionReservationTransactionAsync(connection, transaction).ConfigureAwait(false);
            var successful = 0;
            // Include requests already queued while earlier bodies ran. Taking
            // only the pre-transaction snapshot left most planning commits
            // partly empty on slow disks. Never wait here for a producer, and
            // never exceed the original 16-request transaction bound. Callers
            // still receive acknowledgement only after this commit/disposal.
            for (var index = 0; index < batch.Count || queue.AppendReadyRequests(batch); index++)
            {
                var request = batch[index];
                if (request.Token.IsCancellationRequested)
                {
                    request.Error = new OperationCanceledException(request.Token);
                    InvalidateExecutionReservationCandidates();
                    continue;
                }
                await ExecuteAsync(connection, transaction, "SAVEPOINT execution_request;", CancellationToken.None);
                try
                {
                    started = TimingStart();
                    await request.Action(connection, transaction).ConfigureAwait(false);
                    ReportTiming("write-body", started);
                }
                catch (Exception error)
                {
                    request.Error = error;
                    // Failure to restore the savepoint aborts the entire group;
                    // no successful request is acknowledged in that case.
                    await ExecuteAsync(connection, transaction, "ROLLBACK TO execution_request;", CancellationToken.None);
                    // Failed/savepoint-rolled-back requests must not leave candidate
                    // entries for rows that no longer exist in this transaction.
                    InvalidateExecutionReservationCandidates();
                }
                await ExecuteAsync(connection, transaction, "RELEASE execution_request;", CancellationToken.None);
                if (request.Error is null) successful++;
            }
            if (successful != 0)
            {
                ExecutionWriteCommitBoundaryForTest?.Invoke(successful);
                started = TimingStart();
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                ReportTiming("commit", started);
                committed = true;
            }
            // Otherwise disposal rolls back a group containing only refusals.
        }
        catch (Exception error) { fatal = error; }
        finally
        {
            EndExecutionReservationTransaction(retainCandidates: committed && planningReservations);
            _writer.Release();
        }

        // The transaction is disposed and writer lock released before a caller
        // can authorize its next effect. The idle connection remains protected
        // by the existing execution scope and is disposed when that scope drains.
        foreach (var request in batch)
        {
            if (request.Error is { } rejected) request.Fail(rejected);
            else if (fatal is not null) request.Fail(fatal);
            else if (committed) request.Completion.TrySetResult();
            else request.Fail(new IOException("The execution database transaction did not commit."));
        }
        if (fatal is not null) throw new IOException("The execution database transaction failed; no success was acknowledged.", fatal);
    }
}
