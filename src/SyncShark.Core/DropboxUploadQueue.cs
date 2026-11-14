namespace Massdex.SyncShark;

/// <summary>Bounds closed sessions and overlapping dispatches, coalescing arrivals while all commit lanes are busy.</summary>
internal sealed class DropboxUploadQueue
{
    private readonly object _gate = new();
    private readonly int _batchSize;
    private readonly TimeSpan _maxAge;
    private readonly Func<IReadOnlyList<DropboxUploadCommit>, int, Task> _commit;
    private List<DropboxUploadCommit> _pending = [];
    private readonly int _concurrency;
    private int _committing;
    internal SemaphoreSlim Slots { get; }

    /// <summary>How many age windows the timer has woken through, a probe letting a test prove it re-arms.</summary>
    internal int AgeFlushAttempts;

    internal DropboxUploadQueue(V2.V2Settings settings, Func<IReadOnlyList<DropboxUploadCommit>, int, Task> commit)
    {
        if (settings.PendingUploadCommitLimit < 1 || settings.UploadBatchSize is < 1 or > DropboxApiLimits.BatchMaxEntries ||
            settings.UploadBatchMaxAge <= TimeSpan.Zero || settings.UploadCommitConcurrency is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(settings), "Upload backlog, batch size and maximum age must be positive, the batch within the provider limit, and commit concurrency within 1..4.");
        Slots = new SemaphoreSlim(settings.PendingUploadCommitLimit, settings.PendingUploadCommitLimit);
        // The batch size is the provider-bound finish size, decoupled from the staging buffer (PendingUploadCommitLimit):
        // the buffer is sized to hold this batch plus the ones behind a running commit, not to equal it.
        _batchSize = settings.UploadBatchSize;
        _maxAge = settings.UploadBatchMaxAge;
        _concurrency = settings.UploadCommitConcurrency;
        _commit = commit;
    }

    internal void Enqueue(DropboxUploadCommit item)
    {
        List<DropboxUploadCommit>? ready = null, first = null;
        lock (_gate)
        {
            _pending.Add(item);
            if (_pending.Count == 1) first = _pending;
            if (_committing < _concurrency && _pending.Count >= _batchSize) ready = TakeBatch();
        }
        if (ready is not null) _ = DispatchAsync(ready);
        else if (first is not null) _ = FlushAtAgeAsync(first);
    }

    private List<DropboxUploadCommit> TakeBatch()
    {
        var count = Math.Min(_batchSize, _pending.Count);
        var batch = _pending.GetRange(0, count);
        _pending = _pending.GetRange(count, _pending.Count - count);
        _committing++;
        Slots.Release(count);
        return batch;
    }

    private async Task FlushAtAgeAsync(List<DropboxUploadCommit> watched)
    {
        // Re-arms rather than firing once: while every commit lane is busy (or the tail just grew) the aged partial
        // batch waits another age and tries again, so a short tail still dispatches promptly once a lane frees
        // instead of depending on a later arrival. The watched-list guard retires the timer as soon as a lane has
        // taken (and thereby replaced) this list, since that lane already carries the tail forward.
        while (true)
        {
            await Task.Delay(_maxAge).ConfigureAwait(false);
            AgeFlushAttempts++;
            List<DropboxUploadCommit>? ready = null;
            lock (_gate)
            {
                if (!ReferenceEquals(_pending, watched)) return;
                if (_committing < _concurrency) ready = TakeBatch();
            }
            if (ready is not null)
            {
                await DispatchAsync(ready).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task DispatchAsync(List<DropboxUploadCommit> batch)
    {
        while (true)
        {
            try { await _commit(batch, _batchSize).ConfigureAwait(false); }
            catch (Exception error)
            {
                foreach (var item in batch)
                    item.Result.TrySetException(new DropboxEffectUncertainException("The upload batch outcome is unknown.", error));
            }
            lock (_gate)
            {
                _committing--;
                // Each finishing lane drains its successor (including a short tail); the age timer re-arms as a
                // second chance, so pending entries never depend on another arrival.
                if (_pending.Count == 0) return;
                batch = TakeBatch();
            }
        }
    }
}
