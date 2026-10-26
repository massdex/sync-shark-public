namespace Massdex.SyncShark;

internal static partial class DropboxThrottleBudgetChecks
{
    private static async Task QueuedCooldownAsync()
    {
        var clock = new ManualClock();
        var limiter = new SyncTransferRateLimiter(new(MaximumConcurrentFiles: 4, ResourceMode: "Performance"), clock,
            () => new SyncResourcePressure(.1, .4, 8L * 1024 * 1024 * 1024));
        var key = new SyncProviderBudgetKey("dropbox:queue", SyncProviderOperation.Read);
        using var lease = limiter.Resources.ForOperations([key]).TryAcquire(1, true)!;
        // Learn spacing, expire the first cooldown, then hold a predecessor on that spacing.
        limiter.Resources.ProviderThrottled(key, TimeSpan.FromMilliseconds(100));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await limiter.Resources.WaitForRequestAsync(key, default);
        var predecessor = limiter.Resources.WaitForRequestAsync(key, default);
        var previous = SyncTransferResourceController.Lease.Current.Value;
        SyncTransferResourceController.Lease.Current.Value = lease;
        var queued = limiter.Resources.WaitForRequestAsync(key, default, "queued", deferProviderCooldown: true);
        SyncTransferResourceController.Lease.Current.Value = previous;
        Require(!queued.IsCompleted, "read queued behind its predecessor before the cooldown");
        limiter.Resources.ProviderThrottled(key, TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(1));
        ProviderCooldownDeferredException? deferred = null;
        try { await queued.WaitAsync(TimeSpan.FromSeconds(5)); } catch (ProviderCooldownDeferredException error) { deferred = error; }
        Require(deferred is not null && !lease.Holding, "queued read defers from the actual wait and parks its lease");
        limiter.Resources.ProviderThrottled(key, TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(1));
        Require(!predecessor.IsCompleted, "cooldown extension blocks predecessor before server deadline");
        clock.Advance(TimeSpan.FromSeconds(10));
        await clock.CompleteAsync(predecessor).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private readonly List<Timer> _timers = new();
        private TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task CompleteAsync(Task operation)
        {
            while (!operation.IsCompleted)
            {
                Task registered;
                long next;
                lock (_timers)
                {
                    next = _timers.Select(timer => timer.Due).DefaultIfEmpty(long.MaxValue).Min();
                    registered = _registered.Task;
                }
                if (next != long.MaxValue) Advance(TimeSpan.FromTicks(Math.Max(0, next - GetTimestamp())));
                else await Task.WhenAny(operation, registered);
            }
            await operation;
        }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        internal void Advance(TimeSpan delta)
        {
            Interlocked.Add(ref _ticks, delta.Ticks);
            Timer[] due;
            lock (_timers) due = _timers.Where(t => t.Due <= GetTimestamp()).ToArray();
            foreach (var timer in due) timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_timers)
            {
                _timers.Add(timer);
                _registered.TrySetResult();
                _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return timer;
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue;
            internal void Fire() { Due = long.MaxValue; callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks; return true; }
            public void Dispose() { lock (clock._timers) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
