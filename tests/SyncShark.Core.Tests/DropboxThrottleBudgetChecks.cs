using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

namespace Massdex.SyncShark;

// Regression for the rehearsal-final2 incident: Dropbox answered reads with repeated HTTP 429 bursts whose
// Retry-After was about the read's whole 300-second transient budget. The client charged that cooldown to the
// read budget, held the 128 admission slots and worker gates during it, and then snapped its provider window
// back to full, so the next burst repeated. This fixture drives a long listing through the real client, the
// real pacing seam, the real admission controller and a fake Dropbox wire.
internal static partial class DropboxThrottleBudgetChecks
{
    private const int Pages = 24;
    private const int BurstsPerPage = 2;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync()
    {
        await RepeatedBurstsAcrossLongListingAsync();
        await BoundChecksAsync();
        await ExtendedWaitBoundAsync();
        await ParkedExitChecksAsync();
        await SharedFlightAsync();
        await QueuedCooldownAsync();
        await V2.ExecutorChecks.ThrottleReleasesWorkerAsync();
        Console.WriteLine("PASS dropbox throttle budget: a long listing survives repeated 429 bursts without transient exhaustion");
    }

    private static async Task RepeatedBurstsAcrossLongListingAsync()
    {
        var clock = new Clock();
        var pressure = new SyncResourcePressure(.1, .4, 8L * 1024 * 1024 * 1024);
        var settings = new SyncTransferSettings(ResourceMode: "Performance", MaximumConcurrentFiles: 128);
        var limiter = new SyncTransferRateLimiter(settings, clock, () => pressure);
        var binding = Binding();
        var readKey = new SyncProviderBudgetKey("dropbox:" + binding.Normalize().ConnectionId,
            SyncProviderOperation.Read);
        var scope = limiter.Resources.ForOperations([readKey]);
        var independent = limiter.Resources.ForOperations(
            [new SyncProviderBudgetKey("drive:independent", SyncProviderOperation.Write)]);

        // The executor holds one admission lease for the action; a backing-off read must give it up.
        var lease = scope.TryAcquire(1, true) ?? throw new InvalidOperationException("initial admission");
        var releasedWhileBackingOff = false;
        var heldDuringBackoff = false;
        using var handler = new BurstListingHandler(Pages, BurstsPerPage, RetryAfter);
        using var http = new HttpClient(handler);
        var client = new DropboxPilotClient(http, new TokenSource(), "/Pilot", binding, "id:pilot-folder",
            transferLimiter: limiter)
        {
            // Far smaller than one Retry-After: if a cooldown were charged, the first burst would exhaust it.
            ReadTransientBudget = TimeSpan.FromSeconds(10),
            ReadRetryTime = clock,
            ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (lease.Holding) heldDuringBackoff = true;
                else
                {
                    releasedWhileBackingOff = true;
                    if (limiter.Resources.Snapshot().ActiveFiles != 0)
                        throw new InvalidOperationException("A backing-off read still held an admission slot.");
                    using var other = independent.TryAcquire(1, true)
                        ?? throw new InvalidOperationException(
                            "Another provider could not proceed while a Dropbox read backed off.");
                }
                clock.Advance(delay);
                return Task.CompletedTask;
            }
        };

        var previous = SyncTransferResourceController.Lease.Current.Value;
        SyncTransferResourceController.Lease.Current.Value = lease;
        Exception? failure = null;
        var entries = 0;
        try { entries = (await client.ListEntriesAsync(default)).Count; }
        catch (Exception error) { failure = error; }
        finally
        {
            SyncTransferResourceController.Lease.Current.Value = previous;
            lease.Complete(failure is null);
        }

        Require(failure is null,
            "repeated 429 bursts across a long listing complete without any read failure (" +
            handler.Throttles + " throttles): " + failure);
        Require(failure is null ||
                !failure.Message.Contains("transient budget exhausted", StringComparison.Ordinal),
            "no 'transient budget exhausted' is raised for a server-requested cooldown");
        Require(handler.Throttles >= Pages * BurstsPerPage,
            $"the fixture delivered repeated 429 bursts ({handler.Throttles})");
        Require(entries >= 1, "the long listing reached its final page entry (" + entries + ")");
        Require(releasedWhileBackingOff, "the backing-off read released its admission slot and re-admitted afterwards");
        Require(!heldDuringBackoff, "no admission slot was held for a server-requested cooldown");
        Require(limiter.Resources.ProviderDelay(readKey) == TimeSpan.Zero,
            "the last cooldown was waited, not shortened into an early send");

