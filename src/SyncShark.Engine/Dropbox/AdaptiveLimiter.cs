using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Adaptive admission and throttling for one Dropbox account (section 5.6, D33). All mutable state is read and
/// written only under one lock in this class.</summary>
public sealed class AdaptiveLimiter : IAdmission
{
    /// <summary>The owner-approved doubling and halving rules (D33).</summary>
    public const int GrowthFactor = 2;
    /// <summary>The owner-approved divisor for a new busy period (D33).</summary>
    public const int ShrinkDivisor = 2;
    /// <summary>The accepted work window is two provider batches (BATCHING 3.2), not an adaptive growth step.</summary>
    private const int WorkWindowBatches = 2;
    /// <summary>The positive-admission lower bound: a level of zero would admit nothing.</summary>
    private const int MinimumLevel = 1;

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly INetworkSignal _network;
    private readonly Action<WaitEvent> _waited;
    private readonly LinkedList<TaskCompletionSource?> _priority = new();
    private readonly LinkedList<TaskCompletionSource?> _ordinary = new();
    private readonly LinkedList<TaskCompletionSource?> _work = new();
    private readonly LinkedList<TaskCompletionSource?> _upload = new();
    private int _level;
    private int _active;
    private int _roundSuccesses;
    private bool _slowedThisRun;
    private readonly FailureRetryOwner.BackoffSettings _backoff;
    private long _pauseStarted;
    private TimeSpan _pauseDuration;
    private int _requestFailures;
    private int _commitFailures;
    private int _outstanding;
    private bool _directRevoked;
    private int _uploadLevel;
    private int _uploadActive;
    private int _uploadRoundSuccesses;
    private bool _uploadSlowedThisRun;

