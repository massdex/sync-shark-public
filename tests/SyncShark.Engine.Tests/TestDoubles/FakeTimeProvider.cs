namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Explicitly advanced clock for retry and recovery cases.</summary>
public sealed class FakeTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset now = initial;
    private readonly List<Once> timers = [];
    private TaskCompletionSource<TimeSpan> registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? fire;
    private readonly object gate = new(); // timers are created and disposed from the code under test's threads
    public override DateTimeOffset GetUtcNow() => now;
    public long Frequency { get; init; } = TimeSpan.TicksPerSecond; // sub-default lets tick rounding show
    public override long TimestampFrequency => Frequency;
    public override long GetTimestamp() => (long)((Int128)now.UtcTicks * Frequency / TimeSpan.TicksPerSecond);
    public void Advance(TimeSpan by) => now += by;
    public TimeSpan? ScheduledDelay { get; private set; }
    public int Created { get; private set; }
    public Task<TimeSpan> Registered => registered.Task; // completes with the due time once the timer's callback is installed
    public int Live { get { lock (gate) return timers.Count(t => !t.Disposed && !t.Fired); } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Unexpected repeating timer");
        ScheduledDelay = dueTime;
        var timer = new Once(dueTime, callback, state, RearmIfLatest);
        lock (gate)
        {
            timers.Add(timer);
            fire = timer.Fire; Created++;
            if (!registered.TrySetResult(dueTime)) { Rearm(); registered.TrySetResult(dueTime); } // a waiter set up before this call sees this timer, not an earlier one
        }
        return timer;
    }
    public void ElapseScheduledDelay()
    {
        Advance(ScheduledDelay ?? throw new InvalidOperationException("No scheduled delay"));
        Action? call;
        lock (gate) { call = fire; fire = null; ScheduledDelay = null; registered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        call!();
    }
    /// <summary>Advances by the timer's due (unless given) and fires the timer created <paramref name="index"/>-th, 0-based.</summary>
    public void Fire(int index, TimeSpan? advance = null)
    {
        Once timer;
        lock (gate) { timer = timers[index]; registered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        Advance(advance ?? timer.Due);
        timer.Fire();
    }
    private void Rearm() { if (registered.Task.IsCompleted) registered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private void RearmIfLatest(Once disposedTimer) { lock (gate) if (ReferenceEquals(timers[^1], disposedTimer)) Rearm(); } // an older timer's disposal must not erase a newer registration
    private sealed class Once(TimeSpan due, TimerCallback callback, object? state, Action<Once> disposed) : ITimer
    {
        public TimeSpan Due { get; } = due;
        public bool Disposed { get; private set; }
        public bool Fired { get; private set; }
        public void Fire() { if (Disposed || Fired) return; Fired = true; callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { if (!Disposed) { Disposed = true; disposed(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
