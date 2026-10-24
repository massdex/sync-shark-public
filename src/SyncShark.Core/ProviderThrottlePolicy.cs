namespace Massdex.SyncShark;

// One read-local liveness bound, independent of transient exposure. Both a refused HTTP attempt and
// an unsent pacing deferral consume one retry; short/missing Retry-After cannot bypass the bound.
internal sealed class ProviderThrottlePolicy
{
    internal const int MaximumRetries = 32;
    internal static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromMinutes(5);
    private int _retries;

    internal void Retry(TimeSpan delay, TimeSpan maximum, string route, DateTimeOffset now)
    {
        CheckDelay(delay, maximum, route, now);
        if (++_retries > MaximumRetries)
            throw new ProviderThrottleExhaustedException(now + delay, "retry-bound", route);
    }

    internal static void CheckWait(DateTimeOffset started, DateTimeOffset now, TimeSpan remaining,
        TimeSpan maximum, string route)
    {
        CheckDelay(remaining, maximum, route, now);
        if (remaining > TimeSpan.Zero && now - started >= maximum)
            throw new ProviderThrottleExhaustedException(now + remaining, "wait-bound", route);
    }

    internal static void CheckDelay(TimeSpan delay, TimeSpan maximum, string route, DateTimeOffset now)
    {
        if (delay > maximum)
            throw new ProviderThrottleExhaustedException(now + delay, "backoff-bound", route);
    }
}

internal sealed class ProviderThrottleExhaustedException(DateTimeOffset retryAt, string cause, string route)
    : HttpRequestException(cause == "retry-bound"
        ? "The provider kept rate limiting this read; automatic recovery is deferred. Retry after the provider cooldown."
        : "The provider requires more cooldown than can be waited in this run; recovery is deferred. Retry after the provider cooldown.",
        null, System.Net.HttpStatusCode.TooManyRequests)
{
    internal DateTimeOffset RetryAt { get; } = retryAt;
    internal string Cause { get; } = cause;
    internal string Route { get; } = route;
}
