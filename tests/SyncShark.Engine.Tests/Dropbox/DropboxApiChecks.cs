using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

// Over the 300-line review prompt, not split: the row owns this one registered file, and its transport
// doubles exist only for the checks here.
/// <summary>Checks for DropboxApi (`--engine-checks dropbox`), written by DropboxApi's writer.</summary>
public static class DropboxApiChecks
{
    private const string NamespaceId = "namespace-fixture";
    private const string Token = "token-fixture";
    private const string ArgHeader = "Dropbox-API-Arg";
    private const string ArgumentPath = "/Parent/é-文件.txt";
    private const string ResponseJson = "{\"metadata\":{\"id\":\"item-fixture\"}}";
    private const string ErrorJson = "{\"error_summary\":\"path/not_found/fixture\"}";
    private const string WriteThrottleJson = "{\"error\":{\"reason\":{\".tag\":\"too_many_write_operations\"}}}";
    private const string ErrorSummary = "path/not_found/fixture";
    private const string Payload = "prefix:payload";
    private const string PayloadTail = "payload";
    private const int StartLevel = 1;
    private const int StartPosition = 7;
    private const int UploadStartLevel = 1;
    private const string ApiHost = "api.dropboxapi.com";
    private const string ContentHost = "content.dropboxapi.com";
    private const string NotifyHost = "notify.dropboxapi.com";
    private const string EndpointPrefix = "https://";
    private const string VersionPath = "/2/";
    private const string StatusLine = "HTTP/1.1 200 OK\r\n";
    private const string ProgressHeader = "X-Fixture: progress\r\n";
    private const string ReplyTail = "Content-Length: 2\r\n\r\nok";
    // READING 8.4 adopts this write-slice bound.
    private const int WriteSliceBytes = 64 * 1024;
    // These fixture values preserve the existing deadline/boundary matrices, not runtime policy defaults.
    private const int PoolReadSeconds = 60;
    private const int ScavengeSeconds = 8;
    private const int OneUnit = 1;
    private const int IdleMinutes = 30;
    private const int RetryMinutes = 3;
    private const int HeaderWaitSeconds = 5;
    private const int ProviderWaitSeconds = 30;
    private const int ReplacementConnections = 2;
    private const int OrdinarySeconds = 120;
    private const int ProgressSeconds = 50;
    private const int LastProgressSeconds = 40;
    private const int InsideOrdinarySeconds = 119;
    private const int LongpollSeconds = 570;
    private const int InsideLongpollSeconds = 569;
    private const int OutsideLongpollSeconds = 571;
    private const int UploadChunks = 3;
    private const int UploadMinutes = 10;
    private const int UploadSlices = 9;
    private const int MidSliceSeconds = 100;
    private const int EarlyResponseChunks = 4;
    private const int BodyPairBytes = 2;
    private const int HeaderTerminatorBytes = 4;
    private const int RsaBits = 2048;
    private const int TlsReadSeconds = 150;
    private const int ExpiryStepSeconds = 20;
    private const int LongpollRemainderSeconds = 449;
    private const int ShortLongpollAllowanceSeconds = 210;
    private const int RepeatedFailureCount = 3;
    private const int LexicalDepth = 32;
    private const int LexicalDepthLimit = 64;
    private const int InvalidUtf8Lead = 0xC3;
    private const int InvalidUtf8Tail = 0x28;
    private const int OverlongUtf8Lead = 0xC0;
    private const int Utf8Continuation = 0x80;
    private const int SurrogateUtf8Lead = 0xED;
    private const int SurrogateUtf8Tail = 0xA0;
    private const int OutOfRangeUtf8Lead = 0xF4;
    private const int OutOfRangeUtf8Tail = 0x90;
    private const int LongpollHeadroomSeconds = 90;
    private const int RetiredSuccessCapBytes = 16 * 1024 * 1024;
    private const int HugeNumberDigits = 1024;
    private const int SplitChunkBytes = 7;
    private const int LargeChunkBytes = 4096;
    // Below the write allowance, so a read can expire while its overlapping write survives.
    private static readonly TimeSpan PoolReadAllowance = TimeSpan.FromSeconds(PoolReadSeconds);
    // The handler scavenges four times per idle timeout, so its real tick stays inside a fast check.
    private static readonly TimeSpan ScavengeIdleTimeout = TimeSpan.FromSeconds(ScavengeSeconds);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(OneUnit);
    private static readonly TimeSpan IdleGap = TimeSpan.FromMinutes(IdleMinutes);
    // Reports a fixture that stopped progressing instead of hanging the suite; never elapses on a pass.
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(PoolReadSeconds);

