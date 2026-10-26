using System.Net;

namespace Massdex.SyncShark;

internal static partial class DropboxThrottleBudgetChecks
{
    private static async Task BoundChecksAsync()
    {
        foreach (var hint in new TimeSpan?[] { TimeSpan.FromSeconds(1), null, TimeSpan.FromSeconds(61) })
        {
            var clock = new Clock();
            var limiter = new SyncTransferRateLimiter(new(), clock,
                () => new SyncResourcePressure(.1, .4, 8L * 1024 * 1024 * 1024));
            using var wire = new AlwaysThrottle(clock, hint);
            using var http = new HttpClient(wire);
            var client = new DropboxPilotClient(http, new TokenSource(), "/Pilot", Binding(), "id:pilot-folder", transferLimiter: limiter)
            {
                ReadRetryTime = clock, ReadTransientBudget = TimeSpan.FromSeconds(60),
                ReadRetryDelay = (delay, ct) => { ct.ThrowIfCancellationRequested(); clock.Advance(delay); return Task.CompletedTask; }
            };
            ProviderThrottleExhaustedException? failure = null;
            try
            {
                using var result = await client.CallRouteAsync(new(DropboxRouteTable.Embedded.Find("files/list_folder")!, "{}"),
                    DropboxCliSettings.Default.Retry with { ReadTransientBudget = TimeSpan.FromSeconds(60), MaxBackoff = TimeSpan.FromSeconds(60) }, default);
            }
            catch (ProviderThrottleExhaustedException error) { failure = error; }
            Require(failure is not null, "throttle exits with typed cause");
            Require(wire.Attempts == (hint > TimeSpan.FromSeconds(60) ? 1 : ProviderThrottlePolicy.MaximumRetries + 1),
                "every short/missing Retry-After counts toward the bound");
            var deferred = failure ?? throw new InvalidOperationException("Missing typed throttle cause");
            Require(deferred.Cause == (hint > TimeSpan.FromSeconds(60) ? "backoff-bound" : "retry-bound"), "the bound exit retains its cause");
            var store = new V2.InMemoryStateStore();
            var retry = V2.FailureRetryOwner.SaveFailedRetry(store, "pair", "item", deferred.Message, null,
                clock.GetUtcNow(), V2.FailureRetryOwner.FailureKind.ProviderThrottle, deferred.RetryAt);
            Require(retry.FailureKind == V2.FailureRetryOwner.FailureKind.ProviderThrottle && retry.RetryAfter >= deferred.RetryAt,
                "durable retry retains typed cause and provider deadline");
            var notice = V2.FailureRetryOwner.RetryNotice("pair", "item", retry.LastError, retry.Attempts);
            store.AddNotices([notice, notice]);
            Require(store.OpenNotices("pair").Count() == 1, "one deduplicated shared-owner notice");
        }
    }

    private static async Task ExtendedWaitBoundAsync()
    {
        var clock = new Clock();
        var limiter = new SyncTransferRateLimiter(new(), clock,
            () => new SyncResourcePressure(.1, .4, 8L * 1024 * 1024 * 1024));
        var key = new SyncProviderBudgetKey("dropbox:extended", SyncProviderOperation.Read);
        using var lease = limiter.Resources.ForOperations([key]).TryAcquire(1, true)!;
        var previous = SyncTransferResourceController.Lease.Current.Value;
        SyncTransferResourceController.Lease.Current.Value = lease;
        limiter.Resources.ProviderThrottled(key, TimeSpan.FromSeconds(2));
        ProviderThrottleExhaustedException? failure = null;
        var start = clock.GetUtcNow();
        try
        {
            await limiter.Resources.WaitForCooldownAsync(TimeSpan.FromSeconds(2), "extended", (delay, ct) =>
            {
                clock.Advance(delay);
                limiter.Resources.ProviderThrottled(key, TimeSpan.FromSeconds(2));
                return Task.CompletedTask;
            }, default, key, TimeSpan.FromSeconds(4));
        }
        catch (ProviderThrottleExhaustedException error) { failure = error; }
        finally { SyncTransferResourceController.Lease.Current.Value = previous; }
        Require(failure?.Cause == "wait-bound" && failure.RetryAt == clock.GetUtcNow().AddSeconds(2),
            "repeated cooldown extensions leave typed durable retry work instead of waiting forever");
        Require(clock.GetUtcNow() - start == TimeSpan.FromSeconds(4) && !lease.Holding,
            "the bounded wait remains outside admission and does not send before the extended server deadline");
    }

