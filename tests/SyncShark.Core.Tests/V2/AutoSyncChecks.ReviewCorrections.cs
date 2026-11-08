using System.Collections.Concurrent;
using System.Reflection;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Continuous-sync review corrections: a pair-specific notice read that fails at startup stays on that pair's
/// worker (its watch-unavailable notice is scoped to it) while every other loop still starts and runs, and a Start
/// requested while Stop is draining is queued, starting the loops only once the stop has completed.
/// </summary>
/// <remarks>Manual clock, fake watches and the shared <see cref="FakeSyncRun"/> only; no provider or file is touched.</remarks>
internal static partial class AutoSyncChecks
{
    /// <summary>
    /// One pair's startup notice lookup throws (a busy or damaged notice row). The fault must not escape Start or
    /// stop the other pair's loop: both loops start, the faulted pair keeps syncing by polling, and exactly one
    /// watch-unavailable notice exists, scoped to the faulted pair.
    /// </summary>
    public static async Task ReviewStartupNoticeFaultAsync()
    {
        using var inner = new InMemoryStateStore();
        SeedReviewPairs(inner, "p1", "p2");
        var store = NoticeFaultStore.For(inner, faultPairId: "p1");
        var run = new FakeSyncRun();
        var watches = new FakeChangeWatchSource();
        var clock = new ManualClock();
        var auto = new AutoSync(store.Store, run, watches, Settings, _ => false, () => SafetyPoll, clock);
        try
        {
            var escaped = false;
            try { auto.Start(); }
            catch (Exception) { escaped = true; }
            Require(!escaped, "a pair's notice lookup fault never escapes AutoSync.Start");

            await Until(() => Status(auto, "p1") is not null && Status(auto, "p2") is not null,
                "both pair loops start after a pair-specific startup fault");
            await Until(() => run.Runs("p1") == 1,
                "the faulted pair keeps syncing by polling");

            var notice = inner.OpenNotices("p1")
                .FirstOrDefault(row => row.ReasonKey == "autosync-watch-unavailable");
            Require(notice is { PairId: "p1" } &&
                    notice.Detail.Contains("injected notice lookup failure", StringComparison.Ordinal),
                "the failed notice read leaves one watch-unavailable notice scoped to that pair");
            Require(inner.OpenNotices("p2").Count == 0,
                "the other pair carries no notice from its sibling's fault");
            Require(store.Faults >= 1, "the injected notice read really failed");

            // Both loops must already be parked on their own timers: publishing Waiting precedes arming the
            // delay, so advancing on the status alone can miss the quiet timer and never run the other pair.
            await Until(() => Status(auto, "p2") == AutoStatus.Waiting && clock.ActiveTimers == 2,
                "the other pair arms its watches and settles on its timer");
            clock.Advance(Settings.ChangeQuietWindow);
            await Until(() => run.Runs("p2") == 1,
                "the other pair's loop runs once the quiet window passes");
            Pass("ReviewStartupNoticeFault");
        }
        finally
        {
            await auto.StopAsync();
        }
    }

    /// <summary>
    /// Stop is held in its drain by watches whose cancelled waits finish only when the check allows. A Start in that
    /// window must be queued (no loop and no run starts), and the queued start must start the loops as soon as the
    /// stop completes — without another explicit Start.
    /// </summary>
    public static async Task ReviewStartDuringStopDrainAsync()
    {
        using var store = new InMemoryStateStore();
        SeedReviewPairs(store, "p1");
        var run = new FakeSyncRun();
        var watches = new GatedWatchSource();
        var clock = new ManualClock();
        var auto = new AutoSync(store, run, watches, Settings, _ => false, () => SafetyPoll, clock);
        try
        {
            auto.Start();
            // Same ordering rule: wait for the loop's quiet timer, not only the published Waiting state.
            await Until(() => Status(auto, "p1") == AutoStatus.Waiting && clock.ActiveTimers == 1,
                "startup settles on its timer");
            clock.Advance(Settings.ChangeQuietWindow);
            await Until(() => run.Runs("p1") == 1, "startup gives one run");
            await Until(() => Status(auto, "p1") == AutoStatus.Idle && watches.AllParked,
                "the loop parks on its watches");
            Require(watches.Created >= 2, "both roots are watched");

            // Arm the gate only now: the startup debounce cancelled watch waits normally above, and the next
            // cancellation is the stop that must stay draining until this check releases it.
            watches.HoldCancellation = true;
            var stop = auto.StopAsync();
            auto.Start();
            Require(!stop.IsCompleted,
                "a start requested while the stop drains is queued instead of starting loops");
            Require(run.Runs("p1") == 1,
                "the queued start begins no run while the stop drains");

            watches.ReleaseCancellations();
            await stop.WaitAsync(SettleBound);
            await Until(() => watches.Created >= 4,
                "the queued start creates fresh watches after the stop completes");
            await Until(() => Status(auto, "p1") == AutoStatus.Waiting && clock.ActiveTimers == 1,
                "the restarted loop settles on its timer");
            clock.Advance(Settings.ChangeQuietWindow);
            await Until(() => run.Runs("p1") == 2,
                "the queued start runs once the stop has completed");
            Pass("ReviewStartDuringStopDrain");
        }
        finally
        {
            watches.ReleaseCancellations();
            await auto.StopAsync().WaitAsync(SettleBound);
        }
    }

