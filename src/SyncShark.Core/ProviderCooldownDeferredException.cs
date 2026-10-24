namespace Massdex.SyncShark;

/// <summary>
/// A read that would have waited for a server-requested provider cooldown in the pacing seam. It is not a
/// transient failure: the read waits the cooldown outside its transient-failure budget and outside the
/// admission lease, then retries. The provider cooldown itself is never shortened.
/// </summary>
internal sealed class ProviderCooldownDeferredException(TimeSpan delay, string route, DateTimeOffset retryAt = default)
    : HttpRequestException($"Dropbox requires a {delay.TotalSeconds:N0} second cooldown before this read.")
{
    internal TimeSpan Delay { get; } = delay;
    internal string Route { get; } = route;
    internal DateTimeOffset RetryAt { get; } = retryAt;
}
