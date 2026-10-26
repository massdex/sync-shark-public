namespace Massdex.SyncShark;

/// <summary>A bounded provider cooldown exhausted this run; durable retry must retain its deadline.</summary>
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
