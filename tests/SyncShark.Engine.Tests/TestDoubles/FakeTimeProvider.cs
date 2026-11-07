namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>A TimeProvider the limiter checks advance by hand (no sleeps).</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<FakeTimer> timers = new();
    private DateTimeOffset now = DateTimeOffset.UnixEpoch;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (gate) return now.UtcTicks;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return now;
    }

    /// <summary>True while any timer is armed, so a driver advances time only while something is actually waiting.</summary>
    public bool HasArmedTimer
    {
        get { lock (gate) return timers.Any(timer => timer.Armed); }
    }

    public TimeSpan? NextDueIn
    {
        get
        {
            lock (gate) return timers.Where(timer => timer.Armed)
                .Select(timer => (TimeSpan?)(timer.DueAt - now)).Min();
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (gate) now += by;
        while (true)
        {
            FakeTimer? due = null;
            lock (gate)
            {
                foreach (var timer in timers)
                    if (timer.IsDue(now) && (due is null || timer.DueAt < due.DueAt)) due = timer;
                due?.Reschedule();
            }
            if (due is null) return;
            due.Fire();
        }
    }

    /// <summary>Timers fire when Advance passes their due time (Task.Delay with this provider).</summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new FakeTimer(this, callback, state);
        lock (gate)
        {
            timer.Schedule(now, dueTime, period);
            timers.Add(timer);
        }
        return timer;
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider owner;
        private readonly TimerCallback callback;
        private readonly object? state;
        private DateTimeOffset dueAt;
        private TimeSpan period;
        private bool armed;
        private bool disposed;

        internal FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
        }

        internal DateTimeOffset DueAt => dueAt;

        internal bool Armed => armed && !disposed;

        internal bool IsDue(DateTimeOffset at) => Armed && at >= dueAt;

        internal void Schedule(DateTimeOffset from, TimeSpan dueTime, TimeSpan newPeriod)
        {
            period = newPeriod;
            armed = dueTime != Timeout.InfiniteTimeSpan;
            if (armed) dueAt = from + dueTime;
        }

        internal void Reschedule()
        {
            if (!armed) return;
            if (period <= TimeSpan.Zero) armed = false;
            else dueAt += period;
        }

        internal void Fire()
        {
            if (disposed) return;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (disposed) return false;
                Schedule(owner.now, dueTime, period);
                return true;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (disposed) return;
                disposed = true;
                owner.timers.Remove(this);
            }
        }
    }
}