    private static async Task ParkedExitChecksAsync()
    {
        foreach (var pause in new[] { false, true })
        foreach (var readmit in new[] { false, true })
        {
            var clock = new Clock();
            var limiter = new SyncTransferRateLimiter(new(MaximumConcurrentFiles: 1), clock,
                () => new SyncResourcePressure(.1, .4, 8L * 1024 * 1024 * 1024));
            var scope = limiter.Resources.ForOperations([new("dropbox:test", SyncProviderOperation.Read)]);
            using var lease = scope.TryAcquire(1, true)!;
            using var stop = new CancellationTokenSource();
            var paused = false;
            using var pauseScope = V2.PairPause.Begin(new V2.InMemoryStateStore(), "pair", () => paused);
            var previous = SyncTransferResourceController.Lease.Current.Value;
            SyncTransferResourceController.Lease.Current.Value = lease;
            SyncTransferResourceController.Lease? blocker = null;
            var calls = 0;
            Exception? failure = null;
            try
            {
                await limiter.Resources.WaitForCooldownAsync(TimeSpan.FromSeconds(2), "read", (delay, ct) =>
                {
                    Require(!lease.Holding, "parked before sleeping");
                    clock.Advance(delay);
                    if (readmit && blocker is null) blocker = scope.TryAcquire(1, true);
                    if (++calls == (readmit ? 3 : 1)) { if (pause) paused = true; else stop.Cancel(); }
                    return Task.CompletedTask;
                }, stop.Token);
            }
            catch (Exception error) { failure = error; }
            finally { SyncTransferResourceController.Lease.Current.Value = previous; blocker?.Dispose(); }
            Require(pause ? failure is V2.PairPausedException : failure is OperationCanceledException, "park/readmit honours pause and cancel");
            Require(!lease.Holding && limiter.Resources.Snapshot().ActiveFiles == 0, "parked exit has no capacity leak");
        }
    }

    private static async Task SharedFlightAsync()
    {
        var clock = new Clock();
        var limiter = new SyncTransferRateLimiter(new(MaximumConcurrentFiles: 2, ResourceMode: "Performance"), clock,
            () => new SyncResourcePressure(.1, .4, 8L * 1024 * 1024 * 1024));
        var scope = limiter.Resources.ForOperations([new("dropbox:test", SyncProviderOperation.Read)]);
        using var first = scope.TryAcquire(1, true)!;
        using var second = scope.TryAcquire(1, true)!;
        await using var batch = SyncProviderAccountVerificationScope.Begin(default);
        using var cancel = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new object();
        var flights = 0;
        using var wire = new AlwaysThrottle(clock, TimeSpan.FromSeconds(2), succeedAfterFirst: true);
        using var http = new HttpClient(wire);
        var client = new DropboxPilotClient(http, new TokenSource(), "/Pilot", Binding(), "id:pilot-folder", transferLimiter: limiter)
        {
            ReadRetryTime = clock,
            ReadRetryDelay = async (delay, ct) =>
            {
                Require(SyncTransferResourceController.Lease.Current.Value is null, "shared verification is unleased while throttled");
                entered.TrySetResult(); await release.Task.WaitAsync(ct); clock.Advance(delay);
            }
        };
        async Task Verify(CancellationToken token)
        {
            flights++;
            using var result = await client.CallRouteAsync(new(DropboxRouteTable.Embedded.Find("users/get_current_account")!, "{}"),
                DropboxCliSettings.Default.Retry, token);
        }
        var previous = SyncTransferResourceController.Lease.Current.Value;
        SyncTransferResourceController.Lease.Current.Value = first;
        var initiating = batch.VerifyAsync(adapter, "binding", "synthetic", Verify, cancel.Token);
        await entered.Task;
        SyncTransferResourceController.Lease.Current.Value = second;
        var waiting = batch.VerifyAsync(adapter, "binding", "synthetic", Verify, default);
        SyncTransferResourceController.Lease.Current.Value = previous;
        cancel.Cancel(); first.Complete(false);
        try { await initiating; } catch (OperationCanceledException) { }
        release.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Require(flights == 1 && second.Holding, "initiator cancellation cannot deadlock or park another waiter");
    }

    private sealed class AlwaysThrottle(Clock clock, TimeSpan? hint, bool succeedAfterFirst = false) : HttpMessageHandler
    {
        internal int Attempts;
        private DateTimeOffset _deadline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(clock.GetUtcNow() >= _deadline, "nothing sent before server deadline");
            Attempts++;
            if (succeedAfterFirst && Attempts > 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    """{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""") });
            _deadline = clock.GetUtcNow() + (hint ?? TimeSpan.FromSeconds(1));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            if (hint is { } delay) response.Headers.RetryAfter = new(delay);
            return Task.FromResult(response);
        }
    }
}