    // ---- helpers ----
    private static void SeedReviewPairs(IStateStore store, params string[] pairIds)
    {
        foreach (var id in pairIds)
        {
            store.SavePair(RigPair(id, enabled: true));
            store.MarkFirstSyncDone(id);
        }
    }

    private static AutoStatus? Status(AutoSync auto, string pairId) =>
        auto.States.FirstOrDefault(state => state.PairId == pairId)?.Status;

    /// <summary>A store that forwards everything except one pair's notice lookup, which fails like a busy row.</summary>
    public class NoticeFaultStore : DispatchProxy
    {
        private IStateStore _inner = null!;
        private string _faultPairId = string.Empty;

        internal int Faults;

        internal IStateStore Store => (IStateStore)(object)this;

        internal static NoticeFaultStore For(IStateStore inner, string faultPairId)
        {
            var proxy = (NoticeFaultStore)(object)DispatchProxy.Create<IStateStore, NoticeFaultStore>();
            proxy._inner = inner;
            proxy._faultPairId = faultPairId;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IStateStore.NoticeById) && args is { Length: > 0 } &&
                args[0] is string pairId && string.Equals(pairId, _faultPairId, StringComparison.Ordinal))
            {
                Faults++;
                throw new IOException("injected notice lookup failure for " + pairId);
            }
            return method!.Invoke(_inner, args);
        }
    }

    /// <summary>
    /// A watch whose cancelled wait finishes immediately until the source's <c>HoldCancellation</c> is armed; once
    /// armed, a cancelled wait finishes only when the check releases it. Normal debounce and poll timeouts therefore
    /// behave as usual, while the stop the fixture holds stays observably in its drain.
    /// </summary>
    private sealed class GatedChangeWatch(GatedWatchSource source) : IChangeWatch
    {
        private readonly object _lock = new();
        private TaskCompletionSource? _waiter;
        private bool _pending = true;

        /// <summary>True while this watch's wait is outstanding: the owner is parked on it.</summary>
        internal bool Parked
        {
            get
            {
                lock (_lock) return _waiter is not null;
            }
        }

        public Task ChangedAsync(CancellationToken ct)
        {
            TaskCompletionSource waiter;
            lock (_lock)
            {
                // A new watch starts with one signal pending (the IChangeWatch contract).
                if (_pending) { _pending = false; return Task.CompletedTask; }
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiter = waiter;
            }
            ct.Register(() => Cancel(waiter, ct));
            return waiter.Task;
        }

        public void Dispose() { }

        private void Cancel(TaskCompletionSource waiter, CancellationToken ct)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_waiter, waiter)) return;
                _waiter = null;
            }
            if (!source.HoldCancellation) { waiter.TrySetCanceled(ct); return; }
            _ = FinishAsync(waiter, ct);
        }

        private async Task FinishAsync(TaskCompletionSource waiter, CancellationToken ct)
        {
            await source.CancellationReleased.ConfigureAwait(false);
            waiter.TrySetCanceled(ct);
        }
    }

    /// <summary>Hands out gated watches and owns the one hold/release decision the fixture flips.</summary>
    private sealed class GatedWatchSource : IChangeWatchSource
    {
        private readonly ConcurrentQueue<GatedChangeWatch> _watches = new();
        private readonly TaskCompletionSource _cancellationReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _holdCancellation;

        /// <summary>When false, a cancelled wait completes at once; when true, it waits for the release.</summary>
        internal bool HoldCancellation
        {
            get => _holdCancellation;
            set => _holdCancellation = value;
        }

        internal Task CancellationReleased => _cancellationReleased.Task;

        internal int Created => _watches.Count;

        /// <summary>True when every watch handed out so far has an outstanding wait.</summary>
        internal bool AllParked => _watches.Count > 0 && _watches.All(watch => watch.Parked);

        internal void ReleaseCancellations()
        {
            // Co-operative again first, so anything cancelled after the release (the restarted loop's debounce)
            // completes normally even if it races the gate.
            _holdCancellation = false;
            _cancellationReleased.TrySetResult();
        }

        public Task<IChangeWatch> WatchAsync(RootDef root, CancellationToken ct)
        {
            var watch = new GatedChangeWatch(this);
            _watches.Enqueue(watch);
            return Task.FromResult<IChangeWatch>(watch);
        }
    }
}