    /// <summary>Creates an account limiter whose upload pool starts from its own configured level, distinct from the
    /// saved ordinary start level.</summary>
    public AdaptiveLimiter(int startLevel, int uploadStartLevel, TimeProvider time, INetworkSignal network,
        Action<WaitEvent> waited, FailureRetryOwner.BackoffSettings backoff)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startLevel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(uploadStartLevel);
        _time = time;
        _network = network;
        _waited = waited;
        _backoff = backoff;
        _level = startLevel;
        _uploadLevel = Math.Min(WorkCapacity, uploadStartLevel);
    }

    /// <summary>The current account-wide admission level.</summary>
    public int Level { get { lock (_gate) return _level; } }

    /// <summary>The account-wide outstanding-work ceiling W, derived once from the provider fact and read by producer
    /// bookkeeping.</summary>
    public int WorkCapacity => checked(WorkWindowBatches * DropboxApiLimits.BatchMaxEntries);

    /// <summary>Admits priority before ordinary work, FIFO within each queue, until released or cancelled.</summary>
    public async Task<IAsyncDisposable> AdmitAsync(bool priority, CancellationToken ct)
    {
        var node = new LinkedListNode<TaskCompletionSource?>(null);
        lock (_gate) (priority ? _priority : _ordinary).AddLast(node);
        try
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_active < _level && node == (_priority.First ?? _ordinary.First))
                    {
                        node.List!.Remove(node);
                        _active += 1;
                        Pulse();
                        return new Permit(this);
                    }
                    node.Value = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    changed = node.Value.Task;
                }
                await changed.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate)
            {
                if (node.List is not null)
                {
                    node.List.Remove(node);
                    Pulse();
                }
            }
            throw;
        }
    }

    /// <summary>Updates D33 growth only for successfully completed actions.</summary>
    public void ActionCompleted(bool success)
    {
        if (!success) return;
        lock (_gate)
        {
            Grow(ref _level, ref _roundSuccesses, _slowedThisRun, int.MaxValue);
            Pulse();
        }
    }

    /// <summary>Enters one outer Dropbox-touching operation's place in the account work window; the scope is held
    /// until recording and hold release. <paramref name="singleFile"/> describes the whole operation and is never true
    /// for a composite.</summary>
    public async Task<IAsyncDisposable> EnterWorkAsync(bool singleFile, CancellationToken ct)
    {
        var node = new LinkedListNode<TaskCompletionSource?>(null);
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_outstanding != 0 || _work.Count != 0 || !singleFile) _directRevoked = true;
            _work.AddLast(node);
        }
        try
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_outstanding < WorkCapacity && node == _work.First)
                    {
                        node.List!.Remove(node);
                        _outstanding += 1;
                        PulseWork();
                        return new WorkScope(this);
                    }
                    node.Value = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    changed = node.Value.Task;
                }
                await changed.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate)
            {
                if (node.List is not null)
                {
                    node.List.Remove(node);
                    if (_outstanding == 0 && _work.Count == 0) _directRevoked = false;
                    PulseWork();
                }
            }
            throw;
        }
    }

    /// <summary>Revokes direct-upload eligibility for the caller's current work scope. Idempotent and monotonic; it
    /// never grants eligibility.</summary>
    public void DisallowDirectUpload()
    {
        lock (_gate) _directRevoked = true;
    }

    /// <summary>Admits one pure upload's byte consumption in the account upload pool, released after staging or a
    /// validated direct publication has closed the source.</summary>
    public async Task<IAsyncDisposable> AdmitUploadAsync(
        (string AccountId, string RouteName, string SourceIdentity) identity, CancellationToken ct)
    {
        var node = new LinkedListNode<TaskCompletionSource?>(null);
        lock (_gate) _upload.AddLast(node);
        try
        {
            while (true)
            {
                Task? changed = null;
                TimeSpan pause = default;
                lock (_gate)
                {
                    ct.ThrowIfCancellationRequested();
                    var remaining = RemainingAdmission(identity);
                    if (remaining > TimeSpan.Zero)
                    {
                        pause = remaining;
                    }
                    else if (_uploadActive < _uploadLevel && node == _upload.First)
                    {
                        node.List!.Remove(node);
                        _uploadActive += 1;
                        PulseUpload();
                        return new UploadPermit(this);
                    }
                    else
                    {
                        node.Value = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        changed = node.Value.Task;
                    }
                }
                if (changed is not null) await changed.WaitAsync(ct).ConfigureAwait(false);
                else await Task.Delay(pause, _time, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate)
            {
                if (node.List is not null)
                {
                    node.List.Remove(node);
                    PulseUpload();
                }
            }
            throw;
        }
    }

    /// <summary>Reports one successful pure-upload byte completion; growth is capped at <see cref="WorkCapacity"/>
    /// and never changes the ordinary level.</summary>
    public void UploadStaged()
    {
        lock (_gate)
        {
            Grow(ref _uploadLevel, ref _uploadRoundSuccesses, _uploadSlowedThisRun, WorkCapacity);
            PulseUpload();
        }
    }

    /// <summary>Returns true only when this upload fits the single-request limit and the caller owns the sole
    /// outstanding work scope with no parked work and no earlier revocation; a later request is not predicted.</summary>
    internal bool SelectDirect(long contentLength)
    {
        lock (_gate) return contentLength <= DropboxApiLimits.SingleUploadMaxBytes &&
            _outstanding == 1 && _work.Count == 0 && !_directRevoked;
    }

    /// <summary>Reports an accepted publication or classified semantic refusal, never a staging success. Only this and
    /// unknown-write feedback select an account pause, and only for a new busy period: a live provider floor counts, and
    /// a supplied wait is already that floor, so it suppresses selection and arms nothing here.</summary>
    internal void ReportBatchOutcome((string AccountId, string RouteName, string SourceIdentity) identity,
        bool writeThrottled, TimeSpan? providerWait)
    {
        if (!writeThrottled)
        {
            lock (_gate) _commitFailures = 0;
            return;
        }
        TimeSpan wait;
        lock (_gate) wait = Throttle(true, RemainingAdmission(identity), providerWait, true, true);
        _waited(new WaitEvent(TextKey.WaitTooManyWrites, wait));
    }

    /// <summary>Publishes one classified HTTP outcome once: pre-transition busy state, reduction and the provider floor
    /// change under one lock, the notification outside it. writeThrottled is true for proved write contention, false
    /// for a request throttle and null for neither; validated reads also feed ordinary admission growth.</summary>
    internal void ReportHttpOutcome((string AccountId, string RouteName, string SourceIdentity) identity,
        (RequestResult Result, bool NoEffectProven) outcome, bool? writeThrottled,
        DropboxProtocol.ProviderConstraint? providerConstraint, bool isRead = false)
    {
        var wait = TimeSpan.Zero;
        lock (_gate)
        {
            // Read before installing, or the first reply's own floor would hide its busy-period reduction.
            var remaining = RemainingAdmission(identity);
            if (writeThrottled is { } writes)
                wait = Throttle(writes, remaining, providerConstraint?.Duration,
                    outcome.Result == RequestResult.UnknownWrite, false);
            if (providerConstraint is { } floor)
                FailureRetryOwner.InstallProviderConstraint(floor.Scope, floor.StartedTimestamp, floor.Duration, _time);
            if (outcome.Result == RequestResult.Success)
            {
                _requestFailures = 0;
                if (isRead)
                {
                    Grow(ref _level, ref _roundSuccesses, _slowedThisRun, int.MaxValue);
                    Pulse();
                }
            }
        }
        if (writeThrottled is { } written)
            _waited(new WaitEvent(written ? TextKey.WaitTooManyWrites : TextKey.WaitTooManyRequests, wait));
    }

    // Callers hold _gate. Only unknown-write and semantic feedback select a generic pause; a retry-owned failure
    // leaves generic timing to its episode owner, so no selected deadline reaches account admission.
    private TimeSpan Throttle(bool writes, TimeSpan remaining, TimeSpan? providerWait, bool selects, bool semantic)
    {
        var newBusyPeriod = remaining <= TimeSpan.Zero;
        if (!writes && newBusyPeriod)
        {
            _level = Math.Max(MinimumLevel, _level / ShrinkDivisor);
            _slowedThisRun = true;
            _uploadLevel = Math.Max(MinimumLevel, _uploadLevel / ShrinkDivisor);
            _uploadSlowedThisRun = true;
        }
        ref var failures = ref (semantic ? ref _commitFailures : ref _requestFailures);
        var wait = providerWait ?? (!selects || !newBusyPeriod ? TimeSpan.Zero
            : FailureRetryOwner.AdoptedWait(failures, null, _backoff, Random.Shared.NextDouble));
        if (selects && newBusyPeriod && failures < int.MaxValue) failures += 1;
        if (providerWait is null && wait > remaining)
        {
            _pauseStarted = _time.GetTimestamp();
            _pauseDuration = wait;
        }
        return wait;
    }

    // A wall-clock change must not rebase an already armed account constraint.
    private TimeSpan RemainingPause() => _pauseDuration <= TimeSpan.Zero ? TimeSpan.Zero
        : _pauseDuration - _time.GetElapsedTime(_pauseStarted);

    // Callers hold _gate, which orders limiter state before the floor owner's own lock.
    private TimeSpan RemainingAdmission((string AccountId, string RouteName, string SourceIdentity) identity)
    {
        var pause = RemainingPause();
        return FailureRetryOwner.RemainingProviderConstraint(identity, _time) is { } floor && floor > pause ? floor : pause;
    }

    private async Task WaitForAdmissionAsync((string AccountId, string RouteName, string SourceIdentity) identity,
        CancellationToken ct)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                remaining = RemainingAdmission(identity);
            }
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Admits and sends one request; the caller owns the request and response. One network attempt per call:
    /// floors are rechecked after a slow build, and the attempt owner schedules any retry.</summary>
    public async Task<HttpResponseMessage> SendAsync(bool isRead,
        (string AccountId, string RouteName, string SourceIdentity) identity, Func<Task<HttpRequestMessage>> build,
        Func<HttpRequestMessage, HttpCompletionOption, CancellationToken, Task<HttpResponseMessage>> send,
        HttpCompletionOption completion, CancellationToken ct)
    {
        await WaitForAdmissionAsync(identity, ct).ConfigureAwait(false);
        try
        {
            var request = await build().ConfigureAwait(false);
            await WaitForAdmissionAsync(identity, ct).ConfigureAwait(false);
            return await send(request, completion, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException error) when (error.StatusCode is null && isRead && !_network.Available)
        {
            _waited(new WaitEvent(TextKey.NetworkDown, TimeSpan.Zero));
            throw;
        }
    }

    // The one D33 growth transition for both independent states; the ceiling bounds upload growth at W while the
    // ordinary level keeps its existing unbounded behaviour.
    private static void Grow(ref int level, ref int successes, bool slowed, int ceiling)
    {
        if (slowed)
        {
            if (level < ceiling) level += 1;
            return;
        }
        successes = successes == int.MaxValue ? int.MaxValue : successes + 1;
        if (successes < level) return;
        successes = 0;
        level = level >= ceiling / GrowthFactor ? ceiling : level * GrowthFactor;
    }

    // Callers hold _gate; asynchronous continuations never run inside the state transition.
    private void Pulse()
    {
        if (_active < _level)
            (_priority.First ?? _ordinary.First)?.Value?.TrySetResult();
    }

    // Callers hold _gate; the work window admits only the FIFO head while capacity remains.
    private void PulseWork()
    {
        if (_outstanding < WorkCapacity) _work.First?.Value?.TrySetResult();
    }

    // Callers hold _gate; the upload pool admits only the FIFO head while occupancy fits.
    private void PulseUpload()
    {
        if (_uploadActive < _uploadLevel) _upload.First?.Value?.TrySetResult();
    }

    private sealed class Permit(AdaptiveLimiter owner) : IAsyncDisposable
    {
        private bool _released;

        /// <summary>Returns the permit: drops the active count and wakes the next waiter.</summary>
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                if (_released) return ValueTask.CompletedTask;
                _released = true;
                owner._active -= 1;
                owner.Pulse();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WorkScope(AdaptiveLimiter owner) : IAsyncDisposable
    {
        private bool _released;

        /// <summary>Releases one work-window place and restores direct eligibility only when both populations drain.</summary>
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                if (_released) return ValueTask.CompletedTask;
                _released = true;
                owner._outstanding -= 1;
                if (owner._outstanding == 0 && owner._work.Count == 0) owner._directRevoked = false;
                owner.PulseWork();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UploadPermit(AdaptiveLimiter owner) : IAsyncDisposable
    {
        private bool _released;

        /// <summary>Releases one upload byte-consumption place and wakes the next eligible waiter.</summary>
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                if (_released) return ValueTask.CompletedTask;
                _released = true;
                owner._uploadActive -= 1;
                owner.PulseUpload();
            }
            return ValueTask.CompletedTask;
        }
    }
}
