namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Explicitly advanced clock for retry and recovery cases.</summary>
public sealed class FakeTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset now = initial;
    public override DateTimeOffset GetUtcNow() => now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => now.UtcTicks;
    public void Advance(TimeSpan by) => now += by;
}
