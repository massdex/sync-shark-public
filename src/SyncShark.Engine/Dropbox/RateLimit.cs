namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>RateLimit operations for the dropbox module.</summary>
public sealed class RateLimit
{
    /// <summary>Use the configured starting level and retry schedule for this transport; emit waits to its run log only.</summary>
    public RateLimit(int initialLevel, EngineSettings.BackoffSettings backoff, Action<RunEvent> events) => throw new NotImplementedException();
    /// <summary>Wait operation on the supplied facts.</summary>
    public Task WaitAsync(TimeSpan delay, TimeProvider clock, CancellationToken ct) => throw new NotImplementedException();
}
