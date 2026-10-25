namespace Massdex.SyncShark;

// One read-local liveness bound, independent of transient exposure. Both a refused HTTP attempt and
// an unsent pacing deferral consume one retry; short/missing Retry-After cannot bypass the bound.
internal sealed class ProviderThrottlePolicy
{
    internal const int MaximumRetries = V2.V2Settings.ProviderThrottleMaximumRetries;
    internal static readonly TimeSpan DefaultMaxBackoff = V2.V2Settings.ProviderThrottleMaxBackoff;
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
