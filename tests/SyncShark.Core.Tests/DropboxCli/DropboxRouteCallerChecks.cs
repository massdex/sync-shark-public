using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Verdict = Massdex.SyncShark.DropboxRetryDecision.Verdict;

namespace Massdex.SyncShark.DropboxCli;

/// <summary>The generic Dropbox route caller checks (dropbox-cli.md §5 groups 1-3).</summary>
internal static class DropboxRouteCallerChecks
{
    public static async Task RunAsync()
    {
        await RpcReadAsync();
        await NamespaceRootAsync();
        await UploadAsync();
        await DownloadAsync();
        await LongpollAsync();
        await RateLimitRetryAsync();
        await WriteServerFailureAsync();
        await ReadServerRetryAsync();
        await ReadTransportTimeoutRetriesAsync();
        await ZeroRetryReadAttemptsOnceAsync();
        await BackoffExceedsMaximumAsync();
        await ConflictJsonAsync();
        await BadRequestTextAsync();
        await WriteTransportFaultIsUncertainAsync();
        await WriteTimeoutIsUncertainAsync();
        await WriteMalformedBodyIsUncertainAsync();
        await ReadBodyInterruptionRetriesAsync();
        await DownloadBodyInterruptionResetsAsync();
        await DownloadThrottlesAsync();
        await ErrorBodyCancellationPropagatesAsync();
        RetryDecisionTable();
        ArgumentHeaderTable();
        Console.WriteLine("--dropbox-route-checks passed (caller).");
    }