    private static readonly (string Name, string Valid, string Invalid)[] MetadataFields =
    [
        (DropboxProtocol.Tag, "\"file\"", "0"),
        (DropboxProtocol.Id, "\"id:file\"", "0"),
        (DropboxProtocol.PathDisplay, "\"/Parent/Child\"", "0"),
        (DropboxProtocol.Size, "7", "\"seven\""),
        (DropboxProtocol.ClientModified, "\"2026-01-01T00:00:00Z\"", "\"not-a-time\""),
        (DropboxProtocol.Rev, "\"123456789\"", "0"),
        (DropboxProtocol.ContentHash, "\"hash-fixture\"", "0"),
        (DropboxProtocol.IsDownloadable, "true", "\"no\"")
    ];

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RequestObservationIsolationAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var api = Create(http, new Tokens());
        var left = new ConcurrentQueue<RunEvent>();
        var right = new ConcurrentQueue<RunEvent>();
        async Task Run(string subject, ConcurrentQueue<RunEvent> events)
        {
            using var scope = DropboxApi.ObserveRequests(events.Enqueue);
            await api.RpcAsync(DropboxProtocol.GetMetadata, new { path = "private-request-value" },
                DropboxReply.Void(), new RequestAttempt(new object(), AttemptOwner.Request), default, subject);
        }
        var first = Run("Pair A/Child", left);
        var second = Run("Pair B/Child", right);
        try
        {
            Check.Require(left.Count == 1 && right.Count == 1 && !first.IsCompleted && !second.IsCompleted,
                "both concurrent request starts are visible before a provider reply");
        }
        finally { release.SetResult(); }
        await Task.WhenAll(first, second).WaitAsync(Watchdog);
        foreach (var (events, subject) in new[] { (left, "Pair A/Child"), (right, "Pair B/Child") })
        {
            var facts = events.ToArray();
            Check.Require(facts.Length == 2 && facts.All(item => item.Path == subject &&
                item.What == DropboxProtocol.GetMetadata && item.Detail == string.Empty) &&
                !facts[0].Ended && facts[1].Ended && facts[0].RequestId == facts[1].RequestId &&
                facts[1].RequestResult == RequestResult.Success,
                "a shared API attributes complete request lifetimes only to their own async run, without bodies");
        }
        await api.RpcAsync(DropboxProtocol.GetMetadata, new { }, DropboxReply.Void(),
            new RequestAttempt(new object(), AttemptOwner.Request), default);
        Check.Require(left.Count == 2 && right.Count == 2, "disposed scopes cannot capture later unrelated work");
        var download = new ConcurrentQueue<RunEvent>();
        Stream content;
        using (DropboxApi.ObserveRequests(download.Enqueue))
            content = await api.DownloadAsync(DropboxProtocol.Download, new { path = "private-request-value" },
                new RequestAttempt(new object(), AttemptOwner.Transfer), default, subject: "Parent/Child");
        Check.Require(download.Count == 1 && !download.Single().Ended,
            "download headers keep the request active for quiet reporting throughout the response lifetime");
        await content.CopyToAsync(Stream.Null);
        Check.Require(download.Count == 1, "EOF does not pretend that response cleanup or staging finished");
        await content.DisposeAsync();
        content.Dispose();
        Check.Require(download.Count == 2 && download.Last().Ended &&
            download.Last().RequestResult == RequestResult.Success && download.Last().Path == "Parent/Child",
            "download release publishes one terminal fact to its captured run even after the scope ends");
    }

    // Bug #95: a failed provider line carries the mapped cause, so a documented absence or a real refusal can be
    // told apart from the status word alone.
    private static async Task RequestFailureCarriesItsCauseAsync()
    {
        foreach (var (body, expected) in new (string Body, string Cause)[]
        {
            (ErrorJson, ErrorSummary),
            // The structured union is authoritative even with no summary.
            ("""{"error_summary":"","error":{".tag":"path","path":{".tag":"not_found"}}}""", "path/not_found"),
            // Unsafe or empty summary text never reaches the log.
            ("""{"error_summary":"Bad Summary: secret"}""", Text.Get(TextKey.ProviderReasonUnavailable))
        })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.Conflict) { Content = new StringContent(body) })));
            var api = Create(http, new Tokens());
            var events = new ConcurrentQueue<RunEvent>();
            using (DropboxApi.ObserveRequests(events.Enqueue))
            {
                try
                {
                    await api.RpcAsync(DropboxProtocol.GetMetadata, new { path = "private-request-value" },
                        DropboxReply.Void(), new RequestAttempt(new object(), AttemptOwner.Request), default, "Item");
                }
                catch (DropboxApiException) { }
            }
            var ended = events.Single(item => item.Ended);
            Check.Require(ended.RequestResult == RequestResult.PermanentFailure && ended.Detail == expected,
                "a failed provider line carries the safe mapped cause; detail=" + ended.Detail);
        }
    }

    private static async Task RunAsync()
    {
        await ValidatedReadGrowthAsync();
        await DownloadEofDoesNotGrowAsync();
        var routes = new[]
        {
            DropboxProtocol.ListFolder, DropboxProtocol.ListFolderContinue, DropboxProtocol.GetMetadata,
            DropboxProtocol.Download, DropboxProtocol.Upload, DropboxProtocol.UploadSessionStart,
            DropboxProtocol.UploadSessionAppend, DropboxProtocol.UploadSessionFinish, DropboxProtocol.CreateFolder,
            DropboxProtocol.Delete, DropboxProtocol.Move
        }.Select(name => DropboxRouteTable.Embedded.Find(name)!)
            .Concat(DropboxRouteTable.Embedded.Routes.Where(route => route.IsNoAuth));
        foreach (var route in routes)
        {
            await RequestAsync(route);
        }
        foreach (var route in new[] { DropboxProtocol.GetMetadata, DropboxProtocol.Download, DropboxProtocol.Upload })
        {
            await ErrorAsync(route);
        }
        await RequestObservationIsolationAsync();
        await RequestFailureCarriesItsCauseAsync();
        await DirectUploadRetryAsync();
        await RetryUploadAsync();
        await WriteThrottleRetryAsync();
        await ThrottleBodyAsync();
        await RefreshTokenAsync();
        await RejectNonSeekableAsync();
        await FailedUploadAsync();
        await CompleteRepliesAsync();
        await AccountRootAsync();
        await SealedPagesAsync();
        await UnpublishedPageAsync();
        await UnknownValuesAsync();
        GuardedPools();
        await MutationReplyFailureAsync();
        await TypedScalarsAsync();
        await PoolReuseAsync();
        await ErrorBoundAsync();
        await RepeatedFailuresAsync();
        await RequestRetryAsync();
        await DecoderMatrixAsync();
        await PageWaitLifecycleAsync();
        await HeaderActivityAsync();
        await PoolMatrixAsync();
        await ProgressingUploadAsync();
        await OverdueSliceAsync();
        await EarlyResponseAsync();
        await TransportContractsAsync();
        await RedirectAndProxyAsync();
        await TlsActivityAsync();
        await ClockMatchesOnlyFreshAllowancesAsync();
        await ConcurrentPoolsAsync();
        await LongpollRebindAsync();
        await FinishBatchReplyMatrixAsync();
        await FinishExcessMemberSkipsHugeSelectedScalars();
        await FolderExcessMemberSkipsHugeSelectedScalars();
        await PageHasNoRetentionCountSentinel();
        await UnknownScopeWaitGatesWholeAccount();
        await HeaderBodyTakesLonger();
        ObservationNeverShorter();
        await LongpollAnchorSurvivesTransportCleanup();
        await BodyOnlyRateWaitSurvivesAbortFailure();
        await BodyOnlyRateWaitSurvivesOriginalErrorContentDisposeFailure();
        await EarlyRefusalJoinsBeforeOriginalErrorBodyDispose();
        await EarlyRefusalJoinsBeforeResponseDispose();
        await MalformedReplyAndThrowingResponseDisposePreserveBothErrors();
        await StopCallbackFailureStillJoinsAndCompletesAttempt();
        await HttpThrottleNotificationFailureStopsAllFiveOwners();
        await SealedPageTransportCleanupFailureDisposesSpoolAndFailsOnce();
        await ErrorReadAndDisposeFailuresPreserveBoth();
        await CancelledPageBeginAndSpoolDisposeFailurePreserveBoth();
        await PartiallyStartedPageBeginDisposesSpoolOnce();
        await PartiallyStartedPageBeginThenPreBeginRetryFailureDoesNotRedisposeSpool();
        await HashedUploadTransientTokenFailureBeforeSend();
        await HashedUploadDispatchedFailureNeverResends();
    }

    private static async Task RequestAsync(DropboxRoute route)
    {
        var tokens = new Tokens();
        var argument = new { path = route.Name == DropboxProtocol.ListFolder ? "" : ArgumentPath, recursive = true };
        var json = JsonSerializer.Serialize(argument);
        var emptyUpload = route.Name == DropboxProtocol.UploadSessionFinish;
        using var upload = new ObservedStream(emptyUpload ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(Payload));
        if (!emptyUpload) upload.Position = StartPosition;
        using var content = new ObservedStream(Encoding.UTF8.GetBytes(ResponseJson));
        using var handler = new Handler(async (request, ct) =>
        {
            var host = route.Host switch
            {
                DropboxRouteHost.Api => ApiHost,
                DropboxRouteHost.Content => ContentHost,
                DropboxRouteHost.Notify => NotifyHost,
                _ => throw new InvalidOperationException(nameof(route.Host))
            };
            Check.Require(request.Method == HttpMethod.Post, nameof(HttpMethod.Post));
            Check.Require(request.RequestUri?.AbsoluteUri == EndpointPrefix + host + VersionPath + route.Name,
                nameof(route.Endpoint));
            Check.Require(request.RequestUri?.Host == host, nameof(route.Host));
            Check.Require(request.Headers.Authorization?.Parameter == (route.IsNoAuth ? null : Token), nameof(route.Auth));
            if (route.IsNoAuth)
            {
                Check.Require(request.Headers.Authorization is null, nameof(route.IsNoAuth));
                Check.Require(!request.Headers.Contains(DropboxProtocol.PathRootHeader), nameof(DropboxProtocol.PathRootHeader));
                Check.Require(tokens.Calls == 0, nameof(tokens.Calls));
            }
            else
            {
                Check.Require(request.Headers.Authorization?.Scheme == DropboxProtocol.BearerScheme, nameof(DropboxProtocol.BearerScheme));
                using var root = JsonDocument.Parse(request.Headers.GetValues(DropboxProtocol.PathRootHeader).Single());
                Check.Require(request.Headers.GetValues(DropboxProtocol.PathRootHeader).Single() ==
                    "{\".tag\":\"root\",\"root\":\"" + NamespaceId + "\"}",
                    "The path-root header serialises the tagged union with .tag first.");
                Check.Require(root.RootElement.GetProperty(DropboxProtocol.Tag).GetString() == DropboxProtocol.Root,
                    nameof(DropboxProtocol.Tag));
                Check.Require(root.RootElement.GetProperty(DropboxProtocol.Root).GetString() == NamespaceId, nameof(NamespaceId));
                Check.Require(tokens.Calls == 1, nameof(tokens.Calls));
            }
            if (route.Style == DropboxRouteStyle.Rpc)
            {
                Check.Require(!request.Headers.Contains(ArgHeader), nameof(DropboxRouteStyle.Rpc));
                Check.Require(await request.Content!.ReadAsStringAsync(ct) == json, nameof(argument));
                Check.Require(request.Content.Headers.ContentType?.MediaType == System.Net.Mime.MediaTypeNames.Application.Json,
                    nameof(request.Content.Headers.ContentType));
            }
            else
            {
                var header = request.Headers.GetValues(ArgHeader).Single();
                Check.Require(header == DropboxArgHeader.Encode(json) && header.All(char.IsAscii), nameof(DropboxArgHeader));
                using var decoded = JsonDocument.Parse(header);
                Check.Require(decoded.RootElement.GetProperty(nameof(argument.path)).GetString() == ArgumentPath, nameof(ArgumentPath));
                if (route.Style == DropboxRouteStyle.Upload)
                {
                    Check.Require(await request.Content!.ReadAsStringAsync(ct) == (emptyUpload ? "" : PayloadTail), nameof(PayloadTail));
                    Check.Require(request.Content.Headers.ContentType?.MediaType == DropboxProtocol.OctetStream,
                        nameof(DropboxProtocol.OctetStream));
                }
                else
                {
                    Check.Require(request.Content is null, nameof(DropboxRouteStyle.Download));
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content) };
        });
        using var http = new HttpClient(handler);
        var api = Create(http, tokens);
        if (route.Style == DropboxRouteStyle.Download)
        {
            using (var downloaded = await api.DownloadAsync(route.Name, argument,
                new RequestAttempt(new object(), AttemptOwner.Transfer), CancellationToken.None))
            {
                Check.Require(downloaded is ResponseStream && !content.Disposed, nameof(ResponseStream));
                Check.Require(content.Position == 0, nameof(HttpCompletionOption.ResponseHeadersRead));
                using var reader = new StreamReader(downloaded, leaveOpen: true);
                Check.Require(await reader.ReadToEndAsync() == ResponseJson, nameof(ResponseJson));
            }
        }
        else
        {
            var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
            _ = route.Style == DropboxRouteStyle.Upload
                ? await api.UploadAsync(route.Name, argument, upload, DropboxReply.Void(), attempt, CancellationToken.None)
                : await api.RpcAsync(route.Name, argument, DropboxReply.Void(), attempt, CancellationToken.None);
            Check.Require(attempt.Completion?.Result == RequestResult.Success,
                "A typed reply completes only after the response is validated.");
        }
        Check.Require(content.Disposed, nameof(content.Disposed));
        Check.Require(!upload.Disposed && upload.CanRead, nameof(upload.Disposed));
        Check.Require(handler.Requests.Count == 1, nameof(handler.Requests));
    }

    private static async Task ErrorAsync(string name)
    {
        using var content = new ObservedStream(Encoding.UTF8.GetBytes(ErrorJson));
        using var body = new ObservedStream(Encoding.UTF8.GetBytes(Payload));
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StreamContent(content)
        }));
        using var http = new HttpClient(handler);
        var api = Create(http, new Tokens());
        var rejected = false;
        try
        {
            if (name == DropboxProtocol.Download)
            {
                using var stream = await api.DownloadAsync(name, new { path = ArgumentPath },
                    new RequestAttempt(new object(), AttemptOwner.Transfer), CancellationToken.None);
            }
            else
            {
                var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
                _ = name == DropboxProtocol.Upload
                    ? await api.UploadAsync(name, new { path = ArgumentPath }, body,
                        DropboxReply.Void(), attempt, CancellationToken.None)
                    : await api.RpcAsync(name, new { path = ArgumentPath },
                        DropboxReply.Void(), attempt, CancellationToken.None);
            }
        }
        catch (DropboxApiException error)
        {
            rejected = error.StatusCode == HttpStatusCode.Conflict && error.SafeReason == ErrorSummary;
        }
        Check.Require(rejected, nameof(DropboxApiException));
        Check.Require(content.Disposed, nameof(content.Disposed));
        Check.Require(!body.Disposed, nameof(body.Disposed));
    }

    private static async Task RetryUploadAsync()
    {
        foreach (var route in new[] { DropboxProtocol.Upload, DropboxProtocol.UploadSessionStart,
            DropboxProtocol.UploadSessionAppend, DropboxProtocol.UploadSessionFinish })
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        foreach (var owner in Enum.GetValues<AttemptOwner>())
        {
            if (route == DropboxProtocol.Upload && status == HttpStatusCode.TooManyRequests) continue;
            using var body = new ObservedStream(Encoding.UTF8.GetBytes(Payload));
            body.Position = StartPosition;
            using var handler = new Handler(async (request, ct) =>
            {
                Check.Require(await request.Content!.ReadAsStringAsync(ct) == PayloadTail,
                    "The mutation sends the caller's original stream window.");
                var response = new HttpResponseMessage(status) { Content = new StringContent(ErrorJson) };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            });
            using var http = new HttpClient(handler);
            var attempt = new RequestAttempt(new object(), owner);
            try
            {
                await Create(http, new Tokens()).UploadAsync(route, new { path = ArgumentPath }, body,
                    DropboxReply.Void(), attempt, default);
                Check.Require(false, "An unproved mutation refusal cannot report success.");
            }
            catch (DropboxApiException)
            {
                Check.Require(attempt.Completion?.Result == RequestResult.UnknownWrite &&
                    attempt.Completion?.NoEffectProven == false,
                    "Retry advice, including zero, is not proof that a mutation had no effect.");
            }
            Check.Require(handler.Requests.Count == 1 && !body.Disposed,
                "No owner may blindly replay a possible effect or dispose caller input.");
        }
    }

    private static async Task WriteThrottleRetryAsync()
    {
        foreach (var upload in new[] { false, true })
        foreach (var owner in new[] { AttemptOwner.Request, AttemptOwner.Recovery })
        {
            var calls = 0;
            using var handler = new Handler(async (request, ct) =>
            {
                await request.Content!.ReadAsStringAsync(ct);
                var failed = ++calls == 1;
                var response = new HttpResponseMessage(failed ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
                {
                    Content = new StringContent(failed ? WriteThrottleJson : "{}")
                };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            });
            using var http = new HttpClient(handler);
            using var body = new ObservedStream(Encoding.UTF8.GetBytes(Payload));
            var api = Create(http, new Tokens());
            var attempt = new RequestAttempt(new object(), owner);
            var pending = upload
                ? api.UploadAsync(DropboxProtocol.Upload, new { path = ArgumentPath }, body,
                    DropboxReply.Void(), attempt, default)
                : api.RpcAsync(DropboxProtocol.CreateFolder, new { path = ArgumentPath },
                    DropboxReply.Void(), attempt, default);
            if (owner == AttemptOwner.Request)
            {
                await pending;
                Check.Require(calls == ReplacementConnections && attempt.Completion?.Result == RequestResult.Success,
                    "A Request-owned RPC write or direct upload retries a received 429 exactly once before success.");
            }
            else
            {
                Check.Require(await FailureOf(pending) is DropboxApiException && calls == 1 &&
                    attempt.Completion?.Result == RequestResult.TransientFailure &&
                    attempt.Completion?.NoEffectProven == true && attempt.Completion?.ProviderWait == TimeSpan.Zero,
                    "Recovery receives the refusal's no-effect proof and zero wait without a Request-owned replay.");
            }
            Check.Require(!body.Disposed, "The caller retains upload content after a refusal.");
        }
    }

    private static async Task ThrottleBodyAsync()
    {
        foreach (var (body, throttle) in new[]
        {
            ("{", false), ("[]", false), ("null", false), (WriteThrottleJson, true), (ErrorJson, false)
        })
        {
            var content = new ObservedContent(Encoding.UTF8.GetBytes(body));
            using var handler = new Handler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = content }));
            using var http = new HttpClient(handler);
            var notifications = new List<WaitEvent>();
            var attempt = new RequestAttempt(new object(), AttemptOwner.Recovery);
            var error = await FailureOf(Create(http, new Tokens(), waited: notifications.Add).RpcAsync(
                DropboxProtocol.GetMetadata, new { }, DropboxReply.Void(), attempt, default));
            Check.Require(error is DropboxApiException && content.Reads == 1 && content.Disposed &&
                handler.Requests.Count == 1 && notifications.Count == 1 &&
                notifications[0].Reason == (throttle ? TextKey.WaitTooManyWrites : TextKey.WaitTooManyRequests),
                "One bounded body read produces one classified feedback and disposes the original content.");
            Check.Require(ReferenceEquals(attempt.Completion?.Error, error),
                "The supplied finite attempt retains the original refusal without replaying its content.");
        }

        using var failedHandler = new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new FailingContent() }));
        using var failedHttp = new HttpClient(failedHandler);
        var failed = await FailureOf(Create(failedHttp, new Tokens()).RpcAsync(DropboxProtocol.GetMetadata,
            new { }, DropboxReply.Void(), new RequestAttempt(new object(), AttemptOwner.Recovery), default));
        Check.Require(failed is HttpRequestException { InnerException: { } cause } &&
            ReferenceEquals(cause, FailingContent.Failure), "A bounded-body read failure retains its cause.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var attemptCancelled = new RequestAttempt(new object(), AttemptOwner.Recovery);
        Check.Require(await FailureOf(Create(failedHttp, new Tokens()).RpcAsync(DropboxProtocol.GetMetadata,
            new { }, DropboxReply.Void(), attemptCancelled, cancelled.Token)) is OperationCanceledException &&
            attemptCancelled.Completion?.Result == RequestResult.CallerStopped && failedHandler.Requests.Count == 1,
            "Cancellation completes the finite attempt without a further wire request.");
    }

    private static async Task RefreshTokenAsync()
    {
        foreach (var owner in new[] { AttemptOwner.Listener, AttemptOwner.Prerequisite,
            AttemptOwner.Transfer, AttemptOwner.Recovery })
        foreach (var wait in new TimeSpan?[] { null, TimeSpan.Zero, TimeSpan.FromMinutes(RetryMinutes) })
        {
            var tokens = new Tokens();
            using var handler = new Handler((request, _) =>
            {
                Check.Require(request.Headers.Authorization?.Parameter == tokens.Current,
                    "Each admitted attempt fetches the current token.");
                var response = new HttpResponseMessage(tokens.Calls == 1
                    ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                {
                    Content = new StringContent(tokens.Calls == 1 ? ErrorJson : "{\"cursor\":\"next\"}")
                };
                if (wait is { } supplied) response.Headers.RetryAfter = new RetryConditionHeaderValue(supplied);
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var time = new ObservedClock();
            var api = Create(http, tokens, time);
            var operation = new object();
            var attempt = new RequestAttempt(operation, owner);
            try
            {
                await api.RpcAsync(DropboxProtocol.ListFolderContinue, new { cursor = "prior" },
                    DropboxReply.Cursor(), attempt, default);
                Check.Require(false, "The enclosing owner must receive the finite failure.");
            }
            catch (DropboxApiException error)
            {
                Check.Require(attempt.Completion is { } completion &&
                    completion.Result == RequestResult.TransientFailure &&
                    ReferenceEquals(completion.Error, error) && completion.ProviderWait == wait,
                    "The original error and applicable provider wait cross the single attempt boundary.");
            }
            Check.Require(handler.Requests.Count == 1 && attempt.TryClaimCompletion() &&
                !attempt.TryClaimCompletion(), "There is one request and one completion claim, not a nested retry.");
            if (wait is { } delay) time.Advance(delay);
            tokens.Current = "refreshed-fixture";
            var next = new RequestAttempt(operation, owner);
            var cursor = await api.RpcAsync(DropboxProtocol.ListFolderContinue, new { cursor = "prior" },
                DropboxReply.Cursor(), next, default);
            Check.Require(cursor == "next" && next.Completion?.Result == RequestResult.Success &&
                handler.Requests.Count == ReplacementConnections && tokens.Calls == ReplacementConnections &&
                attempt.Completion?.Result == RequestResult.TransientFailure,
                "A separately admitted successful attempt cannot rewrite its predecessor's completion.");
        }
    }

    private static async Task RejectNonSeekableAsync()
    {
        using var body = new NonSeekableStream();
        using var handler = new Handler((_, _) => throw new InvalidOperationException(nameof(RejectNonSeekableAsync)));
        using var http = new HttpClient(handler);
        var tokens = new Tokens();
        var rejected = false;
        try
        {
            await Create(http, tokens).UploadAsync(DropboxProtocol.Upload, new { }, body,
                DropboxReply.Void(), new RequestAttempt(new object(), AttemptOwner.Request), CancellationToken.None);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Check.Require(rejected && handler.Requests.Count == 0 && tokens.Calls == 0, nameof(RejectNonSeekableAsync));
        Check.Require(body.CanRead, nameof(body.CanRead));
    }

    private static async Task FailedUploadAsync()
    {
        foreach (var cancelled in new[] { false, true })
        {
            using var body = new ObservedStream(Encoding.UTF8.GetBytes(Payload));
            using var handler = new Handler(async (request, ct) =>
            {
                _ = await request.Content!.ReadAsByteArrayAsync(ct);
                if (cancelled) throw new OperationCanceledException(ct);
                throw new HttpRequestException();
            });
            using var http = new HttpClient(handler);
            var rejected = false;
            try
            {
                await Create(http, new Tokens()).UploadAsync(
                    DropboxProtocol.Upload, new { path = ArgumentPath }, body, DropboxReply.Void(),
                    new RequestAttempt(new object(), AttemptOwner.Request), CancellationToken.None);
            }
            catch (Exception error) when (cancelled ? error is OperationCanceledException : error is HttpRequestException)
            {
                rejected = true;
            }
            Check.Require(rejected && handler.Requests.Count == 1, nameof(FailedUploadAsync));
            Check.Require(!body.Disposed && body.CanRead, nameof(body.Disposed));
        }
    }

    private static async Task HeaderActivityAsync()
    {
        foreach (var (allowance, gaps, succeeds) in new[]
        {
            (OrdinarySeconds, new[] { PoolReadSeconds, ProgressSeconds, LastProgressSeconds }, true),
            (OrdinarySeconds, new[] { PoolReadSeconds, ProgressSeconds, OrdinarySeconds }, false),
            (OrdinarySeconds, new[] { InsideOrdinarySeconds }, true),
            (OrdinarySeconds, new[] { OrdinarySeconds }, false),
            (LongpollSeconds, new[] { InsideLongpollSeconds }, true),
            (LongpollSeconds, new[] { OutsideLongpollSeconds }, false)
        })
        {
            using var wire = new Wire(TimeSpan.FromSeconds(allowance)) { Respond = Wire.Hold };
            var pending = wire.GetAsync();
            var connection = await wire.AcceptAsync();
            await connection.RequestAsync();
            var parts = gaps.Length == 1
                ? new[] { StatusLine + ReplyTail } : new[] { StatusLine, ProgressHeader, ReplyTail };
            for (var index = 0; index < gaps.Length; index++)
            {
                await Within(connection.Parked);
                wire.Clock.Advance(TimeSpan.FromSeconds(gaps[index]));
                if (succeeds || index < gaps.Length - 1) connection.Send(parts[index]);
            }
            var failure = await FailureOf(pending);
            Check.Require(succeeds ? failure is null && await pending == "ok" : IsExpiry(failure),
                "Progress renews the pending read; silence expires it at the allowance after the last progress.");
        }
    }

    private static async Task PoolMatrixAsync()
    {
        var cases =
            from phase in Enum.GetValues<PoolPhase>()
            from activity in Enum.GetValues<PoolEvent>()
            where activity != PoolEvent.DelayedExpiry || phase == PoolPhase.FirstWrite
            select PoolCaseAsync(phase, activity);
        await Task.WhenAll(cases);
    }

    private static async Task PoolCaseAsync(PoolPhase phase, PoolEvent activity)
    {
        using var wire = new Wire(PoolReadAllowance);
        var clock = wire.Clock;
        if (phase == PoolPhase.Scavenged) HandlerOf(wire.Client).PooledConnectionIdleTimeout = ScavengeIdleTimeout;
        Check.Require(await wire.GetAsync() == "ok", "The first guarded request establishes the pooled connection.");
        var connection = await wire.AcceptAsync();
        if (phase == PoolPhase.Scavenged) await Within(connection.IdleRead);
        if (activity == PoolEvent.None)
        {
            await SurvivingConnectionAsync(wire, connection, phase);
            return;
        }

        void Inject()
        {
            switch (activity)
            {
                case PoolEvent.End: connection.End(); break;
                case PoolEvent.Data: connection.Send("X"); break;
                case PoolEvent.Fault: connection.End(new IOException("idle-fixture")); break;
                case PoolEvent.Expiry: clock.Advance(PoolReadAllowance + OneSecond); break;
                default: clock.Stall(PoolReadAllowance + OneSecond); break;
            }
        }

        switch (phase)
        {
            case PoolPhase.Checkout:
                connection.BeforeRead = Inject;
                break;
            case PoolPhase.FirstWrite:
                connection.BeforeWrite = (_, _) =>
                {
                    connection.BeforeWrite = null;
                    Inject();
                    return Task.CompletedTask;
                };
                break;
            default:
                if (activity == PoolEvent.Expiry)
                {
                    connection.Wake();
                    await Within(connection.Parked);
                }
                Inject();
                break;
        }
        // The handler may replace the connection silently or fail this one request; the next one must work.
        var attempt = await FailureOf(wire.GetAsync());
        Check.Require(attempt is not OperationCanceledException, "A discarded connection surfaces no stale cancellation.");
        Check.Require(await wire.GetAsync() == "ok", "The request after a discarded idle connection succeeds.");
        await Within(connection.Closed);
        Check.Require(wire.ConnectionCount == ReplacementConnections, "An expired, faulted or dirty idle connection is never reused or revived.");
        clock.Advance(PoolReadAllowance * ReplacementConnections);
        Check.Require(clock.ArmedTimers == 0 && await wire.GetAsync() == "ok" && wire.ConnectionCount == ReplacementConnections,
            "No stale timer reaches the replacement connection.");
    }

    private static async Task SurvivingConnectionAsync(Wire wire, ScriptedConnection connection, PoolPhase phase)
    {
        var clock = wire.Clock;
        if (phase != PoolPhase.FirstWrite)
        {
            Check.Require(clock.ArmedTimers == 0, "An idle connection holds no timer without a pending operation.");
            clock.Advance(IdleGap);
            Check.Require(await wire.GetAsync() == "ok" && wire.ConnectionCount == 1,
                "An idle gap cannot expire a connection; checkout starts a fresh allowance.");
            return;
        }
        var almost = PoolReadAllowance - OneSecond;
        wire.Respond = Wire.Hold;
        connection.BeforeWrite = (_, _) =>
        {
            connection.BeforeWrite = null;
            clock.Advance(almost);
            return Task.CompletedTask;
        };
        var pending = wire.GetAsync();
        await connection.RequestAsync();
        await connection.RequestAsync();
        await Within(connection.Parked);
        clock.Advance(almost);
        Check.Require(!pending.IsCompleted, "A completed first write renews a surviving checkout read.");
        connection.Send(StatusLine + ReplyTail);
        Check.Require(await pending == "ok" && wire.ConnectionCount == 1, "The renewed connection serves the request.");
    }

    private static async Task ProgressingUploadAsync()
    {
        using var wire = new Wire(DropboxApiLimits.ReadWriteAllowance) { Respond = Wire.Hold };
        var slice = TimeSpan.FromSeconds(ProgressSeconds);
        wire.Configure = link => link.BeforeWrite = (_, _) =>
        {
            wire.Clock.Advance(slice);
            return Task.CompletedTask;
        };
        using var content = new SlicedContent(UploadChunks, UploadChunks * WriteSliceBytes + 1);
        var pending = wire.Client.PostAsync(Wire.Url, content);
        var connection = await wire.AcceptAsync();
        await connection.RequestAsync();
        await Within(content.Sent);
        await Within(connection.Parked);
        connection.Send(StatusLine + ReplyTail);
        using var response = await Within(pending);
        Check.Require(response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == "ok",
            "An upload with timely slices has no total duration limit.");
        Check.Require(connection.Slices.Count * slice >= TimeSpan.FromMinutes(UploadMinutes) &&
            connection.Slices.Max() <= WriteSliceBytes && connection.Slices.Sum() >= UploadSlices * WriteSliceBytes,
            "The ten-minute upload crosses every byte in slices of at most 64 KiB.");
    }

    private static async Task OverdueSliceAsync()
    {
        foreach (var (seconds, overdue) in new[] { (InsideOrdinarySeconds, false), (OrdinarySeconds, true) })
        {
            using var wire = new Wire(DropboxApiLimits.ReadWriteAllowance) { Respond = Wire.Hold };
            var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wire.Configure = link => link.BeforeWrite = async (count, ct) =>
            {
                if (count != WriteSliceBytes) return;
                link.BeforeWrite = null;
                wire.Clock.Advance(TimeSpan.FromSeconds(MidSliceSeconds));
                link.Send(StatusLine);
                wire.Clock.Advance(TimeSpan.FromSeconds(seconds - MidSliceSeconds));
                if (!overdue) return;
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { aborted.TrySetResult(); }
            };
            using var content = new SlicedContent(ReplacementConnections, UploadChunks * WriteSliceBytes + 1);
            var pending = wire.Client.PostAsync(Wire.Url, content);
            var connection = await wire.AcceptAsync();
            if (overdue)
            {
                await Within(aborted.Task);
                // Reaching here proves the owned write deadline fired although read progress arrived mid-slice.
                wire.Clock.Advance(DropboxApiLimits.ReadWriteAllowance);
                Check.Require(await FailureOf(pending) is not null, "An overdue write slice fails its request.");
                await Within(connection.Closed);
            }
            else
            {
                await Within(content.Sent);
                await Within(connection.Parked);
                connection.Send(ReplyTail);
                using var response = await Within(pending);
                Check.Require(response.IsSuccessStatusCode, "A slice inside its deadline completes.");
            }
        }
    }

    private static async Task EarlyResponseAsync()
    {
        var almost = DropboxApiLimits.ReadWriteAllowance - OneSecond;
        foreach (var expires in new[] { true, false })
        {
            // Expect forces the pending read to overlap the body, which a plain upload need not do.
            using var wire = new Wire(DropboxApiLimits.ReadWriteAllowance)
            {
                Respond = static (link, _) => link.Send("HTTP/1.1 100 Continue\r\n\r\n")
            };
            ScriptedConnection transport = null!;
            wire.Configure = link => transport = link;
            using var content = new SlicedContent(EarlyResponseChunks, WriteSliceBytes, async index =>
            {
                await Within(transport.Parked);
                wire.Clock.Advance(TimeSpan.FromSeconds(MidSliceSeconds));
                if (index == 0) transport.Send(StatusLine);
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, Wire.Url) { Content = content };
            request.Headers.ExpectContinue = true;
            var pending = wire.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await Within(content.Sent);
            await Within(transport.Parked);
            wire.Clock.Advance(almost);
            Check.Require(!pending.IsCompleted, "Write completions keep a stalled early response alive until T+120.");
            if (expires)
            {
                wire.Clock.Advance(OneSecond);
                Check.Require(IsExpiry(await FailureOf(pending)), "With no later read, the response expires at T+120.");
                continue;
            }
            transport.Send(ProgressHeader);
            await Within(transport.Parked);
            wire.Clock.Advance(almost);
            Check.Require(!pending.IsCompleted, "A later read renews the post-upload bound.");
            transport.Send(ReplyTail);
            using var response = await Within(pending);
            Check.Require(await response.Content.ReadAsStringAsync() == "ok", "The early response completes.");
        }
    }

    private static async Task TransportContractsAsync()
    {
        await CallerCancellationAsync();
        await ConsumerGapAsync();
        await DisposalAsync();
        foreach (var (reply, end, expected) in new (string, bool, string?)[]
        {
            (StatusLine + "Connection: close\r\n\r\nok", true, "ok"),
            (StatusLine + "Content-Length: 0\r\n\r\n", false, ""),
            (StatusLine + "Content-Length: 4\r\n\r\nok", true, null)
        })
        {
            using var wire = new Wire(DropboxApiLimits.ReadWriteAllowance)
            {
                Respond = (link, _) =>
                {
                    link.Send(reply);
                    if (end) link.End();
                }
            };
            var pending = wire.GetAsync();
            var failure = await FailureOf(pending);
            Check.Require(expected is null ? failure is not null : failure is null && await pending == expected,
                "EOF ends a delimited body, an empty body is complete, and a truncated body fails.");
        }
    }

    private static async Task CallerCancellationAsync()
    {
        var allowance = DropboxApiLimits.ReadWriteAllowance;
        using var wire = new Wire(allowance) { Respond = Wire.Hold };
        using var stop = new CancellationTokenSource();
        var pending = wire.Client.GetAsync(Wire.Url, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        var connection = await wire.AcceptAsync();
        await connection.RequestAsync();
        await Within(connection.Parked);
        stop.Cancel();
        var failure = await FailureOf(pending);
        Check.Require(failure is OperationCanceledException && !IsExpiry(failure),
            "Caller cancellation is never reported as an owned transport expiry.");
        await Within(connection.Closed);
        wire.Clock.Advance(allowance * ReplacementConnections);
        wire.Respond = Wire.Serve;
        Check.Require(wire.Clock.ArmedTimers == 0 && await wire.GetAsync() == "ok" && wire.ConnectionCount == ReplacementConnections,
            "A cancelled exchange retires its timers and the next request uses a clean connection.");
    }

    private static async Task ConsumerGapAsync()
    {
        using var wire = new Wire(DropboxApiLimits.ReadWriteAllowance)
        {
            Respond = static (link, _) => link.Send(StatusLine + "Content-Length: 4\r\n\r\nok")
        };
        using var response = await Within(wire.Client.GetAsync(Wire.Url, HttpCompletionOption.ResponseHeadersRead));
        await using var body = await response.Content.ReadAsStreamAsync();
        var connection = await wire.AcceptAsync();
        var bytes = new byte[HeaderTerminatorBytes];
        await Within(body.ReadAtLeastAsync(bytes.AsMemory(0, BodyPairBytes), BodyPairBytes).AsTask());
        Check.Require(wire.Clock.ArmedTimers == 0, "A consumer gap holds no timer.");
        wire.Clock.Advance(IdleGap);
        connection.Send("ok");
        await Within(body.ReadAtLeastAsync(bytes.AsMemory(BodyPairBytes), BodyPairBytes).AsTask());
        Check.Require(Encoding.ASCII.GetString(bytes) == "okok", "A read after a consumer gap gets a fresh allowance.");
    }

    private static async Task DisposalAsync()
    {
        using var wire = new Wire(DropboxApiLimits.ReadWriteAllowance)
        {
            Respond = static (link, _) => link.Send(StatusLine + "Connection: close\r\n\r\nok")
        };
        var response = await Within(wire.Client.GetAsync(Wire.Url, HttpCompletionOption.ResponseHeadersRead));
        var body = await response.Content.ReadAsStreamAsync();
        var connection = await wire.AcceptAsync();
        var bytes = new byte[BodyPairBytes];
        await Within(body.ReadAtLeastAsync(bytes, BodyPairBytes).AsTask());
        var read = body.ReadAsync(bytes).AsTask();
        await Within(connection.Parked);
        response.Dispose();
        await FailureOf(read);
        await Within(connection.Closed);
        Check.Require(wire.Clock.ArmedTimers == 0, "Disposal retires the pending read's timer before the stream closes.");
        wire.Clock.Advance(IdleGap);
    }

    private static async Task RedirectAndProxyAsync()
    {
        using (var wire = new Wire(DropboxApiLimits.ReadWriteAllowance)
        {
            Respond = static (link, _) => link.Send(
                "HTTP/1.1 302 Found\r\nLocation: http://other.fixture/\r\nContent-Length: 0\r\n\r\n")
        })
        {
            using var response = await Within(wire.Client.GetAsync(Wire.Url, HttpCompletionOption.ResponseHeadersRead));
            Check.Require(response.StatusCode == HttpStatusCode.Redirect && wire.ConnectionCount == 1 &&
                wire.Hosts.All(host => host == Wire.Host), "A guarded client never follows a redirect.");
        }
        foreach (var expires in new[] { true, false })
        {
            using var wire = new Wire(DropboxApiLimits.ReadWriteAllowance) { Respond = Wire.Hold };
            HandlerOf(wire.Client).Proxy = new WebProxy("http://proxy.fixture:8080");
            var pending = wire.GetAsync();
            var connection = await wire.AcceptAsync();
            var request = await connection.RequestAsync();
            Check.Require(request.StartsWith("GET " + Wire.Url + " ", StringComparison.Ordinal) &&
                wire.Hosts.Single() == "proxy.fixture", "The fixture really routes through the proxy.");
            await Within(connection.Parked);
            wire.Clock.Advance(DropboxApiLimits.ReadWriteAllowance - OneSecond);
            Check.Require(!pending.IsCompleted, "The guard allows the whole allowance through a proxy.");
            if (expires)
            {
                wire.Clock.Advance(OneSecond);
                Check.Require(IsExpiry(await FailureOf(pending)), "A silent proxied exchange expires.");
                continue;
            }
            connection.Send(StatusLine + ReplyTail);
            Check.Require(await pending == "ok", "A progressing proxied exchange succeeds.");
        }
    }

    private static async Task TlsActivityAsync()
    {
        using var key = RSA.Create(RsaBits);
        var signing = new CertificateRequest("CN=fixture.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = signing.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-OneUnit), DateTimeOffset.UtcNow.AddDays(OneUnit));
        // SChannel cannot serve a freshly created certificate's ephemeral key; the PFX round trip persists it.
        using var certificate = new X509Certificate2(created.Export(X509ContentType.Pfx));
        foreach (var tunnel in new[] { false, true })
        foreach (var expires in new[] { true, false })
        {
            await TlsCaseAsync(certificate, tunnel, expires);
        }
    }

    // Bug #69 control: two guards can share one clock (proxy tunnel), so a timer armed before the last advance
    // is not the exchange that advance is measuring.
    private static async Task ClockMatchesOnlyFreshAllowancesAsync()
    {
        var allowance = TimeSpan.FromSeconds(TlsReadSeconds);
        var clock = new ObservedClock();
        using var first = clock.CreateTimer(static _ => { }, null, allowance, Timeout.InfiniteTimeSpan);
        await clock.PendingAsync(allowance);
        using var stale = clock.CreateTimer(static _ => { }, null, allowance, Timeout.InfiniteTimeSpan);
        clock.Advance(allowance - OneSecond);
        var pending = clock.PendingAsync(allowance);
        Check.Require(!pending.IsCompleted, "A timer armed before the last advance is never the pending read.");
        using var fresh = clock.CreateTimer(static _ => { }, null, allowance, Timeout.InfiniteTimeSpan);
        await Within(pending);
    }

    private static async Task TlsCaseAsync(X509Certificate2 certificate, bool tunnel, bool expires)
    {
        // Differs from the 120 s write allowance so the clock can tell a pending read from a write.
        var allowance = TimeSpan.FromSeconds(TlsReadSeconds);
        var clock = new ObservedClock();
        using var client = Massdex.SyncShark.V2.EngineComposition.CreateDropboxHttpClient(
            allowance, DecompressionMethods.None, clock);
        var handler = HandlerOf(client);
        handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (tunnel) handler.Proxy = new WebProxy("http://127.0.0.1:" + port);
        var url = tunnel ? "https://fixture.test:" + port + "/" : "https://127.0.0.1:" + port + "/";
        using var stop = new CancellationTokenSource();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = Channel.CreateUnbounded<string>();
        var server = ServeAsync();
        var pending = FetchAsync();
        try
        {
            await Within(received.Task);
            await Within(clock.PendingAsync(allowance));
            clock.Advance(allowance - OneSecond);
            Check.Require(!pending.IsCompleted, "The guard above TLS allows the whole allowance.");
            if (expires)
            {
                clock.Advance(OneSecond);
                Check.Require(IsExpiry(await FailureOf(pending)), "A silent TLS exchange expires.");
                return;
            }
            await replies.Writer.WriteAsync(StatusLine);
            await Within(clock.PendingAsync(allowance));
            clock.Advance(allowance - OneSecond);
            Check.Require(!pending.IsCompleted, "Decrypted read progress renews the allowance.");
            await replies.Writer.WriteAsync(ReplyTail);
            Check.Require(await Within(pending) == "ok", "A progressing TLS exchange succeeds.");
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await server; }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or
                ObjectDisposedException or System.Security.Authentication.AuthenticationException) { }
        }

        async Task<string> FetchAsync()
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return await response.Content.ReadAsStringAsync();
        }

        async Task ServeAsync()
        {
            using var socket = await listener.AcceptTcpClientAsync(stop.Token);
            await using var network = socket.GetStream();
            if (tunnel)
            {
                await ReadHeadersAsync(network, stop.Token);
                await network.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), stop.Token);
            }
            await using var tls = new SslStream(network);
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions { ServerCertificate = certificate }, stop.Token);
            await ReadHeadersAsync(tls, stop.Token);
            received.TrySetResult();
            await foreach (var reply in replies.Reader.ReadAllAsync(stop.Token))
            {
                await tls.WriteAsync(Encoding.ASCII.GetBytes(reply), stop.Token);
            }
        }
    }

    private static async Task ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var matched = 0;
        var one = new byte[OneUnit];
        while (matched < HeaderTerminatorBytes)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException();
            matched = one[0] == (matched % BodyPairBytes == 0 ? '\r' : '\n') ? matched + 1 : one[0] == '\r' ? OneUnit : 0;
        }
    }

    private static async Task ConcurrentPoolsAsync()
    {
        var clock = new ObservedClock();
        using var ordinary = new Wire(DropboxApiLimits.ReadWriteAllowance, clock) { Respond = Wire.Hold };
        using var longpoll = new Wire(TimeSpan.FromSeconds(LongpollSeconds), clock) { Respond = Wire.Hold };
        var first = ordinary.GetAsync();
        var second = ordinary.GetAsync();
        var poll = longpoll.GetAsync();
        var quiet = await ordinary.AcceptAsync();
        var active = await ordinary.AcceptAsync();
        var polled = await longpoll.AcceptAsync();
        foreach (var connection in new[] { quiet, active, polled })
        {
            await connection.RequestAsync();
            await Within(connection.Parked);
        }
        clock.Advance(TimeSpan.FromSeconds(MidSliceSeconds));
        active.Send(StatusLine);
        await Within(active.Parked);
        clock.Advance(TimeSpan.FromSeconds(ExpiryStepSeconds));
        var expired = await Within(Task.WhenAny(first, second));
        Check.Require(IsExpiry(await FailureOf(expired)), "One silent exchange expires independently.");
        var survivor = ReferenceEquals(expired, first) ? second : first;
        Check.Require(!survivor.IsCompleted && !poll.IsCompleted,
            "An expiry affects neither its pool sibling nor the separate longpoll pool.");
        active.Send(ReplyTail);
        Check.Require(await survivor == "ok" && ordinary.ConnectionCount == ReplacementConnections, "HTTP/1.1 requests hold separate connections.");
        clock.Advance(TimeSpan.FromSeconds(LongpollRemainderSeconds));
        Check.Require(!poll.IsCompleted, "The longpoll pool keeps its own larger allowance at 569 s.");
        polled.Send(StatusLine + ReplyTail);
        Check.Require(await poll == "ok" && longpoll.ConnectionCount == 1, "The longpoll answer arrives on its own pool.");
    }

    private static async Task LongpollRebindAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        using var borrowedHandler = new Handler(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var borrowed = new HttpClient(borrowedHandler, disposeHandler: false);
        try
        {
            foreach (var (requested, allowance) in new[] { (OrdinarySeconds, ShortLongpollAllowanceSeconds), (DropboxApiLimits.LongpollMaxSeconds, LongpollSeconds) })
            {
                await LongpollGenerationAsync(Path.Combine(root, requested.ToString()), borrowed,
                    TimeSpan.FromSeconds(requested), TimeSpan.FromSeconds(allowance));
            }
            using var response = await borrowed.GetAsync(Wire.Url);
            Check.Require(response.IsSuccessStatusCode && borrowedHandler.Requests.Count == 1 &&
                DropboxApiLimits.ReadWriteAllowance == TimeSpan.FromSeconds(OrdinarySeconds),
                "Rebuilding the longpoll pool leaves the borrowed ordinary 120 s client unused and undisposed.");
            Check.Require(!typeof(Massdex.SyncShark.V2.EngineComposition)
                .GetProperty(nameof(Massdex.SyncShark.V2.EngineComposition.Settings))!.CanWrite,
                "A setting change is a new composition, not a hot rebind.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task LongpollGenerationAsync(string productRoot, HttpClient borrowed,
        TimeSpan requested, TimeSpan allowance)
    {
        var clock = new ObservedClock();
        var settings = Massdex.SyncShark.V2.V2Settings.Default with { DropboxLongpollTimeout = requested };
        var composition = Massdex.SyncShark.V2.EngineComposition.Open(
            new Massdex.SyncShark.V2.EngineHostInputs(productRoot, null, borrowed), settings, clock);
        Wire wire;
        ScriptedConnection draining;
        Task<string> stale;
        try
        {
            Check.Require(composition.LongpollHttpTimeout == Timeout.InfiniteTimeSpan &&
                composition.LongpollHttpTimeout != allowance &&
                Massdex.SyncShark.V2.EngineComposition.LongpollTimeoutFor(composition.Settings) == allowance,
                "The composed client has no total timeout; the factory's derived allowance is requested + 90 s.");
            var client = (HttpClient)typeof(Massdex.SyncShark.V2.EngineComposition)
                .GetField("_longpollHttp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composition)!;
            wire = new Wire(client, clock) { Respond = Wire.Hold };

            var alive = wire.GetAsync();
            var first = await wire.AcceptAsync();
            await first.RequestAsync();
            await Within(first.Parked);
            clock.Advance(allowance - OneSecond);
            Check.Require(!alive.IsCompleted, "The composed pool binds its read allowance to the frozen request setting.");
            first.Send(StatusLine + "Connection: close\r\n" + ReplyTail);
            Check.Require(await alive == "ok", "A reply inside the composed allowance succeeds.");

            var silent = wire.GetAsync();
            var second = await wire.AcceptAsync();
            await second.RequestAsync();
            await Within(second.Parked);
            clock.Advance(allowance + OneSecond);
            Check.Require(IsExpiry(await FailureOf(silent)), "The composed pool expires just past its allowance.");

            stale = wire.GetAsync();
            draining = await wire.AcceptAsync();
            await draining.RequestAsync();
            await Within(draining.Parked);
        }
        finally
        {
            composition.Dispose();
        }
        Check.Require(await FailureOf(stale) is not null, "Closing the generation drains its in-flight longpoll.");
        await Within(draining.Closed);
        Check.Require(clock.ArmedTimers == 0, "The closed generation leaves no pending timer.");
        var connections = wire.ConnectionCount;
        // A disposed client throws from the call itself, so the task boundary captures it.
        Check.Require(await FailureOf(Task.Run(() => wire.Client.GetAsync(Wire.Url))) is ObjectDisposedException &&
            wire.ConnectionCount == connections, "The old generation's client receives no stale call.");
    }

    private sealed class ObservedClock : TimeProvider
    {
        private readonly FakeTimeProvider clock = new();
        private readonly object gate = new();
        private readonly List<ObservedTimer> timers = [];
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long serial;
        private long consumed;
        private long stalled;
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override long GetTimestamp() => clock.GetUtcNow().UtcTicks + Interlocked.Read(ref stalled);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) => clock.Advance(duration);
        /// <summary>Monotonic time passes while no timer fires: a delayed callback.</summary>
        public void Stall(TimeSpan duration) => Interlocked.Add(ref stalled, duration.Ticks);
        public int ArmedTimers
        {
            get
            {
                lock (gate) return timers.Count(timer => timer.Due != Timeout.InfiniteTimeSpan);
            }
        }
        public async Task PendingAsync(TimeSpan allowance)
        {
            while (true)
            {
                Task wait;
                var now = clock.GetUtcNow();
                lock (gate)
                {
                    var timer = timers.LastOrDefault(value => value.Serial > consumed &&
                        value.Due == allowance && value.ArmedAt == now);
                    if (timer is not null)
                    {
                        consumed = timer.Serial;
                        return;
                    }
                    wait = changed.Task;
                }
                await wait;
            }
        }
        private void Changed()
        {
            var signal = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.TrySetResult();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var inner = clock.CreateTimer(callback, state, dueTime, period);
            lock (gate)
            {
                var timer = new ObservedTimer(this, inner, ++serial, dueTime) { ArmedAt = clock.GetUtcNow() };
                timers.Add(timer);
                Changed();
                return timer;
            }
        }
        private sealed class ObservedTimer(ObservedClock owner, ITimer inner, long serial, TimeSpan due) : ITimer
        {
            public long Serial { get; } = serial;
            public TimeSpan Due { get; private set; } = due;
            public DateTimeOffset ArmedAt { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var result = inner.Change(dueTime, period);
                lock (owner.gate) { Due = dueTime; ArmedAt = owner.clock.GetUtcNow(); owner.Changed(); }
                return result;
            }
            public void Dispose()
            {
                inner.Dispose();
                lock (owner.gate) { owner.timers.Remove(this); owner.Changed(); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static async Task PageWaitLifecycleAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var end in Enum.GetValues<PageWaitEnd>())
            {
                await PageWaitCaseAsync(Path.Combine(folder, end + ".db"), end);
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static async Task PageWaitCaseAsync(string path, PageWaitEnd end)
    {
        using IStateStore store = Massdex.SyncShark.Engine.Storage.StateStore.Open(path);
        var pair = TestPair();
        store.SavePair(pair);
        var notice = StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, "", new IssueSubject([], null))]
        };
        var operation = new object();
        var before = store.ReadPrerequisiteRetry(pair.PairId);
        Refused(() => store.RecordPrerequisitePageWait(pair.PairId, OneUnit, operation, notice));
        Check.Require(store.Issues(pair.PairId).Count == 0, "A stale generation cannot raise the page-wait notice.");
        var calls = 0;
        using var handler = new Handler((_, _) => ++calls <= RepeatedFailureCount
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("read-fixture"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"entries\":[],\"cursor\":\"next\",\"has_more\":false}")
            }));
        using var http = new HttpClient(handler);
        var api = Create(http, new Tokens());
        Issue wait = null!;
        // Three observations establish persistent failure without prescribing a runtime attempt limit.
        for (var index = 0; index < RepeatedFailureCount; index++)
        {
            var attempt = new RequestAttempt(operation, AttemptOwner.Prerequisite);
            try
            {
                await using var page = await api.ReadPageAsync(DropboxProtocol.ListFolderContinue,
                    new { cursor = "prior" }, pair, attempt, default);
                Check.Require(false, "A failed page attempt cannot publish a page.");
            }
            catch (HttpRequestException)
            {
                Check.Require(attempt.Completion?.Result == RequestResult.TransientFailure &&
                    attempt.TryClaimCompletion(), "The page-wait owner claims one transient disposition per attempt.");
            }
            var current = store.RecordPrerequisitePageWait(pair.PairId, 0, operation, notice);
            Check.Require((index == 0 || current.Id == wait.Id) && current.Kind == IssueKind.Failure &&
                current.Cause == TextKey.RequiredRepairRetry && store.Issues(pair.PairId).Count == 1 &&
                store.ReadPrerequisiteRetry(pair.PairId) == before,
                "First and repeated page failures keep one notice without durable pair retry accounting.");
            wait = current;
        }
        foreach (var (generation, owner, expected) in new[]
        {
            ((long)OneUnit, operation, wait), (0L, new object(), wait), (0L, operation, wait with { Id = wait.Id + 1 })
        })
        {
            Refused(() => store.CompletePrerequisitePageWait(pair.PairId, generation, owner, expected));
            Check.Require(store.Issues(pair.PairId).Any(issue => issue.Id == wait.Id),
                "A stale generation, operation or notice identity cannot clear the page-wait contribution.");
        }
        switch (end)
        {
            case PageWaitEnd.Sealed:
            {
                var attempt = new RequestAttempt(operation, AttemptOwner.Prerequisite);
                await using var page = await api.ReadPageAsync(DropboxProtocol.ListFolderContinue,
                    new { cursor = "prior" }, pair, attempt, default);
                Check.Require(attempt.Completion?.Result == RequestResult.Success, "The same page sealed.");
                break;
            }
            case PageWaitEnd.Cancelled:
            {
                using var stop = new CancellationTokenSource();
                stop.Cancel();
                var attempt = new RequestAttempt(operation, AttemptOwner.Prerequisite);
                try
                {
                    await using var page = await api.ReadPageAsync(DropboxProtocol.ListFolderContinue,
                        new { cursor = "prior" }, pair, attempt, stop.Token);
                    Check.Require(false, "A cancelled page attempt cannot publish a page.");
                }
                catch (OperationCanceledException)
                {
                    Check.Require(attempt.Completion?.Result == RequestResult.CallerStopped, "Cancellation is CallerStopped.");
                }
                break;
            }
            default:
                store.SavePair(pair with { Enabled = false });
                break;
        }
        store.CompletePrerequisitePageWait(pair.PairId, 0, operation, wait);
        Check.Require(store.Issues(pair.PairId).All(issue => issue.Id != wait.Id) &&
            store.ReadPrerequisiteRetry(pair.PairId) == before,
            "Sealing, cancellation and disable close only the exact page wait, not pair state.");

        store.SavePair(pair);
        var local = store.ReadSource(pair.PairId, Side.Local)!.Value;
        var remote = store.ReadSource(pair.PairId, Side.Dropbox)!.Value;
        var debt = new Massdex.SyncShark.V2.FailureRetryOwner.State(OneUnit, DateTimeOffset.UnixEpoch.AddSeconds(OneUnit));
        store.RecordPrerequisiteFailure(pair.PairId, 0, local.Epoch, remote.Epoch,
            local.Revision, remote.Revision, before, debt, notice);
        var protectedNotice = store.RecordPrerequisitePageWait(pair.PairId, 0, operation, notice);
        store.CompletePrerequisitePageWait(pair.PairId, 0, operation, protectedNotice);
        Check.Require(store.ReadPrerequisiteRetry(pair.PairId) == debt &&
            store.Issues(pair.PairId).Any(issue => issue.Id == protectedNotice.Id),
            "Page success cannot erase an independently failed Repair episode or its protected notice.");
        var issues = store.Issues(pair.PairId).Count;
        // A changed subject forces a real UPDATE, so a read-only store must refuse it instead of eliding an identical raise.
        var writeNotice = notice with
        {
            Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, "",
                new IssueSubject([], null, FirstRunDeletion: IssueSubject.FirstRunDeletionKind.Item))]
        };
        using var readOnly = Massdex.SyncShark.Engine.Storage.StateStore.OpenReadOnly(path)!;
        Check.Require(Refused(() => ((IStateStore)readOnly).RecordPrerequisitePageWait(pair.PairId, 0, new object(), writeNotice)) &&
            store.Issues(pair.PairId).Count == issues && store.ReadPrerequisiteRetry(pair.PairId) == debt,
            "A failed notice write surfaces an error, never a healthy completion.");
    }

    // A stale refusal may throw or leave the store unchanged, so callers assert the resulting state.
    private static bool Refused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static async Task DecoderMatrixAsync()
    {
        var pair = TestPair();
        await MetadataMatrixAsync(pair);
        await HistoryHeadMatrixAsync();
        await FolderReplyMatrixAsync(pair);
        await ScalarMatrixAsync();
        await LexicalMatrixAsync();
    }

    private static string FileJson(string? field = null, string? first = null, string? second = null) =>
        "{" + string.Join(",", MetadataFields.Select(item => item.Name == field
            ? "\"" + field + "\":" + first + ",\"" + field + "\":" + second : "\"" + item.Name + "\":" + item.Valid)) + "}";

    // JsonDocument keeps the last duplicate, so it is the existing winning-value oracle.
    private static Listed? WinningListed(string json, PairConfig pair)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return DropboxEntries.Parse(document.RootElement, pair);
        }
        catch (Exception error) when (error is JsonException || DropboxEntries.IsShapeFailure(error))
        {
            return null;
        }
    }

    private static async Task MetadataMatrixAsync(PairConfig pair)
    {
        const string folder = "{\".tag\":\"folder\",\"id\":\"id:folder\",\"name\":\"Child\",\"path_display\":\"/Parent/Child\"}";
        const string tombstone = "{\".tag\":\"deleted\",\"name\":\"Child\",\"path_display\":\"/Parent/Child\"}";
        foreach (var json in new[]
        {
            FileJson(), FileJson(DropboxProtocol.IsDownloadable, "\"no\"", "false"), folder,
            folder.Insert(OneUnit, "\"id\":0,"), folder.Insert(folder.Length - 1, ",\"id\":0"), tombstone, "{}"
        })
        {
            await MetadataCaseAsync(json, pair);
        }
        foreach (var (name, valid, invalid) in MetadataFields)
        foreach (var (first, second) in new[] { (invalid, valid), (valid, invalid) })
        {
            var json = FileJson(name, first, second);
            Check.Require((WinningListed(json, pair) is null) == (second == invalid),
                "Each differential pair makes exactly its winning invalid value fail.");
            await MetadataCaseAsync(json, pair);
        }
        var untagged = FileJson().Replace("\".tag\":\"file\",", "", StringComparison.Ordinal);
        await DecodeCaseAsync(untagged, () => DropboxReply.Metadata(pair, DropboxProtocol.TagFile),
            value => value.Id == "id:file" && !value.IsFolder);
        await DecodeCaseAsync(untagged, () => DropboxReply.Metadata(pair), _ => false, valid: false);
    }

    private static async Task MetadataCaseAsync(string json, PairConfig pair)
    {
        var winner = WinningListed(json, pair);
        await DecodeCaseAsync(json, () => DropboxReply.Listed(pair), value => value == winner, winner is not null);
        await DecodeCaseAsync(json, () => DropboxReply.Metadata(pair), value => value == winner?.Entry,
            winner?.Entry is not null);
        await DecodeCaseAsync("{\"metadata\":null,\"metadata\":" + json + "}", () => DropboxReply.Metadata(pair, nested: true),
            value => value == winner?.Entry, winner?.Entry is not null);
        await DecodeCaseAsync("{\"metadata\":" + json + ",\"metadata\":0}", () => DropboxReply.Metadata(pair, nested: true),
            _ => false, valid: false);
    }

    private static async Task HistoryHeadMatrixAsync()
    {
        const string head = "{\"id\":\"id:file\",\"rev\":\"revision\",\"path_display\":\"/Parent/Child\"";
        foreach (var (optional, wrong) in new[]
        {
            ("", false), (",\"server_deleted\":null,\"content_hash\":null", false),
            (",\"server_deleted\":7,\"content_hash\":false", true)
        })
        {
            await DecodeCaseAsync("{\"is_deleted\":true,\"entries\":[" + head + optional + "},{\"id\":0}]}",
                DropboxReply.HistoryHead, value => value.IsDeleted && value.HasHead &&
                    value.Id == "id:file" && value.Revision == "revision" && value.NativePath == "/Parent/Child" &&
                    value.ServerDeleted is null && value.ContentHash is null &&
                    value.TimeWrongType == wrong && value.HashWrongType == wrong);
        }
        await DecodeCaseAsync(
            "{\"is_deleted\":0,\"is_deleted\":false,\"entries\":null,\"entries\":[" + head +
            ",\"server_deleted\":\"2026-01-01T00:00:00Z\",\"content_hash\":\"hash-fixture\"}]}",
            DropboxReply.HistoryHead, value => !value.IsDeleted && value.ServerDeleted == "2026-01-01T00:00:00Z" &&
                value.ContentHash == "hash-fixture" && !value.TimeWrongType && !value.HashWrongType);
        await DecodeCaseAsync("{\"is_deleted\":false,\"entries\":[]}", DropboxReply.HistoryHead,
            value => !value.IsDeleted && !value.HasHead);
        foreach (var invalid in new[]
        {
            "{}", "{\"entries\":[]}", "{\"is_deleted\":0,\"entries\":[]}", "{\"is_deleted\":false,\"entries\":null}",
            "{\"is_deleted\":false,\"entries\":[0]}", "{\"is_deleted\":false,\"entries\":[{\"id\":0}]}",
            "{\"is_deleted\":false,\"is_deleted\":0,\"entries\":[]}", "{\"is_deleted\":false,\"entries\":[{},tru]}"
        })
        {
            await DecodeCaseAsync(invalid, DropboxReply.HistoryHead, _ => false, valid: false);
        }
    }

    private static async Task FolderReplyMatrixAsync(PairConfig pair)
    {
        const string folder = "{\".tag\":\"folder\",\"id\":\"id:folder\",\"name\":\"Child\",\"path_display\":\"/Parent/Child\"}";
        await DecodeCaseAsync("{\".tag\":\"async_job_id\",\"async_job_id\":\"job-fixture\"}",
            () => DropboxReply.FolderSubmit(pair, StartLevel), value => value is FolderSubmitResult.Accepted { JobId: "job-fixture" });
        await DecodeCaseAsync("{\".tag\":\"in_progress\"}", () => DropboxReply.FolderJob(pair, StartLevel),
            value => value is FolderJobResult.InProgress);
        var complete = "{\".tag\":\"complete\",\"entries\":[{\".tag\":\"success\",\"metadata\":" + folder + "}]}";
        await DecodeCaseAsync(complete, () => DropboxReply.FolderSubmit(pair, StartLevel),
            value => value is FolderSubmitResult.Terminal terminal && terminal.Results.Count == 1 &&
                terminal.Results[0] is FolderMemberResult.Receipt { Ordinal: 0 });
        await DecodeCaseAsync(complete, () => DropboxReply.FolderJob(pair, StartLevel),
            value => value is FolderJobResult.Terminal terminal && terminal.Results.Count == 1 &&
                terminal.Results[0] is FolderMemberResult.Receipt { Ordinal: 0 });
        foreach (var member in new[]
        {
            "{\".tag\":\"failure\",\"failure\":{\".tag\":\"path_write\",\"path_write\":{\".tag\":\"no_write_permission\"}}}",
            "{}", "null", "{\".tag\":\"success\",\"metadata\":{}}"
        })
        {
            var json = "{\".tag\":\"complete\",\"entries\":[{\".tag\":\"success\",\"metadata\":" + folder + "}," + member + "]}";
            FolderMemberResult[] expected = [new FolderMemberResult.Receipt(0, null!),
                new FolderMemberResult.Unknown(StartLevel, member.Contains("no_write_permission", StringComparison.Ordinal)
                    ? TextKey.FailDropboxNoPermission : null)];
            bool Accepts(IReadOnlyList<FolderMemberResult> results) => results.Count == expected.Length &&
                results[0] is FolderMemberResult.Receipt { Ordinal: 0, Entry.IsFolder: true } && results[1] == expected[1];
            await DecodeCaseAsync(json, () => DropboxReply.FolderSubmit(pair, expected.Length),
                value => value is FolderSubmitResult.Terminal terminal && Accepts(terminal.Results));
            await DecodeCaseAsync(json, () => DropboxReply.FolderJob(pair, expected.Length),
                value => value is FolderJobResult.Terminal terminal && Accepts(terminal.Results));
        }
        foreach (var entries in new[] { "[]", "[{},{}]", "null" })
        {
            var json = "{\".tag\":\"complete\",\"entries\":" + entries + "}";
            await DecodeCaseAsync(json, () => DropboxReply.FolderSubmit(pair, StartLevel),
                value => value is FolderSubmitResult.Terminal terminal && terminal.Results.Count == 1 &&
                    terminal.Results[0] == new FolderMemberResult.Unknown(0, null));
            await DecodeCaseAsync(json, () => DropboxReply.FolderJob(pair, StartLevel),
                value => value is FolderJobResult.Terminal terminal && terminal.Results.Count == 1 &&
                    terminal.Results[0] == new FolderMemberResult.Unknown(0, null));
        }
        await DecodeCaseAsync("{\".tag\":\"failed\"}", () => DropboxReply.FolderJob(pair, StartLevel),
            value => value is FolderJobResult.Terminal terminal && terminal.Results.Count == 1 &&
                terminal.Results[0] == new FolderMemberResult.Unknown(0, null));
        foreach (var json in new[] { "{\".tag\":\"unknown\"}", "{}", "{\".tag\":null}" })
        {
            await DecodeCaseAsync(json, () => DropboxReply.FolderSubmit(pair, StartLevel), _ => false, valid: false);
            await DecodeCaseAsync(json, () => DropboxReply.FolderJob(pair, StartLevel), _ => false, valid: false);
        }
        await DecodeCaseAsync("{\".tag\":\"failed\"}", () => DropboxReply.FolderSubmit(pair, StartLevel), _ => false, valid: false);
    }

    private static async Task FinishBatchReplyMatrixAsync()
    {
        var success = FileJson().Replace("\"file\"", "\"success\"", StringComparison.Ordinal);
        foreach (var expectedIsNull in new[] { false, true })
        foreach (var (member, errorType, throttle) in new[]
        {
            ("{\".tag\":\"failure\",\"failure\":{\".tag\":\"path\",\"path\":{\".tag\":\"conflict\"}}}",
                expectedIsNull ? typeof(PathOccupiedException) : typeof(ChangedUnderneathException), false),
            ("{\".tag\":\"failure\",\"failure\":{\".tag\":\"too_many_write_operations\"}}", typeof(ItemFailedException), true),
            ("{\".tag\":\"failure\",\"failure\":{\".tag\":\"path\",\"path\":{\".tag\":\"no_write_permission\"}}}", typeof(ItemFailedException), false),
            ("{}", typeof(ItemFailedException), false), ("null", typeof(ItemFailedException), false),
            ("{\".tag\":\"success\",\"id\":false}", typeof(ItemFailedException), false)
        })
        {
            (string Path, bool ExpectedIsNull)[] targets = [(ArgumentPath, expectedIsNull), (ArgumentPath, expectedIsNull)];
            await DecodeCaseAsync("{\"entries\":[" + success + "," + member + "]}",
                () => DropboxReply.FinishBatch(TestPair(), targets), results => results.Count == targets.Length &&
                    results[0] is { Entry.Id: "id:file", Error: null, KnownNoEffect: false, WriteThrottled: false } &&
                    results[1].Entry is null && results[1].Error?.GetType() == errorType &&
                    !results[1].KnownNoEffect && results[1].WriteThrottled == throttle);
        }
        foreach (var json in new[] { "{}", "{\"entries\":[]}", "{\"entries\":[{},{}]}", "{\"entries\":null}" })
            await DecodeCaseAsync(json, () => DropboxReply.FinishBatch(TestPair(), [(ArgumentPath, true)]),
                _ => false, valid: false);
    }

    private static async Task ScalarMatrixAsync()
    {
        foreach (var invalid in new[] { "{}", "{\"cursor\":null}", "{\"cursor\":0}", "[]", "null", "\"next\"" })
        {
            await DecodeCaseAsync(invalid, DropboxReply.Cursor, _ => false, valid: false);
        }
        foreach (var invalid in new[] { "{}", "{\"session_id\":0}", "{\"session_id\":\"session-fixture\",\"session_id\":null}" })
        {
            await DecodeCaseAsync(invalid, DropboxReply.SessionId, _ => false, valid: false);
        }
        await DecodeCaseAsync("{\"changes\":true}", DropboxReply.Longpoll, value => value.Changes && value.Backoff is null);
        await DecodeCaseAsync("{\"changes\":true,\"backoff\":30}", DropboxReply.Longpoll,
            value => value.Changes && value.Backoff == TimeSpan.FromSeconds(IdleMinutes));
        foreach (var invalid in new[] { "{}", "{\"changes\":0}", "{\"changes\":false,\"changes\":\"bad\"}" })
        {
            await DecodeCaseAsync(invalid, DropboxReply.Longpoll, _ => false, valid: false);
        }
        await DecodeCaseAsync("{\"unknown\":[1,{\"nested\":null}]}", DropboxReply.Void, _ => true);
        const string info = "\"root_info\":{\"root_namespace_id\":\"root-fixture\",\"home_namespace_id\":\"home-fixture\"}";
        await DecodeCaseAsync("{\"account_id\":0,\"account_id\":\"account-fixture\"," + info + "}", DropboxReply.AccountRoot,
            value => value == ("account-fixture", "root-fixture", "home-fixture"));
        foreach (var invalid in new[]
        {
            "{" + info + "}", "{\"account_id\":\"\"," + info + "}", "{\"account_id\":\"account-fixture\"}",
            "{\"account_id\":\"account-fixture\",\"account_id\":0," + info + "}",
            "{\"account_id\":\"account-fixture\",\"root_info\":{\"root_namespace_id\":0,\"home_namespace_id\":\"home-fixture\"}}",
            "{\"account_id\":\"account-fixture\",\"root_info\":{\"root_namespace_id\":\"root-fixture\",\"home_namespace_id\":\"\"}}"
        })
        {
            await DecodeCaseAsync(invalid, DropboxReply.AccountRoot, _ => false, valid: false);
        }
    }

    private static string? WinningCursor(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(DropboxProtocol.Cursor, out var cursor) &&
                cursor.ValueKind == JsonValueKind.String ? cursor.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task LexicalMatrixAsync()
    {
        var values = new[]
        {
            "\"\\\"\\\\\\/\\b\\f\\n\\r\\t\\u0041\"", "\"\\uD83D\\uDE00\"", "\"\u00e9-\u6587\U0001F600\"",
            "0", "-0", "0.5", "-1.25e+7", "1E-2", "123456789012345678901234567890",
            "true", "false", "null", "[]", "{}", "[{\"a\":[1,{\"b\":null}]}]",
            new string('[', LexicalDepth) + new string(']', LexicalDepth), new string('[', LexicalDepthLimit) + new string(']', LexicalDepthLimit),
            "\"\\q\"", "\"\\u12\"", "\"\\uZZZZ\"", "\"unterminated", "\"raw\ncontrol\"",
            "01", "1.", ".5", "1e", "1e+", "-", "+1", "tru", "truex", "nul",
            "[1,]", "{\"a\":}", "{\"a\":1,}", "[", "{", "'text'", "/*comment*/1"
        };
        var documents = values.SelectMany(value => new[]
        {
            "{\"unknown\":" + value + ",\"cursor\":\"next\"}", "{\"cursor\":\"next\",\"unknown\":" + value + "}"
        }).Concat(new[]
        {
            "{\"curs\\u006fr\":\"next\"}", "{\"Cursor\":\"next\"}", "{\"cursorx\":\"next\"}", "{\"\":\"next\"}",
            "{\"cursor\":\"old\",\"curs\\u006fr\":\"next\"}", "{\"\\u0063ursor\":0,\"cursor\":\"next\"}",
            "{\"cursor\":\"next\"} trailing", " \r\n\t{\"cursor\":\"next\"}\r\n ", "{\"cursor\":\"next\",}"
        });
        foreach (var json in documents)
        {
            var winner = WinningCursor(json);
            await DecodeCaseAsync(json, DropboxReply.Cursor, cursor => cursor == winner, winner is not null);
        }
        // JsonDocument leaves these unknown-string bytes unvalidated; READING 8.3 requires their rejection.
        foreach (var sequence in new byte[][]
        {
            "\\uD83D"u8.ToArray(), "\\uDE00"u8.ToArray(), [InvalidUtf8Lead, InvalidUtf8Tail], [OverlongUtf8Lead, Utf8Continuation], [SurrogateUtf8Lead, SurrogateUtf8Tail, Utf8Continuation], [OutOfRangeUtf8Lead, OutOfRangeUtf8Tail, Utf8Continuation, Utf8Continuation]
        })
        {
            var bytes = "{\"unknown\":\""u8.ToArray().Concat(sequence).Concat("\",\"cursor\":\"next\"}"u8.ToArray()).ToArray();
            await DecodeBytesAsync(bytes, DropboxReply.Cursor, _ => false, valid: false);
        }
    }

    private static Task DecodeCaseAsync<T>(string json, Func<DropboxReply<T>> decoder,
        Func<T, bool> accepts, bool valid = true) =>
        DecodeBytesAsync(Encoding.UTF8.GetBytes(json), decoder, accepts, valid);

    private static async Task DecodeBytesAsync<T>(byte[] json, Func<DropboxReply<T>> decoder,
        Func<T, bool> accepts, bool valid)
    {
        // Every byte boundary is exercised without replacing the production lexical decoder.
        using var input = new ChunkedStream(json, OneUnit);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(input)
        }));
        using var http = new HttpClient(handler);
        var attempt = new RequestAttempt(new object(), AttemptOwner.Recovery);
        try
        {
            var value = await Create(http, new Tokens()).RpcAsync(DropboxProtocol.GetMetadata,
                new { path = ArgumentPath }, decoder(), attempt, default);
            Check.Require(valid && accepts(value), "The typed decoder must preserve its complete winning shape.");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            Check.Require(!valid && attempt.Completion?.Result != RequestResult.Success,
                "Malformed required structure cannot publish a typed success.");
        }
        Check.Require(handler.Requests.Count == 1, "Recovery decoding does not create an inner resend loop.");
    }

    private static async Task RequestRetryAsync()
    {
        var calls = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            Check.Require(await request.Content!.ReadAsStringAsync(ct) == "{\"cursor\":\"prior\"}",
                "A logical Request retry retains its exact input cursor.");
            var failed = ++calls == 1;
            var response = new HttpResponseMessage(failed ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            {
                Content = new StringContent(failed ? ErrorJson : "{\"cursor\":\"next\"}")
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        using var http = new HttpClient(handler);
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        var cursor = await Create(http, new Tokens()).RpcAsync(DropboxProtocol.ListFolderContinue,
            new { cursor = "prior" }, DropboxReply.Cursor(), attempt, default);
        Check.Require(cursor == "next" && calls == ReplacementConnections && attempt.Completion?.Result == RequestResult.Success &&
            attempt.TryClaimCompletion() && !attempt.TryClaimCompletion(),
            "Only Request owns a safe internal retry; its outer completion is published and claimed once.");
    }

    private static async Task RepeatedFailuresAsync()
    {
        foreach (var owner in new[] { AttemptOwner.Prerequisite, AttemptOwner.Recovery, AttemptOwner.Listener })
        {
            var failure = new HttpRequestException("read-fixture");
            using var handler = new Handler((_, _) => Task.FromException<HttpResponseMessage>(failure));
            using var http = new HttpClient(handler);
            var api = Create(http, new Tokens());
            var operation = new object();
            // Three observations establish persistent failure without prescribing a runtime attempt limit.
            for (var index = 0; index < RepeatedFailureCount; index++)
            {
                var attempt = new RequestAttempt(operation, owner);
                try
                {
                    await api.RpcAsync(DropboxProtocol.ListFolderContinue, new { cursor = "prior" },
                        DropboxReply.Cursor(), attempt, default);
                    Check.Require(false, "A failed finite read must reach its enclosing owner.");
                }
                catch (HttpRequestException error)
                {
                    Check.Require(ReferenceEquals(error, failure) && attempt.Completion is
                        { Result: RequestResult.TransientFailure, NoEffectProven: true } &&
                        attempt.Owner == owner && ReferenceEquals(attempt.Operation, operation) &&
                        attempt.TryClaimCompletion() && !attempt.TryClaimCompletion(),
                        "Repeated failures retain exact operation/owner identity and one completion claim.");
                }
                Check.Require(handler.Requests.Count == index + 1,
                    "Persistent failure never starts an inner request loop or a root listing.");
            }
        }
    }

    private static async Task ErrorBoundAsync()
    {
        foreach (var advertised in new[] { false, true })
        foreach (var extra in new[] { 0, OneUnit })
        {
            var bytes = Encoding.UTF8.GetBytes(new string(' ', DropboxApiLimits.ErrorBodyMaxBytes + extra));
            using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StreamContent(new UnknownLengthStream(bytes))
            };
            if (advertised) response.Content.Headers.ContentLength = bytes.Length;
            else response.Content.Headers.ContentLength = null;
            Check.Require(advertised || response.Content.Headers.ContentLength is null,
                "The streaming-cap branch has no computed length and must count bytes read.");
            using var handler = new Handler((_, _) => Task.FromResult(response));
            using var http = new HttpClient(handler);
            var attempt = new RequestAttempt(new object(), AttemptOwner.Recovery);
            var error = await FailureOf(Create(http, new Tokens()).RpcAsync(DropboxProtocol.GetMetadata,
                new { }, DropboxReply.Void(), attempt, default));
            Check.Require(extra == 0 ? error is DropboxApiException : error is InvalidDataException,
                "The exact byte boundary permits the malformed fallback; an excess byte fails even without length.");
            Check.Require(handler.Requests.Count == 1 && ReferenceEquals(attempt.Completion?.Error, error),
                "The bounded refusal completes once without replaying disposed content.");
        }
    }

    private static async Task PoolReuseAsync()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = Massdex.SyncShark.V2.EngineComposition.CreateDropboxHttpClient(
            TimeSpan.FromSeconds(OrdinarySeconds), DecompressionMethods.None, new FakeTimeProvider());
        var server = ServeAsync();
        try
        {
            foreach (var expected in new[] { "first", "second" })
            {
                using var response = await client.GetAsync("http://127.0.0.1:" + port + "/", stop.Token);
                Check.Require(await response.Content.ReadAsStringAsync(stop.Token) == expected,
                    "A closed pooled connection is discarded; the next guarded request succeeds.");
            }
            await server;
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        async Task ServeAsync()
        {
            foreach (var body in new[] { "first", "second" })
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + body.Length +
                    "\r\nConnection: close\r\n\r\n" + body);
                await stream.WriteAsync(bytes, stop.Token);
            }
        }
    }

    private static async Task MutationReplyFailureAsync()
    {
        foreach (var reply in new[] { "{", "{} trailing" })
        foreach (var route in new[] { DropboxProtocol.Upload, DropboxProtocol.UploadSessionStart,
            DropboxProtocol.UploadSessionAppend, DropboxProtocol.UploadSessionFinish })
        {
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply)
            }));
            using var http = new HttpClient(handler);
            using var body = new MemoryStream(Encoding.UTF8.GetBytes(Payload));
            var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
            try
            {
                await Create(http, new Tokens()).UploadAsync(route, new { path = ArgumentPath }, body,
                    DropboxReply.Void(), attempt, default);
                Check.Require(false, "Successful headers cannot validate a malformed mutation receipt.");
            }
            catch (JsonException error)
            {
                Check.Require(attempt.Completion is { Result: RequestResult.UnknownWrite, NoEffectProven: false } result &&
                    ReferenceEquals(result.Error, error), "Malformed receipts preserve unknown-effect custody and the error.");
            }
            Check.Require(handler.Requests.Count == 1 && body.CanRead,
                "Malformed mutation receipts cannot trigger replay or dispose the caller stream.");
        }
    }

    private static async Task TypedScalarsAsync()
    {
        foreach (var json in new[]
        {
            "{\"session_id\":0,\"session_id\":\"session-fixture\"}",
            "{\"session_id\":\"session-fixture\",\"unknown\":{\"value\":null}}"
        })
        {
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            }));
            using var http = new HttpClient(handler);
            using var body = new MemoryStream();
            var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
            var session = await Create(http, new Tokens()).UploadAsync(DropboxProtocol.UploadSessionStart,
                new { close = true }, body, DropboxReply.SessionId(), attempt, default);
            Check.Require(session == "session-fixture" && attempt.Completion?.Result == RequestResult.Success,
                "Session IDs are typed winning values, not whole response documents.");
        }
        foreach (var json in new[] { "{\"changes\":false,\"backoff\":0}",
            "{\"changes\":\"bad\",\"changes\":false,\"backoff\":0}" })
        {
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            }));
            using var http = new HttpClient(handler);
            var result = await Create(http, new Tokens()).RpcAsync(DropboxProtocol.ListFolderLongpoll,
                new { cursor = "prior", timeout = OrdinarySeconds }, DropboxReply.Longpoll(),
                new RequestAttempt(new object(), AttemptOwner.Listener), default);
            Check.Require(!result.Changes && result.Backoff == TimeSpan.Zero,
                "A valid no-change longpoll and explicit zero backoff survive typed decoding.");
        }
    }

    private static void GuardedPools()
    {
        using var defaults = new SocketsHttpHandler();
        foreach (var requestedSeconds in new[] { OrdinarySeconds, DropboxApiLimits.LongpollMaxSeconds })
        {
            var requested = TimeSpan.FromSeconds(requestedSeconds);
            var settings = Massdex.SyncShark.V2.V2Settings.Default with { DropboxLongpollTimeout = requested };
            var allowance = Massdex.SyncShark.V2.EngineComposition.LongpollTimeoutFor(settings);
            Check.Require(allowance == requested + TimeSpan.FromSeconds(LongpollHeadroomSeconds),
                "Reconstruction binds the immutable requested longpoll setting plus its documented headroom.");
            using var ordinary = Massdex.SyncShark.V2.EngineComposition.CreateDropboxHttpClient(
                TimeSpan.FromSeconds(OrdinarySeconds), DecompressionMethods.None, new FakeTimeProvider());
            using var longpoll = Massdex.SyncShark.V2.EngineComposition.CreateDropboxHttpClient(
                allowance, DecompressionMethods.None, new FakeTimeProvider());
            Check.Require(ordinary.Timeout == Timeout.InfiniteTimeSpan &&
                longpoll.Timeout == Timeout.InfiniteTimeSpan && !ReferenceEquals(ordinary, longpoll) &&
                ordinary.DefaultRequestVersion == HttpVersion.Version11 &&
                longpoll.DefaultVersionPolicy == HttpVersionPolicy.RequestVersionExact,
                "Separate guarded pools use activity deadlines, never a total-request timeout.");
            Check.Require(HandlerOf(ordinary).PooledConnectionIdleTimeout == defaults.PooledConnectionIdleTimeout &&
                HandlerOf(longpoll).PooledConnectionIdleTimeout == defaults.PooledConnectionIdleTimeout,
                "The guarded factory keeps the default pool idle timeout.");
        }
    }

    private static async Task UnpublishedPageAsync()
    {
        var pair = TestPair();
        foreach (var cancel in new[] { false, true })
        {
            using var stop = new CancellationTokenSource();
            var spool = new SpoolStream();
            using var input = new GatedEofStream(Encoding.UTF8.GetBytes(
                "{\"entries\":[],\"cursor\":\"next\",\"has_more\":false}"));
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(input)
            }));
            using var http = new HttpClient(handler);
            var attempt = new RequestAttempt(new object(), AttemptOwner.Prerequisite);
            var pending = Create(http, new Tokens(), spool: _ => ValueTask.FromResult<Stream>(spool)).ReadPageAsync(
                DropboxProtocol.ListFolderContinue, new { cursor = "prior" }, pair, attempt, stop.Token);
            await input.Waiting.Task;
            Check.Require(!pending.IsCompleted && attempt.Completion is null,
                "Even a syntactically closed page cannot escape before response EOF.");
            if (cancel) stop.Cancel();
            else input.Release.TrySetResult();
            try
            {
                await using var page = await pending;
                Check.Require(!cancel && page.Cursor == "next" && !page.ReadEntries().Any(),
                    "Only successful EOF seals and publishes the page.");
            }
            catch (OperationCanceledException)
            {
                Check.Require(cancel && attempt.Completion?.Result == RequestResult.CallerStopped,
                    "Caller cancellation aborts the unpublished page, not a new failed Repair.");
            }
            Check.Require(input.Disposed && spool.Disposed && handler.Requests.Count == 1,
                "Completion and cancellation dispose the response and spool without resending.");
        }
    }

    private static async Task UnknownValuesAsync()
    {
        // Cross the retired success cap; it is a regression fixture size, not a provider limit.
        var unknown = new string('x', RetiredSuccessCapBytes + 1);
        var json = "{\"" + unknown + "\":\"" + unknown + "\",\"unknown\":1" +
            new string('0', HugeNumberDigits) + ",\"nested\":[{\"escaped\":\"\\\\\\\"\\u0041\"}],\"cursor\":\"next\"}";
        foreach (var chunk in new[] { OneUnit, SplitChunkBytes, LargeChunkBytes })
        {
            // The byte-at-a-time fixture covers escape boundaries without repeating the huge allocation/read loop.
            var text = chunk == LargeChunkBytes ? json : "{\"unknown\":{\"escaped\":\"\\\\\\\"\\u0041\"},\"cursor\":\"next\"}";
            using var input = new ChunkedStream(Encoding.UTF8.GetBytes(text), chunk);
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(input)
            }));
            using var http = new HttpClient(handler);
            var cursor = await Create(http, new Tokens()).RpcAsync(DropboxProtocol.ListFolderContinue,
                new { cursor = "prior" }, DropboxReply.Cursor(),
                new RequestAttempt(new object(), AttemptOwner.Prerequisite), default);
            Check.Require(cursor == "next", "Unknown names, strings, numbers and nested values do not impose a success cap.");
        }
    }

    private static async Task CompleteRepliesAsync()
    {
        foreach (var (json, succeeds) in new[]
        {
            ("{\"cursor\":0,\"cursor\":\"next\"}", true),
            ("{\"cursor\":\"next\",\"cursor\":0}", false),
            ("{\"cursor\":\"old\",\"cursor\":\"next\"}", true),
            ("{\"cursor\":\"next\"} trailing", false),
            ("{\"cursor\":\"next\",\"unknown\":\"\\q\"}", false),
            ("{\"cursor\":\"next\",\"unknown\":[{\"value\":true},null]}", true)
        })
        {
            using var content = new ObservedStream(Encoding.UTF8.GetBytes(json));
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(content)
            }));
            using var http = new HttpClient(handler);
            var attempt = new RequestAttempt(new object(), AttemptOwner.Prerequisite);
            try
            {
                var cursor = await Create(http, new Tokens()).RpcAsync(DropboxProtocol.ListFolderContinue,
                    new { cursor = "prior" }, DropboxReply.Cursor(), attempt, default);
                Check.Require(succeeds && cursor == "next" && attempt.Completion?.Result == RequestResult.Success,
                    "Only the complete winning cursor is returned.");
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                Check.Require(!succeeds && attempt.Completion?.Result != RequestResult.Success,
                    "Malformed syntax or the winning invalid type cannot complete successfully.");
            }
            Check.Require(content.Disposed && handler.Requests.Count == 1,
                "Complete-response validation disposes transport without an inner prerequisite retry.");
        }
    }

    private static async Task AccountRootAsync()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Check.Require(request.RequestUri!.AbsolutePath == VersionPath + DropboxProtocol.GetCurrentAccount &&
                !request.Headers.Contains(DropboxProtocol.PathRootHeader) &&
                await request.Content!.ReadAsStringAsync(ct) == "null",
                "Account identity is requested fresh with JSON null and without a file namespace header.");
            Check.Require(request.Version == HttpVersion.Version11 &&
                request.VersionPolicy == HttpVersionPolicy.RequestVersionExact,
                "The sender uses the guarded HTTP/1.1 contract.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"account_id\":\"account-fixture\",\"root_info\":{\"root_namespace_id\":\"root-fixture\",\"home_namespace_id\":\"home-fixture\"}}")
            };
        });
        using var http = new HttpClient(handler);
        var api = Create(http, new Tokens());
        foreach (var owner in new[] { AttemptOwner.Request, AttemptOwner.Prerequisite })
        {
            var actual = await api.RpcAsync(DropboxProtocol.GetCurrentAccount, null!, DropboxReply.AccountRoot(),
                new RequestAttempt(new object(), owner), default);
            Check.Require(actual == ("account-fixture", "root-fixture", "home-fixture"),
                "All account-root identity fields reach the configured-root policy unchanged.");
        }
        Check.Require(handler.Requests.Count == ReplacementConnections, "Root acquisition is fresh, without metadata or listing substitutes.");
    }

    private static (Listed[] Entries, string Cursor, bool HasMore)? WinningPage(string json, PairConfig pair)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return (root.GetProperty(DropboxProtocol.Entries).EnumerateArray()
                .Select(item => DropboxEntries.Parse(item, pair)).ToArray(),
                DropboxEntries.RequiredString(root, DropboxProtocol.Cursor),
                root.GetProperty(DropboxProtocol.HasMore).GetBoolean());
        }
        catch (Exception error) when (error is JsonException || DropboxEntries.IsShapeFailure(error))
        {
            return null;
        }
    }

    private static async Task SealedPagesAsync()
    {
        var pair = TestPair();
        const string tombstone = "{\".tag\":\"deleted\",\"name\":\"Child\",\"path_display\":\"/Parent/Child\"}";
        var ordered = "[" + FileJson() + "," + tombstone + "," + FileJson() + "]";
        foreach (var json in new[]
        {
            "{\"entries\":[{}],\"entries\":[],\"cursor\":\"next\",\"has_more\":false}",
            "{\"entries\":[],\"entries\":[{}],\"cursor\":\"next\",\"has_more\":false}",
            "{\"entries\":null,\"entries\":[],\"cursor\":0,\"cursor\":\"next\",\"has_more\":\"bad\",\"has_more\":false}",
            "{\"entries\":[],\"entries\":null,\"cursor\":\"next\",\"has_more\":false}",
            "{\"entries\":[],\"cursor\":\"next\",\"cursor\":0,\"has_more\":false}",
            "{\"entries\":[],\"cursor\":\"next\",\"has_more\":false,\"has_more\":\"bad\"}",
            "{\"entries\":[tru],\"entries\":[],\"cursor\":\"next\",\"has_more\":false}",
            "{\"entries\":[],\"cursor\":\"next\",\"has_more\":false}x",
            "{\"entries\":" + ordered + ",\"cursor\":\"next\",\"has_more\":true}",
            "{\"has_more\":true,\"cursor\":\"next\",\"entries\":" + ordered + "}",
            "{\"cursor\":\"next\",\"entries\":" + ordered + ",\"has_more\":false,\"unknown\":[{}]}"
        })
        {
            var winner = WinningPage(json, pair);
            using var input = new ChunkedStream(Encoding.UTF8.GetBytes(json), OneUnit);
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(input)
            }));
            using var http = new HttpClient(handler);
            var spool = new SpoolStream();
            var attempt = new RequestAttempt(new object(), AttemptOwner.Prerequisite);
            try
            {
                await using var page = await Create(http, new Tokens(), spool: _ => ValueTask.FromResult<Stream>(spool))
                    .ReadPageAsync(DropboxProtocol.ListFolderContinue, new { cursor = "prior" }, pair, attempt, default);
                Check.Require(winner is { } expected && page.Cursor == expected.Cursor && page.HasMore == expected.HasMore &&
                    page.ReadEntries().SequenceEqual(expected.Entries),
                    "A sealed page replays only its winning generation, in provider order, keeping repeated events.");
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                Check.Require(winner is null && attempt.Completion?.Result != RequestResult.Success,
                    "An invalid winning generation or trailing syntax never publishes a page.");
            }
            Check.Require(spool.Disposed && handler.Requests.Count == 1,
                "A failed or consumed page disposes its spool after one finite prerequisite attempt.");
        }
    }

    private static Task FinishExcessMemberSkipsHugeSelectedScalars() => ExcessMemberAsync(false);
    private static Task FolderExcessMemberSkipsHugeSelectedScalars() => ExcessMemberAsync(true);

    private static async Task ExcessMemberAsync(bool folder)
    {
        // A skipped selected scalar is larger than the provider's existing error-body ceiling.
        foreach (var size in new[] { DropboxApiLimits.ErrorBodyMaxBytes, ContentBlockHasher.DropboxBlockSize })
        {
            var scalar = new string('x', size);
            var json = "{\".tag\":\"complete\",\"entries\":[{}, {\".tag\":\"" + scalar +
                "\",\"id\":\"" + scalar + "\",\"metadata\":{\"id\":\"" + scalar +
                "\"},\"failure\":{\".tag\":\"" + scalar + "\"}}]}";
            var input = new AllocationStream(Encoding.UTF8.GetBytes(json));
            using var handler = new Handler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(input) }));
            using var http = new HttpClient(handler);
            var api = Create(http, new Tokens());
            var attempt = new RequestAttempt(new object(), AttemptOwner.Recovery);
            if (folder)
            {
                var result = await api.RpcAsync(DropboxProtocol.GetMetadata, new { },
                    DropboxReply.FolderJob(TestPair(), StartLevel), attempt, default);
                Check.Require(result is FolderJobResult.Terminal terminal && terminal.Results.Count == 1 &&
                    terminal.Results[0] is FolderMemberResult.Unknown, "Excess folder members invalidate attribution.");
            }
            else
            {
                Check.Require(await FailureOf(api.RpcAsync(DropboxProtocol.GetMetadata, new { },
                    DropboxReply.FinishBatch(TestPair(), [(ArgumentPath, true)]), attempt, default)) is JsonException,
                    "An excess finish member invalidates outer cardinality.");
            }
            Check.Require(input.Allocated < size && input.Retained < size,
                "Both allocation traffic and live retained growth at EOF stay below one excess scalar at each size.");
        }
    }

    private static async Task PageHasNoRetentionCountSentinel()
    {
        var entries = Enumerable.Repeat(FileJson(), DropboxApiLimits.BatchMaxEntries + 1).ToArray();
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"entries\":[" + string.Join(",", entries) + "],\"cursor\":\"next\",\"has_more\":false}")
        }));
        using var http = new HttpClient(handler);
        var spool = new SpoolStream();
        await using var page = await Create(http, new Tokens(), spool: _ => ValueTask.FromResult<Stream>(spool))
            .ReadPageAsync(DropboxProtocol.ListFolder, new { }, TestPair(),
                new RequestAttempt(new object(), AttemptOwner.Prerequisite), default);
        var replayed = page.ReadEntries().ToArray();
        Check.Require(replayed.Length == entries.Length && replayed.All(item => item.Entry?.Id == "id:file"),
            "Streaming page entries never acquire the bounded batch projection's excess-count sentinel.");
    }

    private static async Task UnknownScopeWaitGatesWholeAccount()
    {
        var wait = TimeSpan.FromSeconds(ProviderWaitSeconds);
        foreach (var (status, body) in new[]
        {
            (HttpStatusCode.OK, "{}"), (HttpStatusCode.TooManyRequests, ErrorJson),
            (HttpStatusCode.ServiceUnavailable, ErrorJson), (HttpStatusCode.TooManyRequests, WriteThrottleJson)
        })
        {
            var clock = new ObservedClock();
            var account = Guid.NewGuid().ToString();
            using var handler = new Handler((_, _) =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var api = Create(http, new Tokens(), clock, account: account, level: AdaptiveLimiter.GrowthFactor);
            await FailureOf(api.RpcAsync(DropboxProtocol.GetMetadata, new { }, DropboxReply.Void(),
                new RequestAttempt(new object(), AttemptOwner.Recovery), default));
            Check.Require(api.Limiter.Level == (body == WriteThrottleJson || status == HttpStatusCode.OK
                ? AdaptiveLimiter.GrowthFactor : StartLevel), "Write contention alone never halves request admission.");
            using var nextHandler = new Handler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
            using var nextHttp = new HttpClient(nextHandler);
            var next = Create(nextHttp, new Tokens(), clock, account: account);
            var pending = new List<Task>();
            foreach (var route in DropboxRouteTable.Embedded.Routes)
            {
                var identity = next.AdmissionIdentity(route.Name);
                Check.Require(FailureRetryOwner.RemainingProviderConstraint(identity, clock) == wait,
                    "An HTTP wait gates every registered route on another source of the same account.");
            }
            foreach (var owner in Enum.GetValues<AttemptOwner>())
                pending.Add(next.RpcAsync(DropboxProtocol.GetMetadata, new { }, DropboxReply.Void(),
                    new RequestAttempt(new object(), owner), default));
            Check.Require(nextHandler.Requests.Count == 0 && pending.All(task => !task.IsCompleted),
                "All five owners are gated at the actual send, without a caller floor lookup.");
            var other = Create(nextHttp, new Tokens(), clock);
            await other.RpcAsync(DropboxProtocol.GetMetadata, new { }, DropboxReply.Void(),
                new RequestAttempt(new object(), AttemptOwner.Recovery), default);
            Check.Require(nextHandler.Requests.Count == 1, "Another account sends immediately.");
            clock.Advance(wait - OneSecond);
            Check.Require(nextHandler.Requests.Count == 1 && pending.All(task => !task.IsCompleted),
                "No owner can send before the provider floor expires.");
            clock.Advance(OneSecond);
            await Within(Task.WhenAll(pending));
            Check.Require(nextHandler.Requests.Count == pending.Count + 1, "Each gated attempt sends once at expiry.");
        }
    }

    private static async Task HeaderBodyTakesLonger()
    {
        var shorter = TimeSpan.FromSeconds(HeaderWaitSeconds);
        var longer = TimeSpan.FromSeconds(ProviderWaitSeconds);
        foreach (var (header, body, expected) in new (TimeSpan, TimeSpan?, TimeSpan)[]
        {
            (shorter, longer, longer), (longer, shorter, longer),
            (TimeSpan.Zero, longer, longer), (TimeSpan.Zero, null, TimeSpan.Zero)
        })
        {
            var clock = new ObservedClock();
            var json = body is { } supplied
                ? JsonSerializer.Serialize(new { error = new { retry_after = (long)supplied.TotalSeconds } }) : ErrorJson;
            using var handler = new Handler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(json) };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(header);
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var api = Create(http, new Tokens(), clock);
            var attempt = new RequestAttempt(new object(), AttemptOwner.Recovery);
            await FailureOf(api.RpcAsync(DropboxProtocol.GetMetadata, new { }, DropboxReply.Void(), attempt, default));
            Check.Require(attempt.Completion?.ProviderWait == expected &&
                attempt.ProviderWaitStartedTimestamp == clock.GetTimestamp(), "The longer supplied wait retains its anchor.");
            Check.Require(FailureRetryOwner.RemainingProviderConstraint(api.AdmissionIdentity(DropboxProtocol.GetMetadata), clock)
                == (expected == TimeSpan.Zero ? null : expected), "Explicit zero is supplied but installs no floor.");
        }
    }

    private static void ObservationNeverShorter()
    {
        var clock = new ObservedClock();
        var anchor = clock.GetTimestamp();
        clock.Advance(OneSecond);
        Check.Require(FailureRetryOwner.RemainingProviderObservation(PoolReadAllowance, anchor, clock) == PoolReadAllowance - OneSecond &&
            FailureRetryOwner.RemainingProviderObservation(PoolReadAllowance, null, clock) == PoolReadAllowance,
            "Slow cleanup consumes elapsed time, while an absent anchor never shortens a supplied observation.");
        clock.Advance(PoolReadAllowance);
        Check.Require(FailureRetryOwner.RemainingProviderObservation(PoolReadAllowance, anchor, clock) == TimeSpan.Zero &&
            FailureRetryOwner.RemainingProviderObservation(null, null, clock) is null,
            "An expired supplied wait remains zero rather than becoming an absent observation.");
    }

    private static async Task LongpollAnchorSurvivesTransportCleanup()
    {
        foreach (var throws in new[] { false, true })
        {
            var clock = new ObservedClock();
            var cleanup = new IOException(nameof(LongpollAnchorSurvivesTransportCleanup));
            var input = new CleanupStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new { changes = true, backoff = (long)PoolReadAllowance.TotalSeconds })));
            input.BeforeDispose = () => clock.Advance(OneSecond);
            input.DisposeFailure = throws ? cleanup : null;
            var response = new CleanupResponse(HttpStatusCode.OK) { Content = new StreamContent(input), Failure = throws ? cleanup : null };
            using var handler = new Handler((_, _) => Task.FromResult<HttpResponseMessage>(response));
            using var http = new HttpClient(handler);
            var feedback = new List<WaitEvent>();
            var api = Create(http, new Tokens(), clock, waited: feedback.Add);
            var attempt = new RequestAttempt(new object(), AttemptOwner.Listener);
            var anchor = clock.GetTimestamp();
            var pending = api.LongpollAsync("prior", attempt, default);
            var error = await FailureOf(pending);
            Check.Require(throws ? Contains(error, cleanup) && attempt.Completion?.Result == RequestResult.PermanentFailure
                : error is null && await pending && attempt.Completion?.Result == RequestResult.Success,
                "Accepted longpoll results survive cleanup, but cleanup errors cannot report success.");
            Check.Require(FailureRetryOwner.RemainingProviderConstraint(api.AdmissionIdentity(DropboxProtocol.ListFolderLongpoll), clock)
                == PoolReadAllowance - clock.GetElapsedTime(anchor) &&
                FailureRetryOwner.RemainingProviderConstraint(api.AdmissionIdentity(DropboxProtocol.ListFolderContinue), clock) is null &&
                feedback.Count == 0 && handler.Requests.Count == 1,
                "The original decode anchor remains scoped to the next longpoll, without duplicate feedback.");
        }
    }

    private static Task BodyOnlyRateWaitSurvivesAbortFailure() => BodyWaitCleanupAsync(true);
    private static Task BodyOnlyRateWaitSurvivesOriginalErrorContentDisposeFailure() => BodyWaitCleanupAsync(false);

    private static async Task BodyWaitCleanupAsync(bool abort)
    {
        var clock = new ObservedClock();
        var cleanup = new IOException(nameof(BodyWaitCleanupAsync));
        var content = new CleanupContent(JsonSerializer.Serialize(new { error = new { retry_after = (long)PoolReadAllowance.TotalSeconds } }))
        { Failure = abort ? null : cleanup, BeforeDispose = () => clock.Advance(OneSecond) };
        using var handler = new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = content }));
        using var http = new HttpClient(handler);
        var feedback = new List<WaitEvent>();
        var api = Create(http, new Tokens(), clock, waited: feedback.Add);
        var reply = abort ? new DropboxReply<ValueTuple>(null, (_, _) => ValueTask.FromResult(default(ValueTuple)),
            abort: () => ValueTask.FromException(cleanup)) : DropboxReply.Void();
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        var anchor = clock.GetTimestamp();
        var error = await FailureOf(api.RpcAsync(DropboxProtocol.GetMetadata, new { }, reply, attempt, default));
        var refusal = Errors(error).OfType<DropboxApiException>().Single();
        Check.Require(Contains(error, cleanup) && DropboxApi.Failure(refusal).GetProperty(DropboxProtocol.RetryAfter).GetInt64()
                == (long)PoolReadAllowance.TotalSeconds && attempt.Completion?.Result == RequestResult.PermanentFailure &&
            attempt.Completion?.ProviderWait == PoolReadAllowance && attempt.ProviderWaitStartedTimestamp == anchor &&
            FailureRetryOwner.RemainingProviderConstraint(api.AdmissionIdentity(DropboxProtocol.GetMetadata), clock)
                == PoolReadAllowance - OneSecond && feedback.Count == 1 && handler.Requests.Count == 1,
            "Body-only refusal facts and their original floor survive cleanup failure without retry or duplicate feedback.");
    }

    private static Task EarlyRefusalJoinsBeforeOriginalErrorBodyDispose() => EarlyCleanupAsync(false, false, false, true);
    private static Task EarlyRefusalJoinsBeforeResponseDispose() => EarlyCleanupAsync(false, true, false);
    private static Task MalformedReplyAndThrowingResponseDisposePreserveBothErrors() => EarlyCleanupAsync(true, true, false);
    private static Task StopCallbackFailureStillJoinsAndCompletesAttempt() => EarlyCleanupAsync(false, false, true);

    private static async Task EarlyCleanupAsync(bool malformed, bool throwsOnDispose, bool throwsOnStop, bool throwsOnInput = false)
    {
        var cleanup = new IOException(nameof(EarlyCleanupAsync));
        using var source = new JoiningUpload(Encoding.UTF8.GetBytes(Payload)) { StopFailure = throwsOnStop ? cleanup : null };
        var input = new CleanupStream(Encoding.UTF8.GetBytes(malformed ? "{" :
            "{\"error\":{\".tag\":\"path\",\"reason\":{\".tag\":\"no_write_permission\"}}}"))
            { DisposeFailure = throwsOnInput ? cleanup : null };
        input.BeforeDispose = () => Check.Require(source.Exited, "Original input disposal follows the serializer join.");
        var content = new CleanupContent(input)
        {
            BeforeDispose = () => Check.Require(source.Exited, "Original error content remains alive until the serializer joins.")
        };
        var response = new CleanupResponse(malformed ? HttpStatusCode.OK : HttpStatusCode.Conflict)
        {
            Content = content, Failure = throwsOnDispose ? cleanup : null,
            BeforeDispose = () => Check.Require(source.Exited, "Response disposal follows upload join.")
        };
        Task copying = Task.CompletedTask;
        using var handler = new Handler(async (request, ct) =>
        {
            copying = request.Content!.CopyToAsync(Stream.Null, ct);
            await source.Started.Task;
            return response;
        });
        using var http = new HttpClient(handler);
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        var pending = Create(http, new Tokens()).UploadAsync(DropboxProtocol.Upload, new { }, source,
            DropboxReply.Void(), attempt, default);
        await Within(source.Stopped.Task);
        try
        {
            Check.Require(!pending.IsCompleted && attempt.Completion is null && input.AsyncDisposals == 0 &&
                content.Disposals == 0 && response.Disposals == 0,
                "A stopped but unjoined serializer keeps original input/content/response alive and completion unpublished.");
        }
        finally { source.Release.TrySetResult(); }
        var error = await FailureOf(pending);
        await FailureOf(copying);
        Check.Require(Errors(error).Any(failure => malformed ? failure is JsonException : failure is DropboxApiException refusal &&
                DropboxApi.Failure(refusal).GetProperty(DropboxProtocol.Tag).GetString() == DropboxProtocol.Path) &&
            (!(throwsOnDispose || throwsOnStop || throwsOnInput) || Contains(error, cleanup)) && source.Exited &&
            input.AsyncDisposals == 1 && content.Disposals == 1 && response.Disposals == 1 &&
            attempt.Completion?.Result == RequestResult.UnknownWrite && ReferenceEquals(attempt.Completion?.Error, error) &&
            attempt.TryClaimCompletion() && !attempt.TryClaimCompletion() && handler.Requests.Count == 1 && source.CanRead,
            "Primary and cleanup diagnostics survive one joined, nonretryable attempt without closing borrowed input.");
    }

    private static async Task HttpThrottleNotificationFailureStopsAllFiveOwners()
    {
        foreach (var owner in Enum.GetValues<AttemptOwner>())
        foreach (var write in new[] { false, true })
        {
            var notification = new IOException(nameof(HttpThrottleNotificationFailureStopsAllFiveOwners));
            var notifications = 0;
            using var handler = new Handler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(ErrorJson) };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var api = Create(http, new Tokens(), waited: _ => { notifications++; throw notification; });
            var attempt = new RequestAttempt(new object(), owner);
            using var source = new MemoryStream();
            var pending = write ? api.UploadAsync(DropboxProtocol.Upload, new { }, source, DropboxReply.Void(), attempt, default)
                : api.RpcAsync(DropboxProtocol.GetMetadata, new { }, DropboxReply.Void(), attempt, default);
            var error = await FailureOf(pending);
            Check.Require(Contains(error, notification) && Errors(error).OfType<DropboxApiException>().Count() == 1 &&
                attempt.Completion?.Result == (write ? RequestResult.UnknownWrite : RequestResult.PermanentFailure) &&
                attempt.Completion?.ProviderWait == TimeSpan.Zero && attempt.ProviderWaitStartedTimestamp is not null &&
                notifications == 1 && handler.Requests.Count == 1 && attempt.TryClaimCompletion() && !attempt.TryClaimCompletion(),
                "Notification failure is terminal for every owner and cannot repeat already-applied feedback.");
        }
    }

    private static async Task ErrorReadAndDisposeFailuresPreserveBoth()
    {
        var read = new IOException(nameof(ErrorReadAndDisposeFailuresPreserveBoth));
        var dispose = new IOException(nameof(CleanupStream));
        var input = new CleanupStream([]) { ReadFailure = read, DisposeFailure = dispose };
        var response = new CleanupResponse(HttpStatusCode.TooManyRequests) { Content = new StreamContent(input) };
        using var handler = new Handler((_, _) => Task.FromResult<HttpResponseMessage>(response));
        using var http = new HttpClient(handler);
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        var error = await FailureOf(Create(http, new Tokens()).RpcAsync(DropboxProtocol.GetMetadata,
            new { }, DropboxReply.Void(), attempt, default));
        Check.Require(Contains(error, read) && Contains(error, dispose) && response.Disposals == 1 &&
            input.AsyncDisposals == 1 && attempt.Completion?.Result == RequestResult.PermanentFailure &&
            handler.Requests.Count == 1, "Read and distinct disposal errors survive; remaining cleanup runs without retry.");
    }

    private static async Task SealedPageTransportCleanupFailureDisposesSpoolAndFailsOnce()
    {
        var cleanup = new IOException(nameof(SealedPageTransportCleanupFailureDisposesSpoolAndFailsOnce));
        var spool = new CleanupStream([]);
        var response = new CleanupResponse(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"entries\":[],\"cursor\":\"next\",\"has_more\":false}"), Failure = cleanup
        };
        using var handler = new Handler((_, _) => Task.FromResult<HttpResponseMessage>(response));
        using var http = new HttpClient(handler);
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        var error = await FailureOf(Create(http, new Tokens(), spool: _ => ValueTask.FromResult<Stream>(spool))
            .ReadPageAsync(DropboxProtocol.ListFolderContinue, new { }, TestPair(), attempt, default));
        Check.Require(Contains(error, cleanup) && spool.AsyncDisposals == 1 &&
            attempt.Completion?.Result == RequestResult.PermanentFailure && handler.Requests.Count == 1 &&
            attempt.TryClaimCompletion() && !attempt.TryClaimCompletion(),
            "A sealed but unpublished page retains disposal custody and cannot escape after transport cleanup fails.");
    }

    private static Task CancelledPageBeginAndSpoolDisposeFailurePreserveBoth() => PageBeginFailureAsync(true);
    private static Task PartiallyStartedPageBeginDisposesSpoolOnce() => PageBeginFailureAsync(false);

    private static async Task PageBeginFailureAsync(bool cancel)
    {
        using var stop = new CancellationTokenSource();
        var begin = new IOException(nameof(PageBeginFailureAsync));
        var cleanup = new IOException(nameof(CleanupStream));
        var spool = new CleanupStream([]) { WriteCapabilityFailure = cancel ? null : begin, DisposeFailure = cleanup };
        var response = new CleanupResponse(HttpStatusCode.OK) { Content = new StringContent("{}") };
        using var handler = new Handler((_, _) => Task.FromResult<HttpResponseMessage>(response));
        using var http = new HttpClient(handler);
        var api = Create(http, new Tokens(), spool: _ =>
        {
            if (cancel) stop.Cancel();
            return ValueTask.FromResult<Stream>(spool);
        });
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        var error = await FailureOf(api.ReadPageAsync(DropboxProtocol.ListFolder, new { }, TestPair(), attempt, stop.Token));
        Check.Require((cancel ? Errors(error).Any(value => value is OperationCanceledException) : Contains(error, begin)) &&
            Contains(error, cleanup) && spool.AsyncDisposals == 1 && response.Disposals == 1 &&
            attempt.Completion?.Result == RequestResult.PermanentFailure && handler.Requests.Count == 1,
            "Failed Begin keeps its original error, disposes its provisional spool once, and attempts remaining cleanup.");
        Check.Require(Refused(() => handler.Requests[0].Content!.ReadAsStream()), "Request cleanup also ran after failed Begin.");
    }

    private static async Task PartiallyStartedPageBeginThenPreBeginRetryFailureDoesNotRedisposeSpool()
    {
        var first = new HttpRequestException(nameof(PartiallyStartedPageBeginThenPreBeginRetryFailureDoesNotRedisposeSpool));
        var terminal = new InvalidOperationException(nameof(Tokens));
        var spool = new CleanupStream([]) { WriteCapabilityFailure = first };
        var tokens = new Tokens { FailureAtCall = call => call > 1 ? terminal : null };
        var clock = new ObservedClock();
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}") }));
        using var http = new HttpClient(handler);
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        var pending = Create(http, tokens, clock, _ => ValueTask.FromResult<Stream>(spool))
            .ReadPageAsync(DropboxProtocol.ListFolder, new { }, TestPair(), attempt, default);
        var delay = TimeSpan.FromSeconds(Backoff().InitialBackoffSeconds);
        await Within(clock.PendingAsync(delay));
        Check.Require(spool.AsyncDisposals == 1 && handler.Requests.Count == 1 && attempt.Completion is null,
            "The first partial Begin was disposed before a permitted Request retry.");
        clock.Advance(delay);
        var error = await FailureOf(pending);
        Check.Require(ReferenceEquals(error, terminal) && spool.AsyncDisposals == 1 && handler.Requests.Count == 1 &&
            attempt.Completion?.Result == RequestResult.PermanentFailure && ReferenceEquals(attempt.Completion?.Error, terminal),
            "A pre-Begin retry failure must not revisit the disposed spool left by the prior partial Begin.");
    }

    private static async Task HashedUploadTransientTokenFailureBeforeSend()
    {
        var clock = new ObservedClock();
        var tokens = new Tokens { FailureAtCall = call => call == 1 ? new HttpRequestException(nameof(Tokens)) : null };
        using var source = new ObservedStream(Encoding.UTF8.GetBytes(Payload));
        source.Position = StartPosition;
        using var hash = new ContentBlockHasher();
        using var handler = new Handler(async (request, ct) =>
        {
            Check.Require(await request.Content!.ReadAsStringAsync(ct) == PayloadTail, "The admitted upload uses its original window.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        using var http = new HttpClient(handler);
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        var pending = Create(http, tokens, clock).UploadAsync(DropboxProtocol.Upload, new { }, source,
            DropboxReply.Void(), attempt, default, hash);
        var delay = TimeSpan.FromSeconds(Backoff().InitialBackoffSeconds);
        await Within(clock.PendingAsync(delay));
        Check.Require(handler.Requests.Count == 0 && hash.TotalBytes == 0 && source.Position == StartPosition,
            "A transient token failure is known unsent and has not touched source or hash.");
        clock.Advance(delay);
        var result = await Within(pending);
        var bytes = Encoding.UTF8.GetBytes(PayloadTail);
        var expected = Convert.ToHexString(SHA256.HashData(SHA256.HashData(bytes))).ToLowerInvariant();
        Check.Require(result.UploadedBytes == bytes.Length && hash.TotalBytes == bytes.Length && hash.Finish() == expected &&
            handler.Requests.Count == 1 && tokens.Calls == StartLevel + 1 && !source.Disposed &&
            attempt.Completion?.Result == RequestResult.Success, "One admitted send hashes each byte exactly once after token recovery.");
    }

    private static async Task HashedUploadDispatchedFailureNeverResends()
    {
        using var source = new ObservedStream(Encoding.UTF8.GetBytes(Payload));
        using var hash = new ContentBlockHasher();
        var failure = new HttpRequestException(nameof(HashedUploadDispatchedFailureNeverResends));
        using var handler = new Handler(async (request, ct) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, ct);
            throw failure;
        });
        using var http = new HttpClient(handler);
        var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
        var error = await FailureOf(Create(http, new Tokens()).UploadAsync(DropboxProtocol.Upload, new { }, source,
            DropboxReply.Void(), attempt, default, hash));
        Check.Require(ReferenceEquals(error, failure) && attempt.Completion is { Result: RequestResult.UnknownWrite, NoEffectProven: false } &&
            handler.Requests.Count == 1 && hash.TotalBytes == source.Length && !source.Disposed,
            "Dispatched hashed upload failure is uncertain and never appends or sends again.");
    }

    private static async Task DirectUploadRetryAsync()
    {
        foreach (var (wait, cancel, cleanupFailure, status) in new[]
        {
            (TimeSpan.Zero, false, false, HttpStatusCode.TooManyRequests),
            (OneSecond, false, false, HttpStatusCode.TooManyRequests),
            (OneSecond, true, false, HttpStatusCode.TooManyRequests),
            (TimeSpan.Zero, false, true, HttpStatusCode.TooManyRequests),
            (TimeSpan.Zero, false, false, HttpStatusCode.ServiceUnavailable)
        })
        {
            var clock = new ObservedClock();
            using var stop = new CancellationTokenSource();
            using var source = new JoiningUpload(Encoding.UTF8.GetBytes(Payload)) { ReadBeforeJoin = true };
            source.Position = StartPosition;
            var cleanup = new IOException(nameof(DirectUploadRetryAsync));
            var response = new CleanupResponse(status)
            {
                Content = new StringContent(ErrorJson), Failure = cleanupFailure ? cleanup : null,
                BeforeDispose = () => Check.Require(source.Exited, "Retry cleanup must follow the serializer join.")
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
            Task copying = Task.CompletedTask;
            var calls = 0;
            using var handler = new Handler(async (request, ct) =>
            {
                if (++calls == 1)
                {
                    copying = request.Content!.CopyToAsync(Stream.Null, ct);
                    await source.Started.Task;
                    return response;
                }
                Check.Require(response.Disposals == 1 && source.Exited &&
                    await request.Content!.ReadAsStringAsync(ct) == PayloadTail,
                    "The next attempt starts after cleanup and sends the complete original window.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });
            using var http = new HttpClient(handler);
            var attempt = new RequestAttempt(new object(), AttemptOwner.Request);
            var pending = Create(http, new Tokens(), clock).UploadDirectAsync(new { }, source,
                DropboxReply.Void(), attempt, stop.Token);
            await Within(source.Stopped.Task);
            Check.Require(calls == 1 && !pending.IsCompleted && response.Disposals == 0 &&
                source.Position > StartPosition && source.Position < source.Length,
                "A partially serialized refusal cannot replay before the previous serializer exits.");
            source.Release.TrySetResult();
            await FailureOf(copying);
            if (cleanupFailure || status == HttpStatusCode.ServiceUnavailable)
            {
                var error = await FailureOf(pending);
                Check.Require(calls == 1 && attempt.Completion?.Result == RequestResult.UnknownWrite &&
                    (!cleanupFailure || Contains(error, cleanup)), "Cleanup failure and 503 are never replayed.");
                continue;
            }
            if (wait != TimeSpan.Zero)
            {
                await Within(clock.PendingAsync(wait));
                Check.Require(calls == 1 && attempt.Completion is null, "The provider wait precedes the retry.");
                if (cancel) stop.Cancel();
                else clock.Advance(wait);
            }
            if (cancel)
            {
                Check.Require(await FailureOf(pending) is OperationCanceledException && calls == 1 &&
                    attempt.Completion?.Result == RequestResult.CallerStopped, "Cancellation during the wait stops replay.");
                continue;
            }
            var result = await Within(pending);
            var bytes = Encoding.UTF8.GetBytes(PayloadTail);
            var expected = Convert.ToHexString(SHA256.HashData(SHA256.HashData(bytes))).ToLowerInvariant();
            Check.Require(calls == ReplacementConnections && result.UploadedBytes == bytes.Length &&
                result.ContentHash == expected && attempt.Completion?.Result == RequestResult.Success && source.CanRead,
                "Only the successful direct attempt contributes bytes and hash; the caller retains its source.");
        }
    }

    private static IEnumerable<Exception> Errors(Exception? error)
    {
        if (error is null) yield break;
        yield return error;
        if (error is AggregateException aggregate)
        {
            foreach (var child in aggregate.InnerExceptions)
                foreach (var nested in Errors(child)) yield return nested;
        }
        else if (error.InnerException is { } inner)
            foreach (var nested in Errors(inner)) yield return nested;
    }

    private static bool Contains(Exception? error, Exception expected) => Errors(error).Any(value => ReferenceEquals(value, expected));

    private static PairConfig TestPair() => new("pair", "Pair A", "", "/Parent", "connection",
        Array.Empty<string>(), Array.Empty<Exclusion>(), FirstRunPolicy.NewestWins, true, null);

    private static async Task DownloadEofDoesNotGrowAsync()
    {
        foreach (var scenario in new[] { "valid", "short", "wrong-hash", "cleanup" })
        {
            var bytes = Encoding.UTF8.GetBytes(scenario == "short" ? "" : scenario == "wrong-hash" ? "changed" : "content");
            var cleanup = new IOException("download cleanup");
            var input = new CleanupStream(bytes) { DisposeFailure = scenario == "cleanup" ? cleanup : null };
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new CleanupContent(input) }));
            using var http = new HttpClient(handler);
            var api = Create(http, new Tokens());
            var attempt = new RequestAttempt(new object(), AttemptOwner.Transfer);
            var downloaded = await api.DownloadAsync(DropboxProtocol.Download, new { path = ArgumentPath }, attempt, default);
            Exception? failure = null;
            try
            {
                await downloaded.CopyToAsync(Stream.Null);
                Check.Require(api.Limiter.Level == StartLevel,
                    "Raw download EOF never grows admission, regardless of body length or hash.");
            }
            finally
            {
                try { await downloaded.DisposeAsync(); }
                catch (Exception error) { failure = error; }
            }
            Check.Require(scenario == "cleanup" ? Contains(failure, cleanup) : failure is null,
                "The download fixture preserves its cleanup outcome.");
            Check.Require(api.Limiter.Level == StartLevel,
                "Neither valid download transport completion nor failed cleanup earns read growth.");
        }
    }

    private static async Task ValidatedReadGrowthAsync()
    {
        foreach (var valid in new[] { true, false })
        {
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(valid ? "{}" : "{")
            }));
            using var http = new HttpClient(handler);
            var api = Create(http, new Tokens());
            var attempt = new RequestAttempt(new object(), AttemptOwner.Recovery);
            try { await api.RpcAsync(DropboxProtocol.GetMetadata, new { }, DropboxReply.Void(), attempt, default); }
            catch when (!valid) { }
            Check.Require(api.Limiter.Level == (valid ? AdaptiveLimiter.GrowthFactor : StartLevel),
                "Only fully validated read replies grow account admission; malformed successful HTTP does not.");
        }
    }

    private static DropboxApi Create(HttpClient http, Tokens tokens, TimeProvider? time = null,
        Func<CancellationToken, ValueTask<Stream>>? spool = null, Action<WaitEvent>? waited = null,
        string? account = null, string? source = null, int level = StartLevel)
    {
        time ??= new ObservedClock();
        var network = new FakeNetworkSignal();
        network.SetAvailable(true);
        var backoff = Backoff();
        var limiter = new AdaptiveLimiter(level, UploadStartLevel, time, network, waited ?? (_ => { }), backoff);
        return new DropboxApi(http, tokens, NamespaceId, limiter,
            spool ?? (_ => ValueTask.FromResult<Stream>(new MemoryStream())), backoff, time,
            account ?? Guid.NewGuid().ToString(), source ?? Guid.NewGuid().ToString());
    }

    private static FailureRetryOwner.BackoffSettings Backoff()
    {
        using var defaults = typeof(EngineSettings).Assembly.GetManifestResourceStream("SyncShark.EngineSettings.json")!;
        using var reader = new StreamReader(defaults);
        return SettingsParser.Parse(reader.ReadToEnd(), null).Backoff;
    }

    private static SocketsHttpHandler HandlerOf(HttpClient client) =>
        (SocketsHttpHandler)typeof(HttpMessageInvoker)
            .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;

    private static async Task Within(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(Watchdog)) != task) throw new FixtureStuckException();
        await task;
    }

    private static async Task<T> Within<T>(Task<T> task)
    {
        await Within((Task)task);
        return await task;
    }

    private static async Task<Exception?> FailureOf(Task task)
    {
        try
        {
            await Within(task);
            return null;
        }
        catch (Exception error) when (error is not FixtureStuckException)
        {
            return error;
        }
    }

    // ProviderTransportTimeoutException is internal to Core, and the handler may wrap it.
    private static bool IsExpiry(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
        {
            if (error is TimeoutException) return true;
        }
        return false;
    }

    private enum PoolPhase { Checkout, FirstWrite, Scavenged }

    private enum PoolEvent { None, End, Data, Fault, Expiry, DelayedExpiry }

    private enum PageWaitEnd { Sealed, Cancelled, Disabled }

    private sealed class FixtureStuckException() : Exception("A transport fixture stopped progressing.");

    /// <summary>One real guarded client whose sockets are scripted connections, so races are deterministic.</summary>
    private sealed class Wire : IDisposable
    {
        public const string Host = "content.fixture";
        public const string Url = "http://" + Host + "/";
        public static readonly Action<ScriptedConnection, string> Serve =
            static (link, _) => link.Send(StatusLine + ReplyTail);
        public static readonly Action<ScriptedConnection, string> Hold = static (_, _) => { };
        private readonly Channel<ScriptedConnection> accepted = Channel.CreateUnbounded<ScriptedConnection>();
        private int connections;

        public Wire(TimeSpan readAllowance, ObservedClock? clock = null)
            : this(null, clock ?? new ObservedClock(), readAllowance) { }

        public Wire(HttpClient client, ObservedClock clock) : this(client, clock, default) { }

        private Wire(HttpClient? client, ObservedClock clock, TimeSpan readAllowance)
        {
            Clock = clock;
            Client = client ?? Massdex.SyncShark.V2.EngineComposition.CreateDropboxHttpClient(
                readAllowance, DecompressionMethods.None, clock);
            HandlerOf(Client).ConnectCallback = (context, _) =>
            {
                var link = new ScriptedConnection(this);
                Configure?.Invoke(link);
                Hosts.Enqueue(context.DnsEndPoint.Host);
                Interlocked.Increment(ref connections);
                accepted.Writer.TryWrite(link);
                return ValueTask.FromResult<Stream>(link);
            };
        }

        public ObservedClock Clock { get; }
        public HttpClient Client { get; }
        public ConcurrentQueue<string> Hosts { get; } = new();
        public Action<ScriptedConnection, string> Respond { get; set; } = Serve;
        public Action<ScriptedConnection>? Configure { get; set; }
        public int ConnectionCount => Volatile.Read(ref connections);

        public Task<ScriptedConnection> AcceptAsync() => Within(accepted.Reader.ReadAsync().AsTask());

        public async Task<string> GetAsync()
        {
            using var response = await Within(Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead));
            return await Within(response.Content.ReadAsStringAsync());
        }

        public void Dispose() => Client.Dispose();
    }

    /// <summary>A scripted transport below the real handler and guard; only test code decides what arrives.</summary>
    private sealed class ScriptedConnection(Wire wire) : Stream
    {
        private const string HeaderEnd = "\r\n\r\n";
        private readonly object gate = new();
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<string> arrivals = Channel.CreateUnbounded<string>();
        private readonly StringBuilder request = new();
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource idleRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] chunk = [];
        private int offset;
        private int queued;
        private int requests;
        private bool waiting;
        private bool body;

        /// <summary>Runs once inside the next nonempty read, after the guard has armed its timer.</summary>
        public Action? BeforeRead;

        public Func<int, CancellationToken, Task>? BeforeWrite { get; set; }
        public ConcurrentQueue<int> Slices { get; } = new();
        public Task Closed => closed.Task;

        /// <summary>Completes after everything sent was consumed and another read started on the idle connection.</summary>
        public Task IdleRead => idleRead.Task;

        /// <summary>Completes while a nonempty read is pending with nothing to deliver.</summary>
        public Task Parked
        {
            get
            {
                lock (gate) return parked.Task;
            }
        }

        public Task<string> RequestAsync() => Within(arrivals.Reader.ReadAsync().AsTask());

        public void Send(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            lock (gate)
            {
                ResetParked();
                queued += bytes.Length;
                incoming.Writer.TryWrite(bytes);
            }
        }

        /// <summary>Completes a zero-byte read without delivering data.</summary>
        public void Wake() => Send("");

        public void End(Exception? fault = null)
        {
            lock (gate)
            {
                ResetParked();
                incoming.Writer.TryComplete(fault);
            }
        }

        private void ResetParked()
        {
            waiting = false;
            parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!buffer.IsEmpty) Interlocked.Exchange(ref BeforeRead, null)?.Invoke();
            while (true)
            {
                lock (gate)
                {
                    if (requests > 0 && queued == 0) idleRead.TrySetResult();
                    if (buffer.IsEmpty)
                    {
                        if (offset < chunk.Length) return 0;
                    }
                    else
                    {
                        while (offset == chunk.Length && incoming.Reader.TryRead(out var next))
                        {
                            chunk = next;
                            offset = 0;
                        }
                        if (offset < chunk.Length)
                        {
                            var count = Math.Min(buffer.Length, chunk.Length - offset);
                            chunk.AsMemory(offset, count).CopyTo(buffer);
                            offset += count;
                            queued -= count;
                            return count;
                        }
                        waiting = true;
                        parked.TrySetResult();
                    }
                }
                if (!await incoming.Reader.WaitToReadAsync(ct)) return 0;
                if (buffer.IsEmpty) return 0;
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty) return;
            Slices.Enqueue(buffer.Length);
            if (BeforeWrite is { } hook) await hook(buffer.Length, ct);
            if (body) return;
            request.Append(Encoding.Latin1.GetString(buffer.Span));
            var text = request.ToString();
            if (!text.Contains(HeaderEnd, StringComparison.Ordinal)) return;
            request.Clear();
            body = text.StartsWith(HttpMethod.Post.Method, StringComparison.Ordinal);
            lock (gate)
            {
                if (!waiting) ResetParked();
            }
            wire.Respond(this, text);
            Interlocked.Increment(ref requests);
            arrivals.Writer.TryWrite(text);
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                incoming.Writer.TryComplete();
                closed.TrySetResult();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class SlicedContent(int chunks, int chunkBytes, Func<int, Task>? beforeChunk = null) : HttpContent
    {
        private readonly TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Sent => sent.Task;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[chunkBytes];
            for (var index = 0; index < chunks; index++)
            {
                if (beforeChunk is not null) await beforeChunk(index);
                await stream.WriteAsync(chunk);
            }
            sent.TrySetResult();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = (long)chunks * chunkBytes;
            return true;
        }
    }

    private sealed class SpoolStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ChunkedStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], ct);
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(SplitChunkBytes, buffer.Length)], ct);
    }

    private sealed class GatedEofStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, ct);
            Waiting.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class AllocationStream(byte[] bytes) : MemoryStream(bytes)
    {
        private long? started;
        private long initialRetained;
        public long Allocated { get; private set; }
        public long Retained { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (started is null)
            {
                initialRetained = GC.GetTotalMemory(true);
                started = GC.GetAllocatedBytesForCurrentThread();
            }
            if (Position == Length)
            {
                Allocated = GC.GetAllocatedBytesForCurrentThread() - started.Value;
                Retained = GC.GetTotalMemory(true) - initialRetained;
            }
            return base.ReadAsync(buffer, ct);
        }
    }

    private sealed class CleanupStream : MemoryStream
    {
        public CleanupStream(byte[] bytes) { Write(bytes); Position = 0; }
        public Exception? ReadFailure { get; init; }
        public Exception? DisposeFailure { get; set; }
        public Exception? WriteCapabilityFailure { get; init; }
        public Action? BeforeDispose { get; set; }
        public int AsyncDisposals { get; private set; }
        public override bool CanWrite => WriteCapabilityFailure is { } error ? throw error : base.CanWrite;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ReadFailure is { } error ? ValueTask.FromException<int>(error) : base.ReadAsync(buffer, ct);
        public override ValueTask DisposeAsync()
        {
            AsyncDisposals++;
            BeforeDispose?.Invoke();
            base.Dispose(true);
            return DisposeFailure is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask;
        }
    }

    private sealed class CleanupContent : HttpContent
    {
        private readonly Stream input;
        public CleanupContent(string json) : this(new MemoryStream(Encoding.UTF8.GetBytes(json))) { }
        public CleanupContent(Stream input) => this.input = input;
        public Exception? Failure { get; init; }
        public Action? BeforeDispose { get; init; }
        public int Disposals { get; private set; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(input);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => Task.FromResult(input);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => input.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposals++;
                BeforeDispose?.Invoke();
            }
            base.Dispose(disposing);
            if (disposing && Failure is { } error) throw error;
        }
    }

    private sealed class CleanupResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        public Exception? Failure { get; init; }
        public Action? BeforeDispose { get; init; }
        public int Disposals { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposals++;
                BeforeDispose?.Invoke();
            }
            base.Dispose(disposing);
            if (disposing && Failure is { } error) throw error;
        }
    }

    private sealed class JoiningUpload(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? StopFailure { get; init; }
        public bool ReadBeforeJoin { get; init; }
        private bool _read;
        public bool Exited { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (ReadBeforeJoin && (!_read || Exited))
            {
                var first = !_read;
                _read = true;
                return await base.ReadAsync(first ? buffer[..OneUnit] : buffer, ct);
            }
            using var registration = ct.Register(() =>
            {
                Stopped.TrySetResult();
                if (StopFailure is { } error) throw error;
            });
            Started.TrySetResult();
            try
            {
                await Release.Task;
                ct.ThrowIfCancellationRequested();
                return await base.ReadAsync(buffer, ct);
            }
            finally { Exited = true; }
        }
    }

    private sealed class Tokens : IDropboxAccessTokenSource
    {
        public int Calls { get; private set; }
        public string Current { get; set; } = Token;
        public Func<int, Exception?>? FailureAtCall { get; init; }
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return FailureAtCall?.Invoke(Calls) is { } failure ? Task.FromException<string>(failure) : Task.FromResult(Current);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return send(request, cancellationToken);
        }
    }

    private sealed class ObservedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ObservedContent(byte[] bytes) : HttpContent
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads += 1;
            return stream.WriteAsync(bytes).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingContent : HttpContent
    {
        public static readonly IOException Failure = new("body-read-fixture");

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(Failure);

        protected override bool TryComputeLength(out long length)
        {
            length = default;
            return false;
        }
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
