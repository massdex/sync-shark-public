namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>RateLimit operations for the dropbox module.</summary>
public sealed class RateLimit
{
    /// <summary>Wait operation on the supplied facts.</summary>
    public Task WaitAsync(TimeSpan delay, TimeProvider clock, CancellationToken ct) => throw new NotImplementedException();
}
