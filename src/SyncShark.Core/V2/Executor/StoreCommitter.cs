namespace Massdex.SyncShark.V2;

/// <summary>
/// Group commit for one apply (B-124): intents and results that parallel actions write while a store transaction is
/// running wait for it and then share the next one, so a busy apply pays one transaction per batch, not per action.
/// </summary>
/// <remarks>
/// Each caller still waits until its own write is durable: an intent before its effect is attempted, a result before
/// it is reported. A crash therefore never loses an effect's record: until its result commits, its intent is stored
/// and the next run's reconcile resolves it by observation. A failed batch fails every write in it.
/// </remarks>
internal sealed class StoreCommitter
{
    private sealed record Write(SyncAction Action, ActionResult? Result, TaskCompletionSource Durable);

    private readonly IStateStore _store;
    private readonly SemaphoreSlim _storeGate;
    private readonly string _pairId;
    private readonly long _gen;
    private readonly object _lock = new();
    private List<Write> _pending = new();
    private bool _flushing;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SyncAction> _providerActions = new();

    internal StoreCommitter(IStateStore store, SemaphoreSlim storeGate, string pairId, long gen)
    {
        _store = store;
        _storeGate = storeGate;
        _pairId = pairId;
        _gen = gen;
    }

    private bool _pointerPairReported;

