using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Massdex.SyncShark.V2;

// C30: every provider error-body read is bounded by size, by the caller's cancellation and by an idle
// deadline, and pacing leaves the body readable for the adapter that still has to classify the failure.
// One provider-intake fixture: every check exercises the same intake transport seams, so it stays one file.
internal static class ProviderIntakeChecks
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(200);

    internal static async Task RunAsync()
    {
        await BoundedErrorBodyReaderAsync();
        await SharedJsonReaderIdleAsync();
        await ProviderJsonReaderSeamsAsync();
        await PacingKeepsTheErrorBodyAsync();
        await PacingHonoursCallerCancellationAsync();
        await PacingUnreadableBodyAsync(stalled: false);
        await PacingUnreadableBodyAsync(stalled: true);
        await ProductionFactoryAsync();
        GuardedPoolReconstruction();
        await CompositionOwnershipAsync();
        TimeoutTexts();
        await ReadTimeoutAsync();
        await WriteTimeoutAsync();
        await CancellationAndDisposalOrderAsync();
        await RenewalAndReentrantCompletionAsync();
        await SuccessfulControlsAsync();
        await DeterministicMultiSliceAsync();
        await PeerFaultGapAsync();
        await QueuedTimersAndConcurrencyAsync();
        MessageCatalogCost();
        Console.WriteLine("--provider-intake-checks passed.");
    }

    // The one bounded error-body reader: it returns the body, keeps it readable, refuses an oversized body,
    // and aborts a body that sends headers and then no bytes.
    private static async Task BoundedErrorBodyReaderAsync()
    {
        const string throttle = """{"error":{".tag":"too_many_write_operations"},"error_summary":"too_many_write_operations/..."}""";
        using (var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
               { Content = new StringContent(throttle, Encoding.UTF8, "application/json") })
        {
            var text = await SyncResponseBody.ReadAndReplaceAsync(response, SyncResponseBody.MaximumErrorBodyBytes, Idle, "The provider returned an oversized error response.", CancellationToken.None);
            Require(text == throttle, "the bounded error reader returns the exact body");
            Require(await response.Content.ReadAsStringAsync() == throttle,
                "the bounded error reader leaves the body readable for the adapter");
        }

        using (var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
               { Content = new ByteArrayContent(new byte[SyncResponseBody.MaximumErrorBodyBytes + 1]) })
        {
            Exception? failure = null;
            try { await SyncResponseBody.ReadAndReplaceAsync(response, SyncResponseBody.MaximumErrorBodyBytes, Idle, "The provider returned an oversized error response.", CancellationToken.None); }
            catch (Exception error) { failure = error; }
            Require(failure is InvalidDataException,
                $"an error body past {SyncResponseBody.MaximumErrorBodyBytes} bytes is refused at the size bound ({failure?.GetType().Name})");
        }

        using (var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
               { Content = new StreamContent(new StallingBody()) })
        {
            var started = TimeProvider.System.GetTimestamp();
            Exception? failure = null;
            try { await SyncResponseBody.ReadAndReplaceAsync(response, SyncResponseBody.MaximumErrorBodyBytes, Idle, "The provider returned an oversized error response.", CancellationToken.None); }
            catch (Exception error) { failure = error; }
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            Require(failure is ProviderTransportTimeoutException,
                $"a stalled error body is aborted by the idle bound ({failure?.GetType().Name}: {failure?.Message})");
            Require(elapsed < TimeSpan.FromSeconds(5), $"the stalled error body failed promptly ({elapsed.TotalSeconds:N1}s)");
        }
    }

    // The idle deadline the provider JSON seams inherit from the shared reader, exercised at a test-sized
    // bound so the production 60-second ceiling is never waited for.
    private static async Task SharedJsonReaderIdleAsync()
    {
        using var stalled = new StallingBody();
        Exception? failure = null;
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            using var document = await SyncResponseBody.ReadJsonAsync(stalled,
                SyncResponseBody.MaximumErrorBodyBytes, Idle,
                "The provider returned an oversized JSON body.", CancellationToken.None);
        }
        catch (Exception error) { failure = error; }
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        Require(failure is ProviderTransportTimeoutException,
            $"a stalled JSON body is aborted by the shared idle bound ({Describe(failure)})");
        Require(elapsed < TimeSpan.FromSeconds(5),
            $"the stalled JSON body failed promptly ({elapsed.TotalSeconds:N1}s)");
    }

    // Finding 7: the last two copied provider JSON readers now delegate to the one neutral bounded reader.
    // Each seam keeps its private signature, its own ceiling and its oversized text, and still ends a
    // stalled body on the caller's cancellation. Every case is in-memory: no request, socket or sleep.
    private static async Task ProviderJsonReaderSeamsAsync()
    {
        var google = JsonReaderSeam(typeof(GoogleDrivePilotClient));
        var oauth = JsonReaderSeam(typeof(DropboxOAuth));
        await AssertJsonReaderSeamAsync(google, GoogleDrivePilotClient.MaximumJsonResponseBytes,
            "Google Drive returned an oversized JSON response.",
            """{"id":"file-1","name":"a.txt"}""");
        await AssertJsonReaderSeamAsync(oauth, OAuthJsonCeiling(),
            "Dropbox returned an oversized OAuth or account response.",
            """{"access_token":"token","expires_in":14400}""");

        // The two behaviours the shared reader now parameterises stay owned by their callers: Google keeps
        // its empty-body "{}" acceptance and the OAuth/account reader keeps its 32-level nesting ceiling.
        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new ByteArrayContent([]) })
        using (var document = await google(response, CancellationToken.None))
            Require(document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.GetRawText() == "{}",
                "the Google Drive seam still accepts an empty body as an empty JSON object");

        var deeplyNested = new StringBuilder();
        for (var level = 0; level < 40; level++) deeplyNested.Append("{\"a\":");
        deeplyNested.Append('0');
        for (var level = 0; level < 40; level++) deeplyNested.Append('}');
        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StringContent(deeplyNested.ToString(), Encoding.UTF8, "application/json") })
        {
            var failure = await CaptureFailureAsync(() => oauth(response, CancellationToken.None));
            Require(failure is JsonException,
                $"the OAuth seam still refuses JSON nesting past 32 levels ({Describe(failure)})");
        }
    }

    private static Func<HttpResponseMessage, CancellationToken, Task<JsonDocument>> JsonReaderSeam(Type owner)
    {
        var method = owner.GetMethod("ReadBoundedJsonAsync", BindingFlags.NonPublic | BindingFlags.Static, null,
            [typeof(HttpResponseMessage), typeof(CancellationToken)], null)
            ?? throw new InvalidOperationException(
                $"{owner.Name} no longer has the private bounded JSON reader this check covers.");
        return method.CreateDelegate<Func<HttpResponseMessage, CancellationToken, Task<JsonDocument>>>();
    }

    // The ceiling stays owned by DropboxOAuth; the fixture reads it so a changed limit cannot leave a test
    // body under the bound, where it would stall instead of proving the size check.
    private static int OAuthJsonCeiling()
    {
        var field = typeof(DropboxOAuth).GetField("MaximumOAuthJsonBytes",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DropboxOAuth no longer declares its OAuth JSON ceiling.");
        return (int)field.GetRawConstantValue()!;
    }

    private static async Task AssertJsonReaderSeamAsync(
        Func<HttpResponseMessage, CancellationToken, Task<JsonDocument>> readJson, int maximumBytes,
        string oversizedMessage, string validJson)
    {
        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StringContent(validJson, Encoding.UTF8, "application/json") })
        using (var document = await readJson(response, CancellationToken.None))
            Require(document.RootElement.ValueKind == JsonValueKind.Object,
                "a provider JSON body is parsed through the shared reader");

        // A declared length past the seam's own ceiling is refused before a byte is read.
        using (var response = new HttpResponseMessage(HttpStatusCode.OK))
        {
            response.Content = new StreamContent(new StallingBody());
            response.Content.Headers.ContentLength = maximumBytes + 1L;
            var failure = await CaptureFailureAsync(() => readJson(response, CancellationToken.None));
            Require(failure is InvalidDataException && failure.Message == oversizedMessage,
                $"a declared body over the seam ceiling is refused at the shared size check ({Describe(failure)})");
        }

        // A body with no declared length is refused by the shared streaming bound.
        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StreamContent(new OverlongBody(maximumBytes + 1L)) })
        {
            var failure = await CaptureFailureAsync(() => readJson(response, CancellationToken.None));
            Require(failure is InvalidDataException && failure.Message == oversizedMessage,
                $"a streamed body over the seam ceiling is refused by the shared bound ({Describe(failure)})");
        }

        // A stalled body still ends on the caller's cancellation, promptly.
        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StreamContent(new StallingBody()) })
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            var started = TimeProvider.System.GetTimestamp();
            var failure = await CaptureFailureAsync(() => readJson(response, cancel.Token));
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            Require(failure is OperationCanceledException,
                $"caller cancellation still ends a stalled provider JSON body ({Describe(failure)})");
            Require(elapsed < TimeSpan.FromSeconds(5),
                $"the cancelled provider JSON read ended promptly ({elapsed.TotalSeconds:N1}s)");
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static string Describe(Exception? failure) =>
        failure is null ? "no exception" : $"{failure.GetType().Name}: {failure.Message}";

    // A Dropbox write-throttle answer is still classified through the bounded reader, the read lane is never
    // throttled for a write-only refusal, and the response body remains readable after classification.
    private static async Task PacingKeepsTheErrorBodyAsync()
    {
        // The flat write-throttle spelling the classifier already tests: an exact too_many_write_operations tag.
        var body = """{"error":{".tag":"too_many_write_operations"}}""";
        var limiter = new SyncTransferRateLimiter(new());
        var handler = new FixedHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://fixture.invalid/2/files/create_folder_batch");
        using var received = await SyncProviderRequestPacing.SendAsync(limiter,
            new("error-body-retention", SyncProviderOperation.Write), false, http, request,
            HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        Require(limiter.Resources.ProviderDelay(new("error-body-retention", SyncProviderOperation.Write)) > TimeSpan.Zero,
            "a write-only throttle still throttles the write lane");
        Require(limiter.Resources.ProviderDelay(new("error-body-retention", SyncProviderOperation.Read)) == TimeSpan.Zero,
            "a write-only throttle never closes the read lane");
        Require(await received.Content.ReadAsStringAsync() == body,
            "pacing leaves the error body readable for the adapter after classification");
    }

    // A stalled classification body must not outlive the caller's own cancellation.
    private static async Task PacingHonoursCallerCancellationAsync()
    {
        var limiter = new SyncTransferRateLimiter(new());
        var handler = new FixedHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StreamContent(new StallingBody()) };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://fixture.invalid/2/files/create_folder_batch");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var started = TimeProvider.System.GetTimestamp();
        Exception? failure = null;
        try
        {
            await SyncProviderRequestPacing.SendAsync(limiter,
                new("error-body-cancel", SyncProviderOperation.Write), false, http, request,
                HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        }
        catch (Exception error) { failure = error; }
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        Require(failure is OperationCanceledException,
            $"caller cancellation wins over a stalled classification body ({failure?.GetType().Name})");
        Require(elapsed < TimeSpan.FromSeconds(5), $"the cancelled classification ended promptly ({elapsed.TotalSeconds:N1}s)");
    }

    private static async Task PacingUnreadableBodyAsync(bool stalled)
    {
        var limiter = new SyncTransferRateLimiter(new());
        using var http = new HttpClient(new FixedHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = stalled ? new StreamContent(new StallingBody()) :
                    new ByteArrayContent(new byte[SyncResponseBody.MaximumErrorBodyBytes + 1])
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        }));
        var key = "unreadable-" + stalled;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://fixture.invalid/request");
        using var received = await SyncProviderRequestPacing.SendAsync(limiter,
            new(key, SyncProviderOperation.Write), false, http, request,
            HttpCompletionOption.ResponseHeadersRead, CancellationToken.None, errorBodyIdleTimeout: Idle);
        Require(await received.Content.ReadAsStringAsync() == string.Empty,
            "a failed classification leaves a bounded readable fallback body");
        Require(limiter.Resources.ProviderDelay(new(key, SyncProviderOperation.Read)) > TimeSpan.Zero &&
            limiter.Resources.ProviderDelay(new(key, SyncProviderOperation.Write)) > TimeSpan.Zero,
            "an unreadable throttle body conservatively pauses both lanes");
    }

    // The actual production factory: every call owns one guarded pool with the requested decompression, redirects
    // off, the sourced connect allowance and an infinite total timeout; the shared guard is the same stream the
    // bounded intake readers above consume.
    private static async Task ProductionFactoryAsync()
    {
        Require(Throws<ArgumentNullException>(() => EngineComposition.CreateDropboxHttpClient(
                DropboxApiLimits.ReadWriteAllowance, DecompressionMethods.None, null!)),
            "the production factory refuses a missing time provider");
        Require(Throws<ArgumentOutOfRangeException>(() => EngineComposition.CreateDropboxHttpClient(
                TimeSpan.Zero, DecompressionMethods.None, TimeProvider.System)),
            "the production factory refuses a non-positive read allowance");

        using var ordinary = EngineComposition.CreateDropboxHttpClient(DropboxApiLimits.ReadWriteAllowance,
            DecompressionMethods.GZip | DecompressionMethods.Deflate, TimeProvider.System);
        using var longpoll = EngineComposition.CreateDropboxHttpClient(
            EngineComposition.LongpollTimeoutFor(V2Settings.Default), DecompressionMethods.None, TimeProvider.System);
        var ordinaryPool = HandlerOf(ordinary);
        var longpollPool = HandlerOf(longpoll);
        Require(!ReferenceEquals(ordinaryPool, longpollPool),
            "each factory call owns its own ordinary or long-poll pool");
        foreach (var (client, handler, decompression) in new[]
                 {
                     (ordinary, ordinaryPool, DecompressionMethods.GZip | DecompressionMethods.Deflate),
                     (longpoll, longpollPool, DecompressionMethods.None)
                 })
        {
            Require(client.Timeout == Timeout.InfiniteTimeSpan,
                "a guarded factory client has no total timeout");
            Require(client.DefaultRequestVersion == System.Net.HttpVersion.Version11 &&
                    client.DefaultVersionPolicy == System.Net.Http.HttpVersionPolicy.RequestVersionExact,
                "a guarded factory client sends the exact HTTP/1.1 request version");
            Require(!handler.AllowAutoRedirect, "a guarded factory client retains automatic redirects off");
            Require(handler.ConnectTimeout == DropboxApiLimits.ConnectTimeout,
                "a guarded factory client uses the sourced connect allowance");
            Require(handler.AutomaticDecompression == decompression,
                "a guarded factory client retains its requested decompression");
            Require(handler.PlaintextStreamFilter is not null,
                "a guarded factory client installs the one shared plaintext guard");
        }

        // The same guard must serve the existing bounded intake readers: the factory's filter wraps the transport
        // stream, the bounded reader consumes it exactly, and disposal keeps owning the transport stream.
        const string guardedBody = """{"error":{".tag":"too_many_write_operations"}}""";
        var inner = new MemoryStream(Encoding.UTF8.GetBytes(guardedBody));
        var guard = await GuardedStreamAsync(longpollPool, inner);
        Require(guard is Massdex.SyncShark.Core.ProviderActivityStream,
            "the factory's plaintext filter installs the shared Core activity guard");
        using (var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
               { Content = new StreamContent(guard) })
        {
            var text = await SyncResponseBody.ReadAndReplaceAsync(response, SyncResponseBody.MaximumErrorBodyBytes,
                Idle, "The provider returned an oversized error response.", CancellationToken.None);
            Require(text == guardedBody, "the bounded reader returns the exact body through the shared guard");
            Require(await response.Content.ReadAsStringAsync() == guardedBody,
                "the guarded body stays readable for the adapter after classification");
        }
        Require(Throws<ObjectDisposedException>(() => inner.WriteByte(0)),
            "disposing the guarded response disposes the owned transport stream");
    }

    // The immutable long-poll generations: each supported setting derives its own long-poll read allowance, the
    // ordinary pool keeps its sourced 120 seconds, and the live client's timeout stays infinite, never the
    // allowance. A settings change is a new composition, not a hot rebind.
    private static void GuardedPoolReconstruction()
    {
        var first = V2Settings.Default;
        var next = first with { DropboxLongpollTimeout = TimeSpan.FromSeconds(DropboxApiLimits.LongpollMaxSeconds) };
        Require(first.DropboxLongpollTimeout == TimeSpan.FromSeconds(120),
            "the shipped long-poll request setting is 120 seconds");
        Require(EngineComposition.LongpollTimeoutFor(first) == TimeSpan.FromSeconds(210),
            "request 120 derives the long-poll read allowance 210");
        Require(EngineComposition.LongpollTimeoutFor(next) == TimeSpan.FromSeconds(570),
            "the next supported setting 480 derives the read allowance 570");
        Require(DropboxApiLimits.ReadWriteAllowance == TimeSpan.FromSeconds(120),
            "the ordinary pool keeps its sourced 120-second read/write allowance");

        var root = Massdex.SyncShark.V2.LocalChecks.FreshRoot(
            "provider-intake-longpoll-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new FixedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        using (var composition = EngineComposition.Open(
                   new EngineHostInputs(Path.Combine(root, "first"), null, http,
                       LongpollHandler: new TrackingHandler(() => new HttpResponseMessage(HttpStatusCode.OK))),
                   first, TimeProvider.System))
        {
            Require(composition.LongpollHttpTimeout == Timeout.InfiniteTimeSpan,
                "the open composition's long-poll client has no total timeout");
            Require(composition.LongpollHttpTimeout != EngineComposition.LongpollTimeoutFor(composition.Settings),
                "LongpollHttpTimeout is the client timeout, never the read allowance");
            Require(composition.Settings.DropboxLongpollTimeout == TimeSpan.FromSeconds(120) &&
                    EngineComposition.LongpollTimeoutFor(composition.Settings) == TimeSpan.FromSeconds(210),
                "the first immutable generation is request120/read210");
        }

        var nextOwned = new TrackingHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using (var composition = EngineComposition.Open(
                   new EngineHostInputs(Path.Combine(root, "next"), null, http, LongpollHandler: nextOwned),
                   next, TimeProvider.System))
            Require(composition.Settings.DropboxLongpollTimeout == TimeSpan.FromSeconds(480) &&
                    EngineComposition.LongpollTimeoutFor(composition.Settings) == TimeSpan.FromSeconds(570),
                "the next immutable generation reconstructs request480/read570");
        Require(nextOwned.Disposed, "the next generation's owned long-poll client is disposed on close");
    }

    // The borrowed ordinary pool and the owned long-poll pool: the borrowed client is used exactly as supplied and
    // never disposed here, while the long-poll client the composition builds is disposed on normal close and on a
    // failed Open.
    private static async Task CompositionOwnershipAsync()
    {
        var settings = V2Settings.Default;
        var root = Massdex.SyncShark.V2.LocalChecks.FreshRoot(
            "provider-intake-ownership-" + Guid.NewGuid().ToString("N"));
        using var ordinaryHttp = new HttpClient(new FixedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));

        var owned = new TrackingHandler(() => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using (EngineComposition.Open(new EngineHostInputs(Path.Combine(root, "owned-close"), null, ordinaryHttp,
                   LongpollHandler: owned), settings, TimeProvider.System))
            Require(!owned.Disposed, "the owned long-poll pool is live while the composition is open");
        Require(owned.Disposed, "the owned long-poll pool is disposed on normal close");

        var refused = new TrackingHandler(() => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var damagedRoot = Path.Combine(root, "owned-failure");
        var damagedDb = SqliteStateStore.DatabasePath(damagedRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(damagedDb)!);
        File.WriteAllBytes(damagedDb, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        Require(Throws<StateDamagedException>(() => EngineComposition.Open(
                new EngineHostInputs(damagedRoot, null, ordinaryHttp, LongpollHandler: refused),
                settings, TimeProvider.System).Dispose()),
            "a failed Open is still reported, never silently recreated");
        Require(refused.Disposed, "the owned long-poll pool is disposed when Open fails");

        // The exact injected ordinary client answers the composition's Dropbox route: neither Open nor close
        // replaces, disposes or swallows that client's own handler behaviour.
        var product = Path.Combine(root, "borrowed");
        Directory.CreateDirectory(product);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(product, "state", "instance.lock"));
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id",
            "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        var borrowed = new RevokedHandler();
        using var borrowedHttp = new HttpClient(borrowed);
        using (var composition = EngineComposition.Open(
                   new EngineHostInputs(product, instance.StateAuthority, borrowedHttp, new StoredToken(),
                       new TrackingHandler(() => new HttpResponseMessage(HttpStatusCode.BadGateway))),
                   settings, TimeProvider.System))
        {
            Require(await ThrowsAsync<EndpointAccessException>(() =>
                    composition.Roots.ObjectIdAsync(SyncProviders.Dropbox, binding.ConnectionId, "/a", default)),
                "the exact injected ordinary handler answers the composition's Dropbox route");
            Require(borrowed.Sends > 0, "the composition's Dropbox route reaches the borrowed ordinary handler");
        }
        Require(!borrowed.Disposed, "the borrowed ordinary handler is not disposed by the composition");
        using (EngineComposition.Open(new EngineHostInputs(Path.Combine(root, "borrowed-again"), null, borrowedHttp),
                   settings, TimeProvider.System))
            Require(!borrowed.Disposed, "the host still owns a usable ordinary client for the next composition");
    }

    // The handler behind a guarded client and the plaintext context are only reachable through the framework's
    // non-public members; no public client surface exposes the factory's guard or decompression choice.
    private static System.Net.Http.SocketsHttpHandler HandlerOf(HttpClient client) =>
        (System.Net.Http.SocketsHttpHandler)typeof(System.Net.Http.HttpMessageInvoker)
            .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;

    private static async Task<Stream> GuardedStreamAsync(System.Net.Http.SocketsHttpHandler handler, Stream inner)
    {
        var contextType = typeof(System.Net.Http.SocketsHttpPlaintextStreamFilterContext);
        var constructor = contextType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(Stream), typeof(Version), typeof(System.Net.Http.HttpRequestMessage)], null)
            ?? throw new InvalidOperationException("The plaintext filter context constructor was not found.");
        var context = constructor.Invoke(new object?[] { inner, System.Net.HttpVersion.Version11, null });
        return await handler.PlaintextStreamFilter!(
            (System.Net.Http.SocketsHttpPlaintextStreamFilterContext)context, CancellationToken.None);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }

    private sealed class TrackingHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Sends { get; private set; }
        internal bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++;
            return Task.FromResult(response());
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class RevokedHandler : HttpMessageHandler
    {
        internal int Sends { get; private set; }
        internal bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant","error_description":"revoked"}""",
                    Encoding.UTF8, "application/json")
            });
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StoredToken : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fixture-refresh-token");

        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(response());
    }

    // A body that sends nothing after its headers and only ends when its read is cancelled.
    private sealed class StallingBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A body with no declared length that keeps producing bytes past any caller ceiling, without
    // allocating them up front: the shared reader must stop it at the size bound.
    private sealed class OverlongBody(long length) : Stream
    {
        private long _remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (_remaining == 0) return ValueTask.FromResult(0);
            var count = (int)Math.Min(_remaining, buffer.Length);
            buffer.Span[..count].Fill((byte)' ');
            _remaining -= count;
            return ValueTask.FromResult(count);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // AUDIT-D1-rev5 C: the three transport catalog texts are exact, and the grouped N0 placeholders format in
    // the current culture exactly as the old literal/interpolation expressions did.
    private static void TimeoutTexts()
    {
        const string faulted = "An earlier provider transport operation failed; this connection cannot be reused.";
        const string read = "The provider made no read or write progress for {0:N0} seconds; the pending read was aborted.";
        const string write = "The provider did not complete a write within {0:N0} seconds; the write was aborted.";
        var catalog = MessageCatalog.Default;
        Require(catalog.Message("provider-transport-faulted") == faulted, "the faulted transport text is exact");
        Require(catalog.Message("provider-transport-read-timeout") == read, "the read-timeout text is exact");
        Require(catalog.Message("provider-transport-write-timeout") == write, "the write-timeout text is exact");
        Require(read.Contains("{0:N0}", StringComparison.Ordinal) && write.Contains("{0:N0}", StringComparison.Ordinal),
            "both timeout texts keep the grouped N0 placeholder");
        const double readSeconds = 1234;
        const double writeSeconds = 2500;
        Require(string.Format(CultureInfo.CurrentCulture, read, readSeconds) ==
            $"The provider made no read or write progress for {readSeconds:N0} seconds; the pending read was aborted.",
            "the read timeout formats like the current-culture old interpolation");
        Require(string.Format(CultureInfo.CurrentCulture, write, writeSeconds) ==
            $"The provider did not complete a write within {writeSeconds:N0} seconds; the write was aborted.",
            "the write timeout formats like the current-culture old interpolation");
    }

    // Both read-timeout construction routes: the timer-owned expiry, then a completion that is already
    // overdue without its timer firing. Each reports the exact grouped read text, and a new call after the
    // fault is refused with the exact faulted text.
    private static async Task ReadTimeoutAsync()
    {
        const int seconds = 1234;
        var allowance = TimeSpan.FromSeconds(seconds);
        var expected = string.Format(CultureInfo.CurrentCulture,
            MessageCatalog.Default.Message("provider-transport-read-timeout"), (double)seconds);

        var clock = new ManualClock();
        var body = new ControlledBody();
        var guard = new Massdex.SyncShark.Core.ProviderActivityStream(
            body, allowance, allowance, 4, clock);
        var pending = guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask();
        clock.Advance(allowance + TimeSpan.FromSeconds(1));
        var fired = await CaptureTaskFailure(pending);
        Require(fired is ProviderTransportTimeoutException && fired.Message == expected,
            $"the timer-owned read expiry reports the exact grouped read text ({Describe(fired)})");
        await AssertFaultedCallAsync(guard);
        guard.Dispose();

        clock = new ManualClock(fireTimers: false);
        body = new ControlledBody();
        guard = new Massdex.SyncShark.Core.ProviderActivityStream(body, allowance, allowance, 4, clock);
        pending = guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask();
        clock.Advance(allowance + TimeSpan.FromSeconds(1));
        body.ReleaseRead(4);
        var overdue = await CaptureTaskFailure(pending);
        Require(overdue is ProviderTransportTimeoutException && overdue.Message == expected,
            $"an overdue read completion reports the exact grouped read text ({Describe(overdue)})");
        guard.Dispose();
    }

    // Both write-timeout construction routes, mirroring the read routes and reporting the exact grouped
    // write text.
    private static async Task WriteTimeoutAsync()
    {
        const int seconds = 2500;
        var allowance = TimeSpan.FromSeconds(seconds);
        var expected = string.Format(CultureInfo.CurrentCulture,
            MessageCatalog.Default.Message("provider-transport-write-timeout"), (double)seconds);

        var clock = new ManualClock();
        var body = new ControlledBody { BlockWrites = true };
        var guard = new Massdex.SyncShark.Core.ProviderActivityStream(body, allowance, allowance, 4, clock);
        var pending = guard.WriteAsync((ReadOnlyMemory<byte>)new byte[4], CancellationToken.None).AsTask();
        clock.Advance(allowance + TimeSpan.FromSeconds(1));
        var fired = await CaptureTaskFailure(pending);
        Require(fired is ProviderTransportTimeoutException && fired.Message == expected,
            $"the timer-owned write expiry reports the exact grouped write text ({Describe(fired)})");
        await AssertFaultedCallAsync(guard);
        guard.Dispose();

        clock = new ManualClock(fireTimers: false);
        body = new ControlledBody { BlockWrites = true };
        guard = new Massdex.SyncShark.Core.ProviderActivityStream(body, allowance, allowance, 4, clock);
        pending = guard.WriteAsync((ReadOnlyMemory<byte>)new byte[4], CancellationToken.None).AsTask();
        clock.Advance(allowance + TimeSpan.FromSeconds(1));
        body.ReleaseWrite();
        var overdue = await CaptureTaskFailure(pending);
        Require(overdue is ProviderTransportTimeoutException && overdue.Message == expected,
            $"an overdue write completion reports the exact grouped write text ({Describe(overdue)})");
        guard.Dispose();
    }

    // Caller cancellation is checked before disposal: a cancelled token on a disposed guard wins, while a
    // live token on the same guard reports disposal.
    private static async Task CancellationAndDisposalOrderAsync()
    {
        // Caller cancellation of an in-flight read reports cancellation, never the owned transport timeout.
        var liveBody = new ControlledBody();
        var liveGuard = new Massdex.SyncShark.Core.ProviderActivityStream(
            liveBody, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100), 4, new ManualClock());
        using var live = new CancellationTokenSource();
        var livePending = liveGuard.ReadAsync(new byte[4].AsMemory(), live.Token).AsTask();
        live.Cancel();
        var cancelledRead = await CaptureTaskFailure(livePending);
        Require(cancelledRead is not ProviderTransportTimeoutException && cancelledRead is OperationCanceledException,
            $"caller cancellation wins over the owned transport timeout ({Describe(cancelledRead)})");
        liveGuard.Dispose();

        var body = new ControlledBody();
        var guard = new Massdex.SyncShark.Core.ProviderActivityStream(
            body, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100), 4, new ManualClock());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var read = await CaptureTaskFailure(guard.ReadAsync(new byte[1].AsMemory(), cancelled.Token).AsTask());
        Require(read is OperationCanceledException,
            $"caller cancellation wins a pending-operation decision ({Describe(read)})");
        guard.Dispose();
        var afterDisposeLive = await CaptureTaskFailure(
            guard.ReadAsync(new byte[1].AsMemory(), CancellationToken.None).AsTask());
        Require(afterDisposeLive is ObjectDisposedException,
            $"a disposed guard refuses a live read ({Describe(afterDisposeLive)})");
        var afterDisposeCancelled = await CaptureTaskFailure(
            guard.ReadAsync(new byte[1].AsMemory(), cancelled.Token).AsTask());
        Require(afterDisposeCancelled is OperationCanceledException,
            $"cancellation is checked before disposal on a disposed guard ({Describe(afterDisposeCancelled)})");
        var write = await CaptureTaskFailure(
            guard.WriteAsync((ReadOnlyMemory<byte>)new byte[1], CancellationToken.None).AsTask());
        Require(write is ObjectDisposedException, $"a disposed guard refuses a live write ({Describe(write)})");
        var flush = await CaptureTaskFailure(guard.FlushAsync(CancellationToken.None));
        Require(flush is ObjectDisposedException, $"a disposed guard refuses a live flush ({Describe(flush)})");
    }

    // A completed nonempty write renews an already-pending read inside the same gate; an overdue read is
    // never revived by that completion; read activity does not extend a pending write; and a zero-length
    // write is not progress.
    private static async Task RenewalAndReentrantCompletionAsync()
    {
        var readAllowance = TimeSpan.FromSeconds(100);
        var writeAllowance = TimeSpan.FromSeconds(300);

        // A completed nonempty write renews a read that then outlives its original allowance.
        var clock = new ManualClock();
        var body = new ControlledBody();
        var guard = new Massdex.SyncShark.Core.ProviderActivityStream(body, readAllowance, writeAllowance, 4, clock);
        var read = guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromSeconds(90));
        await guard.WriteAsync((ReadOnlyMemory<byte>)new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(90));
        body.ReleaseRead(3);
        Require(await read == 3,
            "a completed nonempty write renews a pending read past its original allowance");
        guard.Dispose();

        // A pending read already past its allowance when the write completes is expired, not revived.
        clock = new ManualClock(fireTimers: false);
        body = new ControlledBody();
        guard = new Massdex.SyncShark.Core.ProviderActivityStream(body, readAllowance, writeAllowance, 4, clock);
        read = guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromSeconds(150));
        await guard.WriteAsync((ReadOnlyMemory<byte>)new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
        var revived = await CaptureTaskFailure(read);
        Require(revived is ProviderTransportTimeoutException,
            $"a write completion expires an overdue pending read instead of reviving it ({Describe(revived)})");
        guard.Dispose();

        // A completed read does not extend a pending write slice's own deadline.
        clock = new ManualClock();
        body = new ControlledBody { BlockWrites = true };
        guard = new Massdex.SyncShark.Core.ProviderActivityStream(body, writeAllowance, readAllowance, 4, clock);
        var write = guard.WriteAsync((ReadOnlyMemory<byte>)new byte[4], CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromSeconds(90));
        var sideRead = guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask();
        body.ReleaseRead(1);
        Require(await sideRead == 1, "a read completes while a write slice is pending");
        clock.Advance(TimeSpan.FromSeconds(20));
        var expiredWrite = await CaptureTaskFailure(write);
        Require(expiredWrite is ProviderTransportTimeoutException,
            $"read progress does not extend a pending write slice ({Describe(expiredWrite)})");
        guard.Dispose();

        // A zero-length write is not progress, so it does not renew a pending read.
        clock = new ManualClock();
        body = new ControlledBody();
        guard = new Massdex.SyncShark.Core.ProviderActivityStream(body, readAllowance, writeAllowance, 4, clock);
        read = guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromSeconds(50));
        await guard.WriteAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(60));
        var zeroWrite = await CaptureTaskFailure(read);
        Require(zeroWrite is ProviderTransportTimeoutException,
            $"a zero-length write does not renew a pending read ({Describe(zeroWrite)})");
        guard.Dispose();
    }

    // The successful read, write and flush controls, zero-length calls, and disposal of the owned stream.
    private static async Task SuccessfulControlsAsync()
    {
        var body = new ControlledBody();
        var guard = new Massdex.SyncShark.Core.ProviderActivityStream(
            body, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100), 4, new ManualClock());
        var buffer = new byte[4];
        var read = guard.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask();
        body.ReleaseRead(3);
        Require(await read == 3, "a guarded read returns the inner byte count");
        Require(buffer[0] == (byte)'r' && buffer[1] == (byte)'r' && buffer[2] == (byte)'r' && buffer[3] == 0,
            "a guarded read copies exactly the returned bytes");
        await guard.WriteAsync((ReadOnlyMemory<byte>)new byte[] { 1, 2, 3, 4, 5 }, CancellationToken.None);
        Require(body.Written.Count == 5, "a guarded write slices and delivers every byte");
        await guard.WriteAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Require(body.Written.Count == 5, "a zero-length write reaches the inner stream without adding bytes");
        Require(await guard.ReadAsync(Memory<byte>.Empty, CancellationToken.None) == 0,
            "a zero-length read reaches the inner stream");
        await guard.FlushAsync(CancellationToken.None);
        Require(!body.Disposed, "the inner stream is live while the guard is open");
        guard.Dispose();
        Require(body.Disposed, "disposing the guard disposes the owned inner stream");
    }

    // One deterministic multi-slice race: a pending read expires during a write slice that is still inside
    // its own write deadline. That slice completes, the next slice refuses with the exact faulted text, no
    // later byte reaches the inner stream, and the pending read reports the read timeout.
    private static async Task DeterministicMultiSliceAsync()
    {
        var clock = new ManualClock();
        var body = new ControlledBody();
        var guard = new Massdex.SyncShark.Core.ProviderActivityStream(
            body, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(300), 4, clock);
        var read = guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask();
        body.OnWrite = _ => { clock.Advance(TimeSpan.FromSeconds(150)); return ValueTask.CompletedTask; };
        var write = await CaptureTaskFailure(guard.WriteAsync(
            (ReadOnlyMemory<byte>)new byte[8], CancellationToken.None).AsTask());
        Require(write is IOException && write.Message == MessageCatalog.Default.Message("provider-transport-faulted"),
            $"the later write slice is refused with the exact faulted text ({Describe(write)})");
        Require(body.Written.Count == 4,
            $"no byte from the refused or later slices reaches the inner stream (wrote {body.Written.Count})");
        var overdueRead = await CaptureTaskFailure(read);
        Require(overdueRead is ProviderTransportTimeoutException,
            $"the pending read expired during the completed slice ({Describe(overdueRead)})");
        guard.Dispose();
    }

    // A peer fault on the first call records the fault and keeps the peer error; the first new call after
    // it is refused with the exact faulted text.
    private static async Task PeerFaultGapAsync()
    {
        var body = new ControlledBody { OnWrite = _ => throw new IOException("peer transport reset") };
        var guard = new Massdex.SyncShark.Core.ProviderActivityStream(
            body, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100), 4, new ManualClock());
        var peer = await CaptureTaskFailure(
            guard.WriteAsync((ReadOnlyMemory<byte>)new byte[4], CancellationToken.None).AsTask());
        Require(peer is IOException && peer.Message == "peer transport reset",
            $"a peer fault on the first call keeps the peer error ({Describe(peer)})");
        var newRead = await CaptureTaskFailure(guard.ReadAsync(new byte[1].AsMemory(), CancellationToken.None).AsTask());
        Require(newRead is IOException && newRead.Message == MessageCatalog.Default.Message("provider-transport-faulted"),
            $"the first new call after a peer fault refuses with the exact faulted text ({Describe(newRead)})");
        var newWrite = await CaptureTaskFailure(
            guard.WriteAsync((ReadOnlyMemory<byte>)new byte[1], CancellationToken.None).AsTask());
        Require(newWrite is IOException && newWrite.Message == MessageCatalog.Default.Message("provider-transport-faulted"),
            $"a new write after a peer fault refuses with the exact faulted text ({Describe(newWrite)})");
        guard.Dispose();
    }

    // One read and one write at a time, a retired operation's queued timer never expires its successor, and
    // disposal cancels a pending read without a hang.
    private static async Task QueuedTimersAndConcurrencyAsync()
    {
        // fireRetired models a callback already dispatched before disposal; the guard must ignore it.
        var clock = new ManualClock(fireRetired: true);
        var body = new ControlledBody { BlockWrites = true };
        var guard = new Massdex.SyncShark.Core.ProviderActivityStream(
            body, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100), 4, clock);
        var read = guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask();
        var secondRead = await CaptureTaskFailure(guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask());
        Require(secondRead is InvalidOperationException, $"a second concurrent read is refused ({Describe(secondRead)})");
        body.ReleaseRead(1);
        Require(await read == 1, "the first read completes after the refused sibling");

        var write = guard.WriteAsync((ReadOnlyMemory<byte>)new byte[4], CancellationToken.None).AsTask();
        var secondWrite = await CaptureTaskFailure(
            guard.WriteAsync((ReadOnlyMemory<byte>)new byte[4], CancellationToken.None).AsTask());
        Require(secondWrite is InvalidOperationException, $"a second concurrent write is refused ({Describe(secondWrite)})");
        body.ReleaseWrite();
        await write;

        // The completed read's timer is retired; advancing past its old due must not expire the next read.
        clock.Advance(TimeSpan.FromSeconds(150));
        var next = guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromSeconds(50));
        body.ReleaseRead(2);
        Require(await next == 2, "a retired operation's queued timer never expires its successor");
        guard.Dispose();

        body = new ControlledBody();
        guard = new Massdex.SyncShark.Core.ProviderActivityStream(
            body, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100), 4, new ManualClock());
        var pending = guard.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask();
        guard.Dispose();
        var aborted = await CaptureTaskFailure(pending);
        Require(aborted is ObjectDisposedException, $"disposal cancels a pending read ({Describe(aborted)})");
        Require(body.Disposed, "disposal released the owned stream under a pending read");
    }

    // Cost evidence (AUDIT-D1-rev5 P): the direct catalog Load component with fresh streams, labelled cold
    // once and as repetitions, then the warm message and format expressions. A true first-use observation is
    // not claimed because this is not the first action in the process, so every figure is labelled warm.
    // Allocation and elapsed figures are reported as evidence, not used as flaky pass thresholds.
    private static void MessageCatalogCost()
    {
        var assembly = typeof(MessageCatalog).Assembly;
        string? resource = null;
        foreach (var candidate in assembly.GetManifestResourceNames())
        {
            if (!candidate.EndsWith("EdgeMessages.json", StringComparison.Ordinal)) continue;
            Require(resource is null, "the catalog resource name is unambiguous");
            resource = candidate;
        }
        Require(resource is not null, "the embedded edge message catalog is present");

        for (var iteration = 0; iteration < 4; iteration++)
        {
            using var stream = assembly.GetManifestResourceStream(resource!)!;
            var watch = Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var loaded = MessageCatalog.Load(stream);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            watch.Stop();
            Require(loaded.Message("provider-transport-faulted") ==
                    MessageCatalog.Default.Message("provider-transport-faulted") &&
                    loaded.Message("provider-transport-read-timeout") ==
                    MessageCatalog.Default.Message("provider-transport-read-timeout") &&
                    loaded.Message("provider-transport-write-timeout") ==
                    MessageCatalog.Default.Message("provider-transport-write-timeout"),
                "a direct catalog Load resolves the same three exact texts");
            Console.WriteLine(
                $"PASS message-catalog direct Load: iteration={iteration}; " +
                $"label={(iteration == 0 ? "cold-load-component" : "load-component-repetition")}; " +
                $"bytes={stream.Length}; allocated-bytes={allocated}; elapsed-ms={watch.Elapsed.TotalMilliseconds:F3}; " +
                "true-first-use=false; source=embedded EdgeMessages.json");
        }

        const int lookups = 1000;
        var cached = MessageCatalog.Default.Message("provider-transport-read-timeout");
        Require(ReferenceEquals(cached, MessageCatalog.Default.Message("provider-transport-read-timeout")),
            "a warm message lookup returns the cached instance, not a new string");
        var lookupBefore = GC.GetAllocatedBytesForCurrentThread();
        var lookupWatch = Stopwatch.StartNew();
        for (var index = 0; index < lookups; index++)
            Require(MessageCatalog.Default.Message("provider-transport-read-timeout") == cached,
                "a warm message lookup stays exact");
        var lookupAllocated = GC.GetAllocatedBytesForCurrentThread() - lookupBefore;
        lookupWatch.Stop();
        Console.WriteLine(
            $"PASS message-catalog warm lookup: label=warm-message-lookup; lookups={lookups}; " +
            $"allocated-bytes={lookupAllocated}; elapsed-ms={lookupWatch.Elapsed.TotalMilliseconds:F3}; true-first-use=false");

        var formatBefore = GC.GetAllocatedBytesForCurrentThread();
        var formatWatch = Stopwatch.StartNew();
        for (var index = 0; index < lookups; index++)
            Require(string.Format(CultureInfo.CurrentCulture, cached, 1234d) ==
                $"The provider made no read or write progress for {1234d:N0} seconds; the pending read was aborted.",
                "a warm format expression stays exact");
        var formatAllocated = GC.GetAllocatedBytesForCurrentThread() - formatBefore;
        formatWatch.Stop();
        Console.WriteLine(
            $"PASS message-catalog warm format: label=warm-format-expression; formats={lookups}; " +
            $"allocated-bytes={formatAllocated}; elapsed-ms={formatWatch.Elapsed.TotalMilliseconds:F3}; true-first-use=false");
    }

    private static async Task<Exception?> CaptureTaskFailure(Task task)
    {
        try { await task; return null; }
        catch (Exception error) { return error; }
    }

    private static async Task AssertFaultedCallAsync(Stream guard)
    {
        var failure = await CaptureTaskFailure(guard.ReadAsync(new byte[1].AsMemory(), CancellationToken.None).AsTask());
        Require(failure is IOException && failure.Message == MessageCatalog.Default.Message("provider-transport-faulted"),
            $"an already-faulted connection refuses a new call with the exact faulted text ({Describe(failure)})");
    }

    // A manual monotonic clock: GetTimestamp and the timers share one elapsed value, and a passive instance
    // never fires a timer so a completion can be observed already overdue.
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ClockTimer> timers = [];
        private readonly bool fireTimers;
        private readonly bool fireRetired;
        private TimeSpan elapsed;

        internal ManualClock(bool fireTimers = true, bool fireRetired = false)
        {
            this.fireTimers = fireTimers;
            this.fireRetired = fireRetired;
        }

        public override long GetTimestamp() => elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        internal void Advance(TimeSpan delta)
        {
            elapsed += delta;
            if (!fireTimers) return;
            foreach (var timer in timers.ToArray()) timer.FireIfDue();
        }
        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan due = Timeout.InfiniteTimeSpan;
            private TimeSpan period = Timeout.InfiniteTimeSpan;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan periodValue)
            {
                if (disposed) return false;
                due = dueTime == Timeout.InfiniteTimeSpan ? dueTime : clock.elapsed + dueTime;
                period = periodValue;
                return true;
            }
            internal void FireIfDue()
            {
                if (due == Timeout.InfiniteTimeSpan || due > clock.elapsed) return;
                // A queued callback may still run after disposal; the guard's operation identity, not the
                // timer's disposal, is what keeps a retired operation from touching its successor.
                if (disposed && !clock.fireRetired) return;
                due = period == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : clock.elapsed + period;
                callback(state);
            }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    // An inner transport stream the fixture controls without a sleep: a read waits for its release or the
    // guard's linked token, and a write may block, run a fixture handler, and then record its bytes.
    private sealed class ControlledBody : Stream
    {
        private readonly Queue<TaskCompletionSource<int>> reads = new();
        private readonly Queue<TaskCompletionSource> writes = new();
        internal Func<ReadOnlyMemory<byte>, ValueTask>? OnWrite;
        internal bool BlockWrites;
        internal List<byte> Written { get; } = [];
        internal bool Disposed { get; private set; }

        internal void ReleaseRead(int count)
        {
            if (reads.Count > 0) reads.Dequeue().TrySetResult(count);
        }

        internal void ReleaseWrite()
        {
            if (writes.Count > 0) writes.Dequeue().TrySetResult();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty) return 0;
            var read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            reads.Enqueue(read);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => cancelled.TrySetResult());
            await Task.WhenAny(read.Task, cancelled.Task).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var count = Math.Min(read.Task.Result, buffer.Length);
            buffer.Span[..count].Fill((byte)'r');
            return count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (BlockWrites)
            {
                var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                writes.Enqueue(write);
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => cancelled.TrySetResult());
                await Task.WhenAny(write.Task, cancelled.Task).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            if (OnWrite is not null) await OnWrite(buffer).ConfigureAwait(false);
            Written.AddRange(buffer.ToArray());
        }

        public override Task FlushAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
