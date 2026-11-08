namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>An IAdmission that counts permits, completions and blocked admissions.</summary>
public sealed class FakeAdmission : IAdmission
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource> _waiting = [];
    private readonly List<bool> _singleFileFacts = [];
    private TaskCompletionSource? _waitingChanged;
    private int _admitted;
    private int _completed;
    private int _workAcquired;
    private int _workActive;
    private int _workReleased;
    private int _stageAcquired;
    private int _stageActive;
    private int _stageReleased;
    private int _stageReports;
    private int _directUploadRevocations;
    private bool _hold;

    /// <summary>Holds every new admission until cleared or the caller cancels; clearing admits every waiter. Observation
    /// and capacity tests need a permit that stays blocked so a test can prove observation waits for it.</summary>
    public bool Hold
    {
        get { lock (_gate) return _hold; }
        set
        {
            TaskCompletionSource[] waiting;
            lock (_gate)
            {
                _hold = value;
                if (value) return;
                waiting = [.. _waiting];
                _waiting.Clear();
                SignalWaiting();
            }
            foreach (var waiter in waiting) waiter.TrySetResult();
        }
    }

    public Task<IAsyncDisposable> AdmitAsync(bool priority, CancellationToken ct) =>
        AcquireAsync(() =>
        {
            Interlocked.Increment(ref _admitted);
            return Permit.None;
        }, ct);

    public void ActionCompleted(bool success) => Interlocked.Increment(ref _completed);

    public int Level { get; set; }

    /// <summary>Enters one work scope and records the supplied singleFile fact for outer-handoff checks.</summary>
    public Task<IAsyncDisposable> EnterWorkAsync(bool singleFile, CancellationToken ct)
    {
        lock (_gate) _singleFileFacts.Add(singleFile);
        return AcquireAsync(() =>
        {
            Interlocked.Increment(ref _workAcquired);
            Interlocked.Increment(ref _workActive);
            return new Permit(() =>
            {
                Interlocked.Decrement(ref _workActive);
                Interlocked.Increment(ref _workReleased);
            });
        }, ct);
    }

    /// <summary>Revokes direct-upload eligibility; the call is counted, never granted.</summary>
    public void DisallowDirectUpload() => Interlocked.Increment(ref _directUploadRevocations);

    /// <summary>Admits one pure upload's byte consumption; counted separately from ordinary permits.</summary>
    public async Task<IAsyncDisposable> AdmitUploadAsync(
        (string AccountId, string RouteName, string SourceIdentity) identity, CancellationToken ct)
    {
        if (UploadCapacity is { } capacity)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                TaskCompletionSource waiting;
                lock (_gate)
                {
                    if (_uploadSlots < capacity) { _uploadSlots++; break; }
                    waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _uploadWaiting.Add(waiting);
                    SignalWaiting();
                }
                try { await waiting.Task.WaitAsync(ct); }
                finally
                {
                    lock (_gate) { _uploadWaiting.Remove(waiting); SignalWaiting(); }
                }
            }
        }
        void ReleaseSlot()
        {
            if (UploadCapacity is null) return;
            lock (_gate)
            {
                _uploadSlots--;
                foreach (var waiting in _uploadWaiting) waiting.TrySetResult();
                SignalWaiting();
            }
        }
        try
        {
            return await AcquireAsync(() =>
            {
                Interlocked.Increment(ref _stageAcquired);
                Interlocked.Increment(ref _stageActive);
                return new Permit(() =>
                {
                    Interlocked.Decrement(ref _stageActive);
                    Interlocked.Increment(ref _stageReleased);
                    ReleaseSlot();
                });
            }, ct);
        }
        catch { ReleaseSlot(); throw; }
    }

    /// <summary>Optional fixture capacity; null leaves upload admission open.</summary>
    public int? UploadCapacity { get; init; }
    private readonly List<TaskCompletionSource> _uploadWaiting = [];
    private int _uploadSlots;

    /// <summary>Reports one successful pure-upload byte completion; counted separately from <see cref="ActionCompleted"/>.</summary>
    public void UploadStaged() => Interlocked.Increment(ref _stageReports);

    /// <summary>The account-wide outstanding-work ceiling W = checked(2 * BatchMaxEntries), supplied as a positive
    /// fixture value tests may override for producer bookkeeping.</summary>
    public int WorkCapacity { get; init; } = checked(2 * DropboxApiLimits.BatchMaxEntries);

    /// <summary>Ordinary permits granted; a blocked admission counts only once it is released.</summary>
    public int Admitted => _admitted;
    public int Completed => _completed;
    /// <summary>Admissions of any kind currently blocked by <see cref="Hold"/>.</summary>
    public int Blocked { get { lock (_gate) return _waiting.Count + _uploadWaiting.Count; } }

    /// <summary>Completes once at least <paramref name="count"/> admissions are blocked; re-reads after every
    /// change, so a caller never polls and never misses the count. It never times out by itself: race it against
    /// the work whose progress it observes.</summary>
    public async Task WaitBlockedAsync(int count)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_waiting.Count + _uploadWaiting.Count >= count) return;
                changed = (_waitingChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            await changed.ConfigureAwait(false);
        }
    }

    private void SignalWaiting()
    {
        _waitingChanged?.TrySetResult();
        _waitingChanged = null;
    }

    /// <summary>Work scopes granted and still held or already released.</summary>
    public int WorkAcquired => _workAcquired;
    public int WorkActive => _workActive;
    public int WorkReleased => _workReleased;

    /// <summary>Upload byte-stage permits granted and still held or already released.</summary>
    public int StageAcquired => _stageAcquired;
    public int StageActive => _stageActive;
    public int StageReleased => _stageReleased;

    /// <summary>Successful stage reports, separate from ordinary completions.</summary>
    public int StageReports => _stageReports;

    /// <summary>The singleFile fact supplied by each work registration, in call order.</summary>
    public IReadOnlyList<bool> SingleFileFacts { get { lock (_gate) return [.. _singleFileFacts]; } }

    /// <summary>Direct-upload eligibility revocations requested.</summary>
    public int DirectUploadRevocations => _directUploadRevocations;

    private async Task<IAsyncDisposable> AcquireAsync(Func<IAsyncDisposable> grant, CancellationToken ct)
    {
        TaskCompletionSource? waiting = null;
        lock (_gate)
        {
            if (_hold) _waiting.Add(waiting = new(TaskCreationOptions.RunContinuationsAsynchronously));
            SignalWaiting();
        }
        if (waiting is not null)
        {
            try
            {
                await waiting.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _waiting.Remove(waiting);
                    SignalWaiting();
                }
            }
        }
        return grant();
    }

    /// <summary>One admission slot; the first disposal releases it and later disposals do nothing.</summary>
    private sealed class Permit(Action released) : IAsyncDisposable
    {
        public static readonly Permit None = new(() => { });

        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) released();
            return ValueTask.CompletedTask;
        }
    }
}
