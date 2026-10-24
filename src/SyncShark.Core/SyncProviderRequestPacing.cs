using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark;

// Request pacing is independent of file concurrency. This seam never retries a
// request: each adapter retains its existing read-only/effect replay boundary.
internal static class SyncProviderRequestPacing
{    // The route path of a request, and nothing else: never the token, the headers or the body.
    internal static string RouteOf(HttpRequestMessage request) => request.RequestUri?.AbsolutePath ?? "request";

    internal static async Task<HttpResponseMessage> SendAsync(SyncTransferRateLimiter limiter,
        SyncProviderBudgetKey key, bool google, HttpClient client, HttpRequestMessage request,
        HttpCompletionOption option, CancellationToken token, Action? onSending = null,
        Func<Task>? beforeSending = null, TimeSpan? errorBodyIdleTimeout = null,
        bool deferProviderCooldown = false)
    {
        var route = RouteOf(request);
        var errorBodyIdle = errorBodyIdleTimeout ?? SyncResponseBody.DefaultIdleTimeout;
        await limiter.Resources.WaitForRequestAsync(key, token, route,
            deferProviderCooldown && !google).ConfigureAwait(false);
        if (beforeSending is not null) await beforeSending().ConfigureAwait(false);
        onSending?.Invoke();
        // The request joins the run's in-flight table, so its own sweeper names the route if it waits too long (RQ-090).
        using var inFlight = V2.RunOperations.Begin(V2.RunOperationKind.Http, route);
        if (V2.RunOperations.Active && request.Content is { } content) request.Content = new ActivityContent(content, inFlight);
        var response = await client.SendAsync(request, option, token).ConfigureAwait(false);
        response.RequestMessage ??= request;
        try
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                google && await IsGoogleQuotaAsync(response, token, errorBodyIdle).ConfigureAwait(false) ||
                (int)response.StatusCode >= 500 && response.Headers.RetryAfter is not null)
            {
                var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(1);
                var writeOnly = !google && response.StatusCode == HttpStatusCode.TooManyRequests &&
                    await IsDropboxWriteThrottleAsync(response, token, errorBodyIdle).ConfigureAwait(false);
                limiter.Resources.ProviderThrottled(writeOnly
                    ? new(key.Provider, SyncProviderOperation.Write) : key, wait, accountWide: !writeOnly);
                await SyncByteProgressStream.ReportThrottleAsync(route, wait);
            }
            else if (response.IsSuccessStatusCode) limiter.Resources.ProviderSucceeded(key);
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private static async Task<bool> IsDropboxWriteThrottleAsync(HttpResponseMessage response, CancellationToken token,
        TimeSpan errorBodyIdleTimeout)
    {
        try
        {
            using var json = JsonDocument.Parse(await SyncResponseBody.ReadAndReplaceAsync(response,
                SyncResponseBody.MaximumErrorBodyBytes, errorBodyIdleTimeout,
                "The provider returned an oversized error response.", token).ConfigureAwait(false));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            string? reason = null;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("reason", out var nested)) reason = Tag(nested);
                reason ??= Tag(error);
            }
            if (reason is null && root.TryGetProperty("error_summary", out var summary) &&
                summary.ValueKind == JsonValueKind.String)
                reason = summary.GetString()?.Split('/', 2)[0];
            return reason == "too_many_write_operations";
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or InvalidOperationException or
                                           IOException or InvalidDataException or TimeoutException)
        { return false; } // Unknown throttle scope is account-wide, never permission to send on the other lane.

        static string? Tag(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(".tag", out var tag) && tag.ValueKind == JsonValueKind.String
                ? tag.GetString() : null;
    }

    private sealed class ActivityContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly V2.RunOperations.Token _operation;
        internal ActivityContent(HttpContent inner, V2.RunOperations.Token operation)
        {
            _inner = inner; _operation = operation;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        { length = _inner.Headers.ContentLength ?? 0; return _inner.Headers.ContentLength.HasValue; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken token)
        {
            using var tracked = V2.RunOperationStream.Touching(stream, _operation.Touch);
            await _inner.CopyToAsync(tracked, context, token).ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var hint = response.Headers.RetryAfter;
        var delay = hint?.Delta ?? (hint?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        return delay is { } value && value < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    internal static async Task<bool> IsGoogleQuotaAsync(HttpResponseMessage response, CancellationToken token,
        TimeSpan? errorBodyIdleTimeout = null)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden) return false;
        try
        {
            // Retain the bounded error response for the adapter's normal error handling.
            // Never log it: provider responses can contain private resource identifiers.
            using var json = JsonDocument.Parse(await SyncResponseBody.ReadAndReplaceAsync(response,
                SyncResponseBody.MaximumErrorBodyBytes,
                errorBodyIdleTimeout ?? SyncResponseBody.DefaultIdleTimeout,
                "The provider returned an oversized error response.", token).ConfigureAwait(false));
            if (!json.RootElement.TryGetProperty("error", out var error) ||
                !error.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return false;
            return errors.EnumerateArray().Any(item => item.TryGetProperty("reason", out var reason) &&
                reason.GetString() is "rateLimitExceeded" or "userRateLimitExceeded" or "sharingRateLimitExceeded");
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or InvalidOperationException or
                                           IOException or InvalidDataException or TimeoutException)
        { return false; } // Unknown/permission failures are not rate-limit retries.
    }
}

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