    private static async Task RpcReadAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("users/get_space_usage", HttpStatusCode.OK, "{\"used\":1}");
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("users/get_space_usage"), "{\"a\":1}"), Limits(), default);
        var request = Single(harness);
        Check(request.Host == "api.dropboxapi.com", "rpc read uses the api host");
        Check(request.Route == "/2/users/get_space_usage", "rpc read uses the route path");
        Check(request.Authorization == "Bearer " + DropboxRouteWire.SecretToken, "rpc read sends the bearer token");
        Check(PathRoot(request) == ("root", "root-namespace"), "rpc read sends the root namespace path-root");
        Check(request.ContentType == "application/json", "rpc read sends JSON");
        Check(request.BodyText == "{\"a\":1}", "rpc read sends the body verbatim");
        Check(result.Result.RootElement.GetProperty("used").GetInt32() == 1, "rpc read parses the result");
    }

    private static async Task NamespaceRootAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("users/get_space_usage", HttpStatusCode.OK, "{}");
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("users/get_space_usage"), "{}", PathRootNamespaceId: "7"), Limits(), default);
        Check(PathRoot(Single(harness)) == ("namespace_id", "7"), "a namespace path-root replaces the account root");
    }

    private static async Task UploadAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("files/upload", HttpStatusCode.OK, "{\"name\":\"x\"}");
        var bytes = Encoding.UTF8.GetBytes("hello-bytes");
        using var content = new MemoryStream(bytes);
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/upload"), "{\"path\":\"/Ünï.txt\"}", ContentIn: content), Limits(), default);
        var request = Single(harness);
        Check(request.ContentType == "application/octet-stream", "upload sends application/octet-stream");
        Check(request.ApiArg is not null && request.ApiArg.All(character => character <= '~'),
            "the upload argument header is ASCII");
        Check(JsonDocument.Parse(request.ApiArg!).RootElement.GetProperty("path").GetString() == "/Ünï.txt",
            "the upload argument header round-trips");
        Check(request.Body.SequenceEqual(bytes), "upload sends the content bytes");
    }

    private static async Task DownloadAsync()
    {
        using var harness = new Harness();
        var payload = Encoding.UTF8.GetBytes("file-bytes");
        harness.Wire.Respond("files/download", HttpStatusCode.OK, bytes: payload, apiResult: "{\"name\":\"a.txt\"}");
        using var destination = new MemoryStream();
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/download"), "{\"path\":\"/a.txt\"}", ContentOut: destination),
            Limits(), default);
        Check(result.ContentBytes == payload.Length, "download reports the byte count");
        Check(destination.ToArray().SequenceEqual(payload), "download writes the content bytes");
        Check(result.Result.RootElement.GetProperty("name").GetString() == "a.txt",
            "download reads its result from the header");
    }

    private static async Task LongpollAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("files/list_folder/longpoll", HttpStatusCode.OK, "{\"changes\":false}");
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/list_folder/longpoll"), "{}"), Limits(), default);
        var request = Single(harness);
        Check(request.Host == "notify.dropboxapi.com", "longpoll uses the notify host");
        Check(request.Authorization is null, "longpoll sends no authorization");
        Check(request.PathRoot is null, "longpoll sends no path-root");
    }

    private static async Task RateLimitRetryAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("files/create_folder_v2", HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero);
        harness.Wire.Respond("files/create_folder_v2", HttpStatusCode.OK, "{}");
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/create_folder_v2"), "{}"), Limits(), default);
        Check(harness.Wire.Requests.Count == 2, "429 on a write is retried once");
    }

    private static async Task WriteServerFailureAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("files/create_folder_v2", HttpStatusCode.InternalServerError, "{}");
        await ExpectAsync<DropboxEffectUncertainException>(() => harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/create_folder_v2"), "{}"), Limits(), default));
        Check(harness.Wire.Requests.Count == 1, "500 on a write is uncertain and never retried");
    }

    private static async Task ReadServerRetryAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("files/get_metadata", HttpStatusCode.InternalServerError, "{}");
        harness.Wire.Respond("files/get_metadata", HttpStatusCode.InternalServerError, "{}");
        harness.Wire.Respond("files/get_metadata", HttpStatusCode.OK, "{}");
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/get_metadata"), "{}"), Limits(), default);
        Check(harness.Wire.Requests.Count == 3, "500 on a read is retried to success");
    }

    private static async Task ReadTransportTimeoutRetriesAsync()
    {
        using var harness = new Harness();
        var clock = new ResilienceChecks.Clock();
        var waits = 0;
        harness.Client.ReadRetryTime = clock;
        harness.Client.ReadRetryDelay = (delay, token) =>
        {
            token.ThrowIfCancellationRequested();
            waits++;
            clock.Advance(delay);
            return Task.CompletedTask;
        };
        harness.Wire.RespondFault("files/get_metadata", new OperationCanceledException(
            "scripted HttpClient timeout", new TimeoutException("scripted transport deadline")));
        harness.Wire.Respond("files/get_metadata", HttpStatusCode.OK, "{\"name\":\"recovered\"}");
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/get_metadata"), "{}"), Limits(), default);
        Check(harness.Wire.Requests.Count == 2 && waits == 1 &&
            result.Result.RootElement.GetProperty("name").GetString() == "recovered",
            "a read HttpClient timeout through real pacing retries successfully with a fake-clock backoff");

        // With retry disabled the original timeout shape must survive pacing unchanged. In particular,
        // a replacement TimeoutException can accidentally pass the read-retry check above.
        harness.Wire.RespondFault("files/get_metadata", new OperationCanceledException(
            "scripted HttpClient timeout", new TimeoutException("scripted transport deadline")));
        var error = await ExpectAsync<OperationCanceledException>(() => harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/get_metadata"), "{}"), Limits(transport: 0), default));
        Check(error.InnerException is TimeoutException && harness.Wire.Requests.Count == 3 && waits == 1,
            "pacing preserves HttpClient cancellation with its inner timeout, without replay when disabled");
    }

    // A caller that asks for zero retries gets exactly one attempt, even though reads are otherwise
    // budget-bound: an explicit zero count disables retries for that class.
    internal static async Task ZeroRetryReadAttemptsOnceAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("files/get_metadata", HttpStatusCode.ServiceUnavailable, "{}");
        harness.Wire.Respond("files/get_metadata", HttpStatusCode.OK, "{}");
        var error = await ExpectAsync<DropboxRouteException>(() => harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/get_metadata"), "{}"),
            new DropboxRetryLimits(0, 0, 0, TimeSpan.FromMinutes(5)), default));
        Check(harness.Wire.Requests.Count == 1, "a zero-retry read sends exactly one attempt");
        Check(error.StatusCode == HttpStatusCode.ServiceUnavailable, "the refusal status is surfaced");
    }

    // Fix 1: a write whose response never arrives (a connection drop or an HttpClient timeout) is an
    // uncertain effect, never a plain failure, and is never replayed.
    private static async Task WriteTransportFaultIsUncertainAsync()
    {
        using var harness = new Harness();
        harness.Wire.RespondFault("files/create_folder_v2", new HttpRequestException("connection dropped"));
        await ExpectAsync<DropboxEffectUncertainException>(() => harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/create_folder_v2"), "{}"), Limits(), default));
        Check(harness.Wire.Requests.Count == 1, "a dropped write response is uncertain and never replayed");
    }

    private static async Task WriteTimeoutIsUncertainAsync()
    {
        using var harness = new Harness();
        harness.Wire.RespondFault("files/create_folder_v2", new OperationCanceledException(
            "request timed out", new TimeoutException("scripted HttpClient transport deadline")));
        await ExpectAsync<DropboxEffectUncertainException>(() => harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/create_folder_v2"), "{}"), Limits(), default));
        Check(harness.Wire.Requests.Count == 1, "a write HttpClient timeout is uncertain and never replayed");
    }

    // Fix 2: a write success body that cannot be read is an uncertain effect, not a raw parse failure.
    private static async Task WriteMalformedBodyIsUncertainAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("files/create_folder_v2", HttpStatusCode.OK, "not json");
        await ExpectAsync<DropboxEffectUncertainException>(() => harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/create_folder_v2"), "{}"), Limits(), default));
        Check(harness.Wire.Requests.Count == 1, "a malformed write success body is uncertain, not replayed");
        Check(harness.Wire.Responses.All(response => response.Disposed), "an uncertain write releases its response");
    }

    // Fix 4: a read whose response body is interrupted retries within its read-transport budget.
    private static async Task ReadBodyInterruptionRetriesAsync()
    {
        using var harness = new Harness();
        harness.Wire.RespondBodyFault("files/get_metadata", HttpStatusCode.OK, new IOException("body interrupted"));
        harness.Wire.Respond("files/get_metadata", HttpStatusCode.OK, "{\"name\":\"a\"}");
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/get_metadata"), "{}"), Limits(), default);
        Check(harness.Wire.Requests.Count == 2, "an interrupted read body is retried within the read budget");
        Check(harness.Wire.Responses.All(response => response.Disposed), "every read attempt releases its response");
    }

    private static async Task DownloadBodyInterruptionResetsAsync()
    {
        using var harness = new Harness();
        var payload = Encoding.UTF8.GetBytes("complete-download-payload");
        harness.Wire.RespondBodyFault("files/download", HttpStatusCode.OK, new IOException("body interrupted"),
            payload[..5]);
        harness.Wire.Respond("files/download", HttpStatusCode.OK, bytes: payload, apiResult: "{\"name\":\"a.txt\"}");
        using var destination = new MemoryStream();
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/download"), "{\"path\":\"/a.txt\"}", ContentOut: destination),
            Limits(), default);
        Check(harness.Wire.Requests.Count == 2, "an interrupted download is retried");
        Check(destination.ToArray().SequenceEqual(payload), "the retry truncates the partial download destination");
        Check(result.ContentBytes == payload.Length, "the retried download reports the full byte count");
    }

    // Fix 3: downloads stream through the shared transfer throttle before each write.
    private static async Task DownloadThrottlesAsync()
    {
        using var harness = new Harness(new SyncTransferSettings(DownloadKiBPerSecond: 4));
        var payload = new byte[1024];
        harness.Wire.Respond("files/download", HttpStatusCode.OK, bytes: payload, apiResult: "{\"name\":\"a.txt\"}");
        using var destination = new MemoryStream();
        var watch = Stopwatch.StartNew();
        using var result = await harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/download"), "{\"path\":\"/a.txt\"}", ContentOut: destination),
            Limits(), default);
        watch.Stop();
        Check(watch.Elapsed >= TimeSpan.FromMilliseconds(120),
            "a throttled download waits for its reserved bytes (>= 120 ms for 1 KiB at 4 KiB/s)");
        Check(result.ContentBytes == payload.Length, "the throttled download still receives every byte");
    }

    // Fix 5: reading an error body never swallows a cancellation; it propagates.
    private static async Task ErrorBodyCancellationPropagatesAsync()
    {
        using var harness = new Harness();
        harness.Wire.RespondBodyFault("files/get_metadata", HttpStatusCode.BadRequest,
            new OperationCanceledException());
        var error = await ExpectAsync<OperationCanceledException>(() => harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/get_metadata"), "{}"), Limits(), default));
        Check(error.GetType() == typeof(OperationCanceledException),
            "an error-body cancellation propagates unchanged instead of becoming a refusal");
    }
    private static async Task BackoffExceedsMaximumAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("files/get_metadata", HttpStatusCode.TooManyRequests,
            retryAfter: TimeSpan.FromMinutes(10));
        var error = await ExpectAsync<DropboxRouteException>(() => harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/get_metadata"), "{}"),
            Limits(maxBackoff: TimeSpan.FromMinutes(5)), default));
        Check(error.StatusCode == HttpStatusCode.TooManyRequests, "a backoff beyond the maximum fails as rate-limited");
        Check(harness.Wire.Requests.Count == 1, "an oversized backoff is not retried");
    }

    private static async Task ConflictJsonAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("files/delete_v2", HttpStatusCode.Conflict,
            "{\"error_summary\":\"path/not_found/..\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}");
        var error = await ExpectAsync<DropboxRouteException>(() => harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/delete_v2"), "{}"), Limits(), default));
        Check(error.StatusCode == HttpStatusCode.Conflict, "409 keeps its status");
        Check(error.ErrorSummary == "path/not_found/..", "409 exposes the error summary");
        Check(error.ErrorJson is not null && error.ErrorJson.Contains("not_found", StringComparison.Ordinal),
            "409 exposes the structured error");
    }

    private static async Task BadRequestTextAsync()
    {
        using var harness = new Harness();
        harness.Wire.Respond("files/delete_v2", HttpStatusCode.BadRequest, "not json");
        var error = await ExpectAsync<DropboxRouteException>(() => harness.Client.CallRouteAsync(
            new DropboxRouteCall(Route("files/delete_v2"), "{}"), Limits(), default));
        Check(error.ErrorSummary == "not json", "a plain-text error body becomes the summary");
        Check(error.ErrorJson is null, "a plain-text error body has no structured error");
    }

    private static void RetryDecisionTable()
    {
        var limits = new DropboxRetryLimits(5, 2, 2, TimeSpan.FromMinutes(5));
        Check(Decide(DropboxRoutePolicy.Read, null, true, 0, limits) == Verdict.Retry,
            "a read transport failure retries");
        Check(Decide(DropboxRoutePolicy.Read, null, true, 2, limits) == Verdict.Fail,
            "a read transport failure stops after its budget");
        Check(Decide(DropboxRoutePolicy.Write, null, true, 0, limits) == Verdict.Uncertain,
            "a write transport failure is uncertain");
        Check(Decide(DropboxRoutePolicy.Read, HttpStatusCode.TooManyRequests, false, 4, limits) == Verdict.Retry,
            "a read 429 retries within its budget");
        Check(Decide(DropboxRoutePolicy.Read, HttpStatusCode.TooManyRequests, false, 5, limits) == Verdict.Fail,
            "a read 429 stops after its budget");
        Check(Decide(DropboxRoutePolicy.Write, HttpStatusCode.TooManyRequests, false, 0, limits) == Verdict.Retry,
            "a write 429 retries");
        Check(Decide(DropboxRoutePolicy.Read, HttpStatusCode.ServiceUnavailable, false, 1, limits) == Verdict.Retry,
            "a read 5xx retries");
        Check(Decide(DropboxRoutePolicy.Read, HttpStatusCode.ServiceUnavailable, false, 2, limits) == Verdict.Fail,
            "a read 5xx stops after its budget");
        Check(Decide(DropboxRoutePolicy.Write, HttpStatusCode.ServiceUnavailable, false, 0, limits) == Verdict.Uncertain,
            "a write 5xx is uncertain");
        Check(Decide(DropboxRoutePolicy.Read, HttpStatusCode.RequestTimeout, false, 1, limits) == Verdict.Retry,
            "a read 408 retries");
        Check(Decide(DropboxRoutePolicy.Read, HttpStatusCode.BadRequest, false, 0, limits) == Verdict.Fail,
            "a read refusal fails without retry");
        Check(Decide(DropboxRoutePolicy.Write, HttpStatusCode.BadRequest, false, 0, limits) == Verdict.Fail,
            "a write refusal fails without retry");
    }

    private static void ArgumentHeaderTable()
    {
        const string emoji = "\U0001F600";
        var nonAscii = DropboxArgHeader.Encode("{\"p\":\"Ünï\"}");
        Check(nonAscii.All(character => character <= '~'), "a non-ASCII argument encodes to ASCII");
        Check(JsonDocument.Parse(nonAscii).RootElement.GetProperty("p").GetString() == "Ünï",
            "a non-ASCII argument round-trips");
        var surrogate = DropboxArgHeader.Encode("{\"p\":\"" + emoji + "\"}");
        Check(surrogate.All(character => character <= '~'), "a surrogate pair encodes to ASCII");
        Check(JsonDocument.Parse(surrogate).RootElement.GetProperty("p").GetString() == emoji,
            "a surrogate pair round-trips");
        var escaped = DropboxArgHeader.Encode("{\"p\":\"\\\\u00e9\"}");
        Check(JsonDocument.Parse(escaped).RootElement.GetProperty("p").GetString() == "\\u00e9",
            "an already-escaped argument round-trips");
        Check(Throws<JsonException>(() => DropboxArgHeader.Encode("not json")), "invalid argument JSON is refused");
    }

    private static DropboxRetryDecision.Verdict Decide(DropboxRoutePolicy policy, HttpStatusCode? status,
        bool transportFailure, int attempt, DropboxRetryLimits limits) =>
        DropboxRetryDecision.Decide(policy, status, transportFailure, attempt, limits);

    private static DropboxRetryLimits Limits(int rateLimit = 5, int transport = 2, int server = 2,
        TimeSpan? maxBackoff = null) =>
        new(rateLimit, transport, server, maxBackoff ?? TimeSpan.FromMinutes(5));

    private static DropboxRoute Route(string name) =>
        DropboxRouteTable.Embedded.Find(name) ?? throw new InvalidOperationException($"The table has no {name}.");

    private static DropboxRouteWire.Request Single(Harness harness)
    {
        Check(harness.Wire.Requests.Count == 1, "the route sent exactly one request");
        return harness.Wire.Requests[0];
    }

    private static (string Tag, string? Value) PathRoot(DropboxRouteWire.Request request)
    {
        var root = JsonDocument.Parse(request.PathRoot!).RootElement;
        var tag = root.GetProperty(".tag").GetString()!;
        return (tag, root.TryGetProperty(tag, out var value) ? value.GetString() : null);
    }

    private static async Task<T> ExpectAsync<T>(Func<Task<DropboxRouteResult>> action) where T : Exception
    {
        try { using var result = await action(); }
        catch (T expected) { return expected; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Dropbox route caller check failed: " + description);
        Console.WriteLine("PASS " + description);
    }

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _http;

        internal Harness(SyncTransferSettings? transfer = null)
        {
            Wire = new DropboxRouteWire();
            _http = new HttpClient(Wire);
            Client = new DropboxPilotClient(_http, new SecretDropboxTokenSource(), "/", Binding(),
                PilotPath.DropboxNamespaceRootIdentity("root-namespace"),
                transfer is null ? null : new SyncTransferRateLimiter(transfer));
        }

        internal DropboxRouteWire Wire { get; }
        internal DropboxPilotClient Client { get; }

        public void Dispose()
        {
            _http.Dispose();
            Wire.Dispose();
        }

        private static DropboxConnectionBinding Binding() => new(
            DropboxProviderConfiguration.RegisteredClientId, "account-id", "root-namespace", "home-namespace",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes);
    }
}