        // Defect 3: the window must recover by additive increase after throttle-free intervals, never by a jump.
        var reduced = AdmissionCount(scope);
        Require(reduced is >= 1 and < 128,
            "the repeated bursts reduced the read lane's admission window (" + reduced + ")");
        clock.Advance(TimeSpan.FromSeconds(30));
        var firstStep = AdmissionCount(scope);
        Require(firstStep == Math.Min(128, reduced + 8),
            "one throttle-free interval adds one fixed step, not a jump to the previous window (" + firstStep + ")");
        clock.Advance(TimeSpan.FromSeconds(30));
        var secondStep = AdmissionCount(scope);
        Require(secondStep == Math.Min(128, firstStep + 8),
            "each further quiet interval adds the same fixed step (" + secondStep + ")");
        Require(secondStep < 128, "the window does not regrow to full immediately after a throttle");
        Require(limiter.Resources.Snapshot().ActiveFiles == 0,
            "every admission used to measure recovery was released");
        Console.WriteLine("PASS repeated 429 bursts: long listing completed, slots released while backing off, window regrew additively");
    }

    private static int AdmissionCount(SyncTransferSchedulingScope scope)
    {
        var leases = new List<SyncTransferResourceController.Lease>();
        try
        {
            while (leases.Count < 200 && scope.TryAcquire(1, true) is { } lease) leases.Add(lease);
            return leases.Count;
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    private static DropboxConnectionBinding Binding() => new(DropboxProviderConfiguration.RegisteredClientId,
        "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
        DropboxProviderConfiguration.RequiredScopes);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private const string FixtureValue = "offline-fixture-value";

    private sealed class TokenSource : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken token = default) =>
            Task.FromResult(FixtureValue);
    }

    // A Dropbox listing whose first `bursts` attempts on every page answer 429 with an exact Retry-After,
    // reproducing the incident's repeated bursts across a long paginated read.
    private sealed class BurstListingHandler(int pages, int bursts, TimeSpan retryAfter) : HttpMessageHandler
    {
        private int _page;
        private int _attemptsOnPage;
        internal int Throttles;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/oauth2/token")
                return Task.FromResult(Json(new { access_token = FixtureValue, token_type = "bearer", expires_in = 14400 }));
            if (path == "/2/users/get_current_account")
                return Task.FromResult(Json(new { account_id = "account-id", root_info = new
                    { root_namespace_id = "root-namespace", home_namespace_id = "home-namespace" } }));
            if (path == "/2/files/get_metadata")
                return Task.FromResult(Json(new Dictionary<string, object>
                {
                    [".tag"] = "folder", ["name"] = "Pilot", ["id"] = "id:pilot-folder",
                    ["path_display"] = "/Pilot", ["path_lower"] = "/pilot"
                }));
            if (path == "/2/files/list_folder/get_latest_cursor")
                return Task.FromResult(Json(new { cursor = "cursor-latest" }));
            if (path is not ("/2/files/list_folder" or "/2/files/list_folder/continue"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));
            if (_attemptsOnPage < bursts)
            {
                _attemptsOnPage++;
                Interlocked.Increment(ref Throttles);
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("""{"error_summary":"too_many_requests/..","error":{".tag":"too_many_requests"}}""",
                        Encoding.UTF8, "application/json")
                };
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
                return Task.FromResult(throttled);
            }
            _attemptsOnPage = 0;
            var index = _page++;
            var last = index >= pages - 1;
            var entries = last
                ? new object[] { new Dictionary<string, object>
                    {
                        [".tag"] = "file", ["name"] = "f" + index + ".txt",
                        ["path_display"] = "/Pilot/f" + index + ".txt",
                        ["path_lower"] = "/pilot/f" + index + ".txt",
                        ["id"] = "id:f" + index, ["rev"] = "r" + index, ["size"] = 1L
                    } }
                : Array.Empty<object>();
            return Task.FromResult(Json(new { entries, cursor = "cursor-" + index, has_more = !last }));
        }

        private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
    }

    // Deterministic virtual clock. Read retries advance it through ReadRetryDelay; the pacing seam's own
    // Task.Delay(_, clock) waits advance it through CreateTimer, so a spacing wait elapses instead of
    // spinning on a clock that never moves.
    private sealed class Clock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _now.UtcTicks; }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        internal void Advance(TimeSpan delta) { lock (_gate) _now += delta; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new VirtualTimer(this, callback, state);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Advance(dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
            return timer;
        }

        private sealed class VirtualTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                clock.Advance(dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
                return true;
            }
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
