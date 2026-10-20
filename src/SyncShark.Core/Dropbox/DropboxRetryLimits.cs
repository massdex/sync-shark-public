namespace Massdex.SyncShark;

/// <summary>Retry counts and the backoff ceiling for one Dropbox route call (design dropbox-cli.md §2.4).</summary>
public sealed record DropboxRetryLimits(
    int RateLimitRetries,
    int ReadTransportRetries,
    int ReadServerRetries,
    TimeSpan MaxBackoff)
{
    /// <summary>Total elapsed recovery window for read-only calls; write retry counts remain unchanged.</summary>
    public TimeSpan ReadTransientBudget { get; init; } = TimeSpan.FromMinutes(5);
}
