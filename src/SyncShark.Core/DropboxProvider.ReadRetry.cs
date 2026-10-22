using System.Net;
using System.Net.Sockets;

namespace Massdex.SyncShark;

public sealed partial class DropboxPilotClient
{
    /// <summary>Total elapsed transient recovery window for a read, including attempts and cooldowns.</summary>
    public TimeSpan ReadTransientBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum idle time with no response-body bytes before a Dropbox read is aborted as a transient
    /// interruption. <see cref="System.Net.Http.HttpClient.Timeout"/> does not cover a body read once the
    /// response headers have arrived (<c>HttpCompletionOption.ResponseHeadersRead</c>), so a half-open
    /// connection could otherwise block a read forever. Use <see cref="Timeout.InfiniteTimeSpan"/> to disable.
    /// </summary>
    public TimeSpan ReadStallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum wall-clock time a single attempt of <see cref="VerifyRootIdentityAsync"/> may run before it is
    /// abandoned and retried. A first-sync prepare has no deadline of its own, so a
    /// half-open account or root verification could otherwise hold a listing open forever. The bound is
    /// linked to the caller's token, so the caller's own cancellation still surfaces as cancellation. Use
    /// <see cref="Timeout.InfiniteTimeSpan"/> to disable.
    /// </summary>
    public TimeSpan RootVerifyTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maximum time the "Dropbox account check timed out; retrying" progress notice may take before
    /// recovery continues. The notice is best-effort: a slow or wedged progress sink must not stall the
    /// verification retry loop.
    /// </summary>
    internal TimeSpan RootVerifyNoticeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Optional longer recovery window for listing pages only; item reads retain their own bound.</summary>
    public TimeSpan? ListingTransientBudget { get; set; }

    // Run-scoped, so parallel roots and unrelated clients never steal each other's notice sink.
    internal static readonly AsyncLocal<Action<string>?> ReadWaiting = new();

    internal TimeProvider ReadRetryTime { get; set; } = TimeProvider.System;
    internal Func<TimeSpan, CancellationToken, Task>? ReadRetryDelay { get; set; }

    private static bool IsReadInterruption(Exception error, CancellationToken token) =>
        !token.IsCancellationRequested && error is not (DropboxEffectUncertainException or V2.PairPausedException) &&
        (error is HttpRequestException { StatusCode: null } or SocketException or OperationCanceledException or TimeoutException ||
         error is IOException);

