using System.Net;
using System.Net.Sockets;
using Massdex.SyncShark;

internal static class ResilienceChecks
{
    internal sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        internal void Advance(TimeSpan delay) => Interlocked.Add(ref _ticks, delay.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new ClockTimer(this, callback, state, dueTime, period);

        // Timer creation (and cancellation before firing) never spends virtual elapsed time.
        // Real short pacing timers advance to their deadline only when they actually fire;
        // overlapping timers advance to an absolute instant rather than adding their durations twice.
        private sealed class ClockTimer : ITimer
        {
            private readonly object _gate = new();
            private readonly Clock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private readonly Timer _timer;
            private long _at, _period;
            private bool _disposed;
            internal ClockTimer(Clock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            {
                _clock = clock; _callback = callback; _state = state;
                _timer = new Timer(Fire, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                Change(due, period);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    if (_disposed) return false;
                    _at = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : _clock.GetTimestamp() + dueTime.Ticks;
                    _period = period.Ticks;
                    return _timer.Change(dueTime, period);
                }
            }
            private void Fire(object? unused)
            {
                lock (_gate)
                {
                    if (_disposed || _at == long.MaxValue) return;
                    long before;
                    do { before = _clock.GetTimestamp(); }
                    while (before < _at && Interlocked.CompareExchange(ref _clock._ticks, _at, before) != before);
                    _at = _period > 0 ? _at + _period : long.MaxValue;
                }
                _callback(_state);
            }
            public void Dispose() { lock (_gate) { _disposed = true; _timer.Dispose(); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    // The read budget and the provider cooldown it waits on must share one clock, exactly as the real
    // System clock does. A separate real-clock controller would never elapse while the fake clock advances.
    internal static SyncTransferRateLimiter Limiter(Clock clock) => new(new SyncTransferSettings(), clock,
        () => new SyncResourcePressure(.1, .4, 8L * 1024 * 1024 * 1024));

    internal static async Task RunAsync()
    {
        await RunTokenRefreshAsync();
        foreach (var generic in new[] { false, true })
        foreach (var mode in new[] { "dns", "socket", "timeout", "body", "server", "cooldown", "exhausted", "auth", "permission", "conflict", "cancel" })
        {
            var clock = new Clock();
            using var wire = new Wire(clock, mode);
            using var http = new HttpClient(wire);
            using var cancel = new CancellationTokenSource();
            var client = new DropboxPilotClient(http, new Tokens(), "/Pilot", Binding(), "id:pilot-folder", transferLimiter: Limiter(clock))
            {
                ReadRetryTime = clock,
                ReadTransientBudget = TimeSpan.FromSeconds(mode == "exhausted" ? 10 : 60),
                ReadRetryDelay = (delay, ct) =>
                {
                    if (mode == "cancel") cancel.Cancel();
                    ct.ThrowIfCancellationRequested();
                    clock.Advance(delay);
                    return Task.CompletedTask;
                }
            };
            Exception? failure = null;
            var notices = new List<SyncByteProgress>();
            using var progress = SyncByteProgressStream.Begin("listing", item => { notices.Add(item); return Task.CompletedTask; });
            try
            {
                if (generic)
                {
                    var route = DropboxRouteTable.Embedded.Find("files/list_folder")!;
                    var result = await client.CallRouteAsync(new DropboxRouteCall(route, "{}"),
                        DropboxCliSettings.Default.Retry with { ReadTransientBudget = client.ReadTransientBudget }, cancel.Token);
                    result.Dispose();
                }
                else
                {
                    var entries = await client.ListEntriesAsync(cancel.Token);
                    Check(entries.Count == 0, "the full listing completed");
                }
            }
            catch (Exception error) { failure = error; }
            var success = mode is "dns" or "socket" or "timeout" or "body" or "server" or "cooldown";
            Check(success ? failure is null : failure is not null, $"{generic}/{mode}: expected outcome ({failure?.Message})");
            if (success)
            {
                Check(clock.GetUtcNow() >= DateTimeOffset.UnixEpoch.AddSeconds(20), "outage lasted at least 20 virtual seconds");
                Check(wire.Attempts > 3, "read survived more than two retries");
                Check(mode == "cooldown" ? wire.CooldownWaits >= 4 : notices.Any(n => n.Phase.Contains("Waiting for network")), "cooldown elapsed or waiting-for-network progress");
            }
            if (mode is "auth" or "permission" or "conflict") Check(wire.Attempts == 1, "definite refusal never retried");
            if (mode == "exhausted") Check(failure!.Message.Contains("budget exhausted"), "clear budget exhaustion");
            if (mode == "cancel") Check(failure is OperationCanceledException && wire.Attempts == 1, "prompt user cancellation");
        }
        Console.WriteLine("PASS shared read retry: listing/route, 20s DNS/reset/socket/timeout/body outage, budget, auth/permission/conflict and cancellation");
        await RunRefusalExhaustionAsync();
        await RunRefusalThenTransportExhaustionAsync();
    }

    // When a read exhausts its budget on repeated 429/5xx, the thrown error must keep the last
    // received HTTP refusal status instead of a statusless transport exception.
    internal static async Task RunRefusalExhaustionAsync()
    {
        var clock = new Clock();
        using var wire = new Wire(clock, "always-server");
        using var http = new HttpClient(wire);
        var client = new DropboxPilotClient(http, new Tokens(), "/Pilot", Binding(), "id:pilot-folder", transferLimiter: Limiter(clock))
        {
            ReadRetryTime = clock,
            ReadTransientBudget = TimeSpan.FromSeconds(3),
            ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Advance(delay);
                return Task.CompletedTask;
            }
        };
        Exception? failure = null;
        try
        {
            var route = DropboxRouteTable.Embedded.Find("files/list_folder")!;
            using var result = await client.CallRouteAsync(new DropboxRouteCall(route, "{}"),
                DropboxCliSettings.Default.Retry with { ReadTransientBudget = client.ReadTransientBudget }, CancellationToken.None);
        }
        catch (Exception error) { failure = error; }
        Check(failure is DropboxApiException { StatusCode: HttpStatusCode.ServiceUnavailable },
            $"budget exhaustion after repeated 5xx keeps the refusal status ({failure?.GetType().Name})");
        Check(wire.Attempts > 1, "the refusal was retried before exhaustion");
        Console.WriteLine("PASS read budget exhaustion retains the last received HTTP refusal status");
    }

    // A refusal seen on an earlier attempt must not be lost when the following attempts fail in
    // transport (a statusless timeout with no response). Exhaustion must still surface the refusal
    // status rather than a statusless HttpRequestException.
    internal static async Task RunRefusalThenTransportExhaustionAsync()
    {
        var clock = new Clock();
        using var wire = new Wire(clock, "refusal-then-timeout");
        using var http = new HttpClient(wire);
        var client = new DropboxPilotClient(http, new Tokens(), "/Pilot", Binding(), "id:pilot-folder", transferLimiter: Limiter(clock))
        {
            ReadRetryTime = clock,
            ReadTransientBudget = TimeSpan.FromSeconds(10),
            ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Advance(delay);
                return Task.CompletedTask;
            }
        };
        Exception? failure = null;
        try
        {
            var route = DropboxRouteTable.Embedded.Find("files/list_folder")!;
            using var result = await client.CallRouteAsync(new DropboxRouteCall(route, "{}"),
                DropboxCliSettings.Default.Retry with { ReadTransientBudget = client.ReadTransientBudget }, CancellationToken.None);
        }
        catch (Exception error) { failure = error; }
        Check(failure is DropboxApiException { StatusCode: HttpStatusCode.ServiceUnavailable },
            $"a refusal kept before transport timeouts survives exhaustion ({failure?.GetType().Name}: {failure?.Message})");
        Check(wire.Attempts > 2, "the refusal and the following transport timeouts were retried before exhaustion");
        Console.WriteLine("PASS read budget exhaustion after a refusal then transport timeouts keeps the refusal status");
    }

    // Preparing an authorized request (token refresh, account verification) happens inside the read
    // recovery boundary, so a transient outage while refreshing the token is retried rather than
    // aborting the read outright.
    internal static async Task RunTokenRefreshAsync()
    {
        var clock = new Clock();
        var source = new FlakyTokenSource(failures: 1);
        using var wire = new Wire(clock, "healthy");
        using var http = new HttpClient(wire);
        var client = new DropboxPilotClient(http, source, "/Pilot", Binding(), "id:pilot-folder", transferLimiter: Limiter(clock))
        {
            ReadRetryTime = clock,
            ReadTransientBudget = TimeSpan.FromSeconds(60),
            ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Advance(delay);
                return Task.CompletedTask;
            }
        };
        // A read route builds its authorized request (token refresh) inside the recovery boundary.
        var route = DropboxRouteTable.Embedded.Find("files/get_metadata")!;
        using var result = await client.CallRouteAsync(new DropboxRouteCall(route, "{}"),
            DropboxCliSettings.Default.Retry with { ReadTransientBudget = client.ReadTransientBudget }, CancellationToken.None);
        Check(result.Result.RootElement.GetProperty("id").GetString() == "id:pilot-folder",
            "the route read completed after a transient token-refresh failure");
        Check(source.Calls > 1, "the transient token-refresh failure was retried");
        Console.WriteLine("PASS read retry absorbs a transient token-refresh failure during request preparation");
    }

    private sealed class Wire(Clock clock, string mode) : HttpMessageHandler
    {
        internal int Attempts;
        internal int CooldownWaits;
        private DateTimeOffset _notBefore;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(clock.GetUtcNow() >= _notBefore, "no request is sent before the server deadline");
            if (_notBefore != default) { CooldownWaits++; _notBefore = default; }
            // A full listing first verifies account/root identities. Those are healthy, offline fixture replies.
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("get_current_account")) return Reply("{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}");
            if (path.EndsWith("get_metadata")) return Reply("{\".tag\":\"folder\",\"id\":\"id:pilot-folder\",\"name\":\"Pilot\",\"path_lower\":\"/pilot\",\"path_display\":\"/Pilot\"}");
            Attempts++;
            if (mode == "refusal-then-timeout")
            {
                // The first attempt is a refusal (503); every later attempt fails in transport with no
                // response, so only a status kept across attempts can surface the refusal at exhaustion.
                if (Attempts == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                throw new HttpRequestException("transport timeout after a refusal", new TimeoutException());
            }
            if (mode is "auth" or "permission" or "conflict") return Task.FromResult(new HttpResponseMessage(
                mode == "auth" ? HttpStatusCode.Unauthorized : mode == "permission" ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict)
                { Content = new StringContent("{}") });
            if (mode == "always-server") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (clock.GetUtcNow() < DateTimeOffset.UnixEpoch.AddSeconds(20) || mode is "exhausted" or "cancel" or "healthy")
            {
                if (mode is "server" or "cooldown")
                {
                    var response = new HttpResponseMessage(mode == "server" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests);
                    if (mode == "cooldown")
                    {
                        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(5));
                        _notBefore = clock.GetUtcNow().AddSeconds(5);
                    }
                    return Task.FromResult(response);
                }
                if (mode == "body") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StreamContent(new BrokenBody()) });
                throw mode switch
                {
                    "socket" => new SocketException((int)SocketError.ConnectionReset),
                    "timeout" => new OperationCanceledException("non-user timeout"),
                    _ => new HttpRequestException("DNS/reset", new SocketException((int)SocketError.HostNotFound))
                };
            }
            return Reply("{\"entries\":[],\"cursor\":\"cursor-end\",\"has_more\":false}");
        }
        private static Task<HttpResponseMessage> Reply(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body) });
    }

    private sealed class BrokenBody : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromException<int>(new IOException("interrupted response body"));
    }
    private sealed class Tokens : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("synthetic-token");
    }
    // Fails the first `failures` token resolutions with a transient transport error, then succeeds.
    private sealed class FlakyTokenSource(int failures) : IDropboxAccessTokenSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) <= failures)
                return Task.FromException<string>(new HttpRequestException("token refresh interrupted",
                    new SocketException((int)SocketError.ConnectionReset)));
            return Task.FromResult("synthetic-token");
        }
    }
    private static DropboxConnectionBinding Binding() => new(DropboxProviderConfiguration.RegisteredClientId,
        "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
        DropboxProviderConfiguration.RequiredScopes);
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
