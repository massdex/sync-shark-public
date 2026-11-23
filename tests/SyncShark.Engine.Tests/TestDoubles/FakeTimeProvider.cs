namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Explicitly advanced clock for retry and recovery cases.</summary>
public sealed class FakeTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset now = initial;
    public override DateTimeOffset GetUtcNow() => now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => now.UtcTicks;
    public void Advance(TimeSpan by) => now += by;
    public TimeSpan? ScheduledDelay { get; private set; }
    private Action? fire;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Unexpected repeating timer");
        ScheduledDelay = dueTime;
        var timer = new Once(); fire = () => { if (!timer.Disposed) callback(state); };return timer;
    }
    public void ElapseScheduledDelay()
    {
        Advance(ScheduledDelay ?? throw new InvalidOperationException("No scheduled delay"));
        var call = fire; fire = null; ScheduledDelay = null; call!();
    }
    private sealed class Once : ITimer
    {
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