    // Owns every attempt's request and response. Only an explicit response handoff transfers ownership.
    // Failure bodies are deliberately outside the interruption catch: an auth/conflict response is final,
    // even if its error body is interrupted. The total budget includes the initial request.
    //
    // Retry policy: each transient class is budget-bound, not count-bound - a positive retry count keeps
    // retrying that class until the total budget is spent, while an explicit zero count disables retries
    // for that class (a caller asking for no retries gets exactly one attempt). Passing no policy means
    // every transient class is budget-bound.
    private async Task<T> ReadWithRetryAsync<T>(Func<CancellationToken, Task<HttpRequestMessage>> build,
        Func<HttpResponseMessage, CancellationToken, Task<T>> consume, string path, CancellationToken token,
        TimeSpan? budgetOverride = null, TimeSpan? maxBackoff = null, bool handOffResponse = false, string? operation = null,
        DropboxRetryLimits? retryPolicy = null)
    {
        var budget = budgetOverride ?? ReadTransientBudget;
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ReadTransientBudget));
        var started = ReadRetryTime.GetTimestamp();
        var limits = new DropboxRetryLimits(
            RateLimitRetries: BudgetBound(retryPolicy?.RateLimitRetries),
            ReadTransportRetries: BudgetBound(retryPolicy?.ReadTransportRetries),
            ReadServerRetries: BudgetBound(retryPolicy?.ReadServerRetries),
            MaxBackoff: maxBackoff ?? budget);
        // Retains the exact last HTTP refusal seen anywhere in the read so exhaustion can surface its
        // status even when later attempts fail in transport without a response.
        var lastRefusal = (HttpStatusCode?)null;
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            V2.PairPause.Check();
            var remaining = budget - ReadRetryTime.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw Exhausted(null, lastRefusal);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(remaining);
            HttpRequestMessage? request = null;
            HttpResponseMessage? response = null;
            Exception? interruption = null;
            try
            {
                try
                {
                    if (attempt > 0) V2.RunInstrumentation.Current?.RecordRetry();
                    // Preparing the authorized request (token refresh, account verification) runs inside this
                    // recovery boundary: a transient outage while preparing is retryable, while a definite
                    // auth/identity refusal is not an IsReadInterruption and still propagates unchanged.
                    request = await build(deadline.Token);
                    response = await SendOnceAsync(request, deadline.Token, SyncProviderOperation.Read);
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await consume(response, deadline.Token);
                        if (handOffResponse) response = null;
                        return result;
                    }
                    // A non-success response is a refusal: remember its exact status for exhaustion.
                    lastRefusal = response.StatusCode;
                }
                catch (Exception error) when (IsReadInterruption(error, token))
                {
                    interruption = error;
                }
                if (interruption is not null && deadline.IsCancellationRequested && !token.IsCancellationRequested)
                    throw Exhausted(interruption, lastRefusal);
                var verdict = DropboxRetryDecision.Decide(DropboxRoutePolicy.Read,
                    interruption is null ? response?.StatusCode : null, interruption is not null, attempt, limits);
                if (verdict != DropboxRetryDecision.Verdict.Retry)
                {
                    if (interruption is not null) throw interruption;
                    var result = await consume(response!, token);
                    if (handOffResponse) response = null;
                    return result;
                }
                remaining = budget - ReadRetryTime.GetElapsedTime(started);
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))) *
                    (0.8 + Random.Shared.NextDouble() * 0.2));
                // Retry-After is a floor, never shortened or clamped into an earlier request.
                var retryAfter = response?.Headers.RetryAfter;
                var cooldown = retryAfter?.Delta ?? (retryAfter?.Date is { } date
                    ? date - ReadRetryTime.GetUtcNow() : TimeSpan.Zero);
                if (cooldown > delay) delay = cooldown;
                if (cooldown > TimeSpan.Zero && (cooldown >= remaining || cooldown > limits.MaxBackoff))
                {
                    // Retain the exact refusal status; no earlier request is allowed.
                    if (handOffResponse)
                        throw new DropboxApiException(response!.StatusCode,
                            "Dropbox requires a cooldown beyond the read transient budget; retry later.");
                    return await consume(response!, token);
                }
                var reason = response is { } seen ? "HTTP " + (int)seen.StatusCode : "transport";
                response?.Dispose();
                response = null;
                request?.Dispose();
                request = null;
                if (delay >= remaining || delay > limits.MaxBackoff) throw Exhausted(interruption, lastRefusal);
                if (attempt == 0) ReadWaiting.Value?.Invoke(
                    "Waiting for network or Dropbox service (" + reason + "). " +
                    "Sync will retry automatically when connectivity is restored.");
                await SyncByteProgressStream.ReportProcessAsync("Waiting for network; retry Dropbox read", path,
                    $"Retrying {operation ?? path} in {delay.TotalSeconds:N0}s (attempt {attempt + 1}, {reason}) — " +
                    $"{remaining.TotalSeconds:N0}s remain in the transient budget; no write is being replayed.", ProcessMessageKind.Notice);
                // Pause is cooperative rather than token-based. Short slices bound pause latency
                // without shortening the provider's Retry-After or losing cancellation.
                while (delay > TimeSpan.Zero)
                {
                    token.ThrowIfCancellationRequested();
                    V2.PairPause.Check();
                    var slice = delay < TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1);
                    if (ReadRetryDelay is { } delayAsync) await delayAsync(slice, token);
                    else await Task.Delay(slice, ReadRetryTime, token);
                    delay -= slice;
                }
            }
            finally { response?.Dispose(); request?.Dispose(); }
        }

        // Exhaustion keeps the last received HTTP refusal across the whole read: a statusless transport
        // failure is an HttpRequestException, but any 429/5xx seen must surface its status, not lose it.
        Exception Exhausted(Exception? inner = null, HttpStatusCode? refusal = null) => refusal is { } status
            ? new DropboxApiException(status,
                $"Dropbox kept returning HTTP {(int)status}; the read transient budget of {budget.TotalSeconds:N0} seconds is exhausted. Retry when the service is available.")
            : new HttpRequestException(
                $"Dropbox read transient budget exhausted ({budget.TotalSeconds:N0} seconds); the read did not complete. Retry when the network is available.", inner);

        // A positive count means "retry this class until the budget is spent"; an explicit zero disables it.
        static int BudgetBound(int? count) => count is 0 ? 0 : int.MaxValue;
    }

    // One cancellable idle bound shared by every network body read: the bounded JSON reads and both
    // download paths. The bound is armed immediately before each read and disarmed immediately after, so
    // time spent on bandwidth throttling, destination writes or any other work between reads never counts
    // toward the idle window. A stall that is not the caller's own cancellation raises TimeoutException;
    // the caller's cancellation still surfaces as OperationCanceledException, and when both are observed
    // together the caller's cancellation takes precedence.
    private sealed class ReadIdleBound(CancellationToken callerToken, TimeSpan timeout) : IDisposable
    {
        private readonly CancellationTokenSource _stall =
            CancellationTokenSource.CreateLinkedTokenSource(callerToken);

        internal async ValueTask<int> ReadAsync(Stream input, Memory<byte> buffer)
        {
            if (timeout != Timeout.InfiniteTimeSpan) _stall.CancelAfter(timeout);
            try
            {
                return await input.ReadAsync(buffer, _stall.Token);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                callerToken.ThrowIfCancellationRequested();
                throw new ProviderTransportTimeoutException(
                    $"Dropbox stopped sending the response body for {timeout.TotalSeconds:N0} seconds; the read was aborted.");
            }
            finally
            {
                if (timeout != Timeout.InfiniteTimeSpan && !_stall.IsCancellationRequested)
                    _stall.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose() => _stall.Dispose();
    }
}