    // The entity gate spans native effect AND durable baseline/receipt publication. Refresh uses
    // this same gate, so its pre-effect revision CAS sees the run's committed receipt (and vice versa).
    internal async Task<ActionResult?> RunPointerUpdateAsync(SyncAction action,
        Func<Task<ActionResult?>> run, CancellationToken ct)
    {
        if (_store is not SqliteStateStore store || store.Pair(_pairId) is not { } pair)
            return await run().ConfigureAwait(false);
        ActionResult? result = null;
        await FilesOnDemandSettlement.SerializeAsync(store, pair.A.RootId, action.ExpectedTarget!.ObjectId, async () =>
        {
            if (store.StoragePointerUpdateIsStale(pair, action))
            {
                const string reason = "The pointer revision was already applied or changed. Resync uses the current receipt; no bytes were read.";
                SavePointerRetry(pair, action, reason);
                var skipped = new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, reason);
                result = skipped;
                await CommitAsync(action, skipped).ConfigureAwait(false);
                return;
            }
            result = await run().ConfigureAwait(false);
            if (result?.Outcome == Outcome.Done)
            {
                await RunOperations.WaitAsync(_storeGate, "store commit gate").ConfigureAwait(false);
                try
                {
                    // Positive evidence from this apply clears a prior-run availability cause only when every
                    // open pair cause is connectivity (network/auth/provider), exactly as CompleteSuccess
                    // decides for the storage lanes. A binding, host, volume, backlog or unknown cause survives
                    // an unrelated pointer success. Never hide an unavailable pointer met by this same apply.
                    if (!_pointerPairReported)
                        StorageNotices.CompleteSuccess(store, pair, requested: false, pairCauseReportedThisTurn: false);
                }
                finally { _storeGate.Release(); }
            }
        }, ct).ConfigureAwait(false);
        return result;
    }

    internal async Task DeferPointerUpdateAsync(SyncAction action, StoragePairException error)
    {
        await RunOperations.WaitAsync(_storeGate, "store commit gate").ConfigureAwait(false);
        try
        {
            var pair = _store.Pair(_pairId) ?? throw new InvalidOperationException("The pointer pair was removed.");
            SavePointerRetry(pair, action, error.Message);
            if (!_pointerPairReported)
            {
                if (_store is not SqliteStateStore store)
                    throw new InvalidOperationException("Pointer notices require the durable storage store.");
                StorageNotices.Report(store, pair, string.Empty,
                    new StorageNotice(pair.A.RootId, string.Empty, pair.B.RootId, "pointer"), error);
                _pointerPairReported = true;
            }
        }
        finally { _storeGate.Release(); }
    }

    private void SavePointerRetry(PairDef pair, SyncAction action, string reason)
    {
        var prior = _store.SyncRetryAt(_pairId, action.Path);
        var roots = (prior?.ReadRoots ?? []).Concat(new[] { pair.A.RootId, pair.B.RootId }).Distinct().ToArray();
        _store.SaveSyncRetry(_pairId, (prior ?? new SyncRetry(action.Path, "scan", 0, reason,
            DateTimeOffset.MinValue)) with { ReadRoots = roots, RetryAfter = DateTimeOffset.MinValue });
    }

    internal DefenderGrant? DefenderGrantConflict(SyncAction action) => _store.DefenderGrantConflict(_pairId, action);

    /// <summary>Completes once the action's intent is durable; a cancelled caller records nothing.</summary>
    internal Task RecordIntentAsync(SyncAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Enqueue(action, null);
    }

    /// <summary>Completes once the result is durable; never abandoned by cancellation, since the effect happened.</summary>
    internal async Task CommitAsync(SyncAction action, ActionResult result)
    {
        await Enqueue(LatestAction(action), result).ConfigureAwait(false);
        if (result.Outcome is Outcome.Done or Outcome.Skipped)
            _providerActions.TryRemove(action.ActionId, out _);
    }

    /// <summary>The latest checkpoint-bearing action; uncertain resolution must not discard an in-flight job.</summary>
    internal SyncAction LatestAction(SyncAction action) => _providerActions.GetValueOrDefault(action.ActionId, action);

    /// <summary>
    /// Stores one notice under the same gate the group commits use, so it is durable before the matching result.
    /// Used by an execution-time refusal (insufficient free space) that has no intent to record.
    /// </summary>
    internal async Task AddNoticeAsync(Notice notice)
    {
        await RunOperations.WaitAsync(_storeGate, "store commit gate").ConfigureAwait(false);
        try
        {
            _store.AddNotices([notice]);
        }
        finally
        {
            _storeGate.Release();
        }
    }

    /// <summary>Reuses grouped intent writes for provider evidence, keeping SQL and transaction ownership in Storage.</summary>
    internal ProviderEffectContext CreateProviderContext(SyncAction action) => new(action, this, async checkpoint =>
    {
        var updated = action with { ProviderEffect = checkpoint };
        await Enqueue(updated, null).ConfigureAwait(false);
        _providerActions[action.ActionId] = updated;
    });

    /// <summary>
    /// Records one checkpoint per entry as a single durable write: every intent in <paramref name="checkpoints"/>
    /// shares one store transaction (the same group-commit flusher as ordinary intents), so a 1000-entry upload
    /// batch pays one checkpoint write instead of 1000. The returned task completes only once all are durable, so a
    /// sender awaiting it keeps "checkpoint durable before send"; if the write fails, the whole group fails and
    /// nothing is sent.
    /// </summary>
    internal async Task RecordProviderCheckpointsAsync(
        IReadOnlyList<(SyncAction Action, ProviderEffectCheckpoint Checkpoint)> checkpoints)
    {
        if (checkpoints.Count == 0) return;
        var updated = new SyncAction[checkpoints.Count];
        var writes = new Write[checkpoints.Count];
        for (var i = 0; i < checkpoints.Count; i++)
        {
            updated[i] = checkpoints[i].Action with { ProviderEffect = checkpoints[i].Checkpoint };
            writes[i] = new Write(updated[i], null, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        EnqueueGroup(writes);
        await Task.WhenAll(writes.Select(write => write.Durable.Task)).ConfigureAwait(false);
        // The latest checkpoint-bearing action is published only after durability, matching the single-checkpoint path.
        foreach (var action in updated) _providerActions[action.ActionId] = action;
    }

    private Task Enqueue(SyncAction action, ActionResult? result)
    {
        var write = new Write(action, result, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        EnqueueGroup([write]);
        return write.Durable.Task;
    }

    // Every write joins the queue under one gate section, so a concurrent flusher sees the group whole or not at all:
    // a group is always drained into a single transaction and can never straddle two flushes.
    private void EnqueueGroup(IReadOnlyList<Write> writes)
    {
        bool start;
        lock (_lock)
        {
            _pending.AddRange(writes);
            start = !_flushing;
            _flushing = true;
        }
        if (start) _ = Task.Run(FlushAsync);
    }

    // One flusher at a time drains everything queued meanwhile into the next transaction pair.
    private async Task FlushAsync()
    {
        while (true)
        {
            List<Write> batch;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    _flushing = false;
                    return;
                }
                batch = _pending;
                _pending = new List<Write>();
            }
            await RunOperations.WaitAsync(_storeGate, "store commit gate").ConfigureAwait(false);
            try
            {
                Store(batch);
                foreach (var write in batch) write.Durable.SetResult();
            }
            catch (Exception e)
            {
                foreach (var write in batch) write.Durable.SetException(e);
            }
            finally
            {
                _storeGate.Release();
            }
        }
    }

    private void Store(List<Write> batch)
    {
        var intents = batch.Where(write => write.Result is null).Select(write => write.Action).ToArray();
        var results = batch.Where(write => write.Result is not null)
            .Select(write => (write.Action, write.Result!)).ToArray();
        if (intents.Length > 0) _store.RecordIntents(_pairId, intents);
        if (results.Length > 0) _store.CommitResults(_pairId, _gen, results);
    }
}
