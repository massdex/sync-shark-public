using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Dropbox;

// One HTTP exchange owner: the provisional response, its attempt completion and pre-retry cleanup are one lifetime.
/// <summary>The HTTP and response-consumption boundary; retry policy and decoding retain their shared owners.</summary>
public sealed partial class DropboxApi(
    HttpClient http, IDropboxAccessTokenSource tokens, string rootNamespaceId, AdaptiveLimiter limiter,
    Func<CancellationToken, ValueTask<Stream>> createPageSpool,
    FailureRetryOwner.BackoffSettings backoff, TimeProvider time, string accountId, string sourceIdentity)
{
    private readonly HttpClient _http = http;
    private readonly IDropboxAccessTokenSource _tokens = tokens;
    private readonly string _pathRoot = JsonSerializer.Serialize(
        DropboxProtocol.TaggedUnion(DropboxProtocol.Root, rootNamespaceId));
    private readonly AdaptiveLimiter _limiter = limiter;

    private static readonly AsyncLocal<Action<RunEvent>?> RequestObserver = new();

    /// <summary>Routes this async operation's request facts without changing the shared API's other callers.</summary>
    public static IDisposable ObserveRequests(Action<RunEvent>? events) => new ObservationScope(events);

    private sealed class ObservationScope : IDisposable
    {
        private readonly Action<RunEvent>? _previous = RequestObserver.Value;

        internal ObservationScope(Action<RunEvent>? events) => RequestObserver.Value = events;
        public void Dispose() => RequestObserver.Value = _previous;
    }

    private readonly struct RequestObservation : IDisposable
    {
        private readonly Action<RunEvent>? _events;
        private readonly RunEvent _started;
        private readonly RequestAttempt? _attempt;

        internal RequestObservation(string operation, string subject, RequestAttempt? attempt, object? requestId = null)
        {
            _events = RequestObserver.Value;
            _attempt = attempt;
            _started = new RunEvent(RunEventKind.ProviderRequest, operation, subject, string.Empty, string.Empty)
                { RequestId = _events is null ? null : requestId ?? new object() };
            Publish(_events, _started);
        }

        public void Dispose() => End(_attempt?.Completion?.Result);

        // The mapped provider cause is the evidence a failure line otherwise lacks: a documented absence and a real
        // refusal share the same status word (bug #95). A success carries no cause.
        internal void End(RequestResult? result) => Publish(_events, _started with
        {
            Ended = true,
            RequestResult = result,
            RequestRetry = false,
            Detail = Cause(_attempt?.Completion?.Error)
        });

        private static string Cause(Exception? error) => error switch
        {
            DropboxApiException api => DropboxErrors.ProviderCause(api),
            ProviderFailureException failure => DropboxErrors.SafeProviderTag(failure.Tag),
            null => string.Empty,
            _ => error.GetType().Name
        };
    }

    private static void Publish(Action<RunEvent>? events, RunEvent item)
    {
        // Observing a request must never change its effect or retry ownership.
        try { events?.Invoke(item); }
        catch (Exception) { }
    }

    internal AdaptiveLimiter Limiter => _limiter;

    /// <summary>The account/route/source identity admission and provider floors match for one known route.</summary>
    internal (string AccountId, string RouteName, string SourceIdentity) AdmissionIdentity(string routeName) =>
        (accountId, RequireRoute(routeName).Name, sourceIdentity);

    /// <summary>Returns the typed reply only after complete response validation, completing the supplied attempt.</summary>
    public partial Task<T> RpcAsync<T>(string route, object arg, DropboxReply<T> reply,
        RequestAttempt attempt, CancellationToken ct, string subject = "");

    public async partial Task<T> RpcAsync<T>(string route, object arg, DropboxReply<T> reply,
        RequestAttempt attempt, CancellationToken ct, string subject) =>
        (await ExecuteAsync(route, arg, null, null, reply, attempt, false, ct, subject: subject).ConfigureAwait(false)).Reply;

    /// <summary>Waits on one source's cursor; a returned backoff is installed for that source's next longpoll at decode,
    /// before transport cleanup can block or fail, so Continue drains without waiting.</summary>
    internal async Task<bool> LongpollAsync(string cursor, RequestAttempt attempt, CancellationToken ct)
    {
        var reply = DropboxReplyShapes.Longpoll();
        reply.Accepted = decoded =>
        {
            if (DropboxProtocol.LongpollConstraintFor(accountId, sourceIdentity, decoded.Backoff, time.GetTimestamp())
                is { } floor)
                FailureRetryOwner.InstallProviderConstraint(floor.Scope, floor.StartedTimestamp, floor.Duration, time);
        };
        return (await ExecuteAsync(DropboxProtocol.ListFolderLongpoll,
            new { cursor, timeout = DropboxApiLimits.LongpollMaxSeconds }, null, null, reply, attempt, false, ct)
            .ConfigureAwait(false)).Reply.Changes;
    }

    /// <summary>Transfers a sealed page only after complete response validation; the caller owns its disposal.</summary>
    public partial Task<DropboxPage> ReadPageAsync(string route, object arg, PairConfig pair,
        RequestAttempt attempt, CancellationToken ct);

    public async partial Task<DropboxPage> ReadPageAsync(string route, object arg, PairConfig pair,
        RequestAttempt attempt, CancellationToken ct) =>
        (await ExecuteAsync(route, arg, null, null, DropboxReplyShapes.Page(pair), attempt, true, ct).ConfigureAwait(false)).Reply;

    /// <summary>Returns a ResponseStream carrying the exact staging attempt; EOF is not staging success.</summary>
    public async Task<Stream> DownloadAsync(string route, object arg, RequestAttempt? attempt, CancellationToken ct,
        string subject = "")
    {
        var known = RequireRoute(route);
        var observation = new RequestObservation(known.Name, subject, attempt);
        var identity = AdmissionIdentity(known.Name);
        var exchange = new Exchange();
        var read = known.Policy == DropboxRoutePolicy.Read;
        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(known, JsonSerializer.Serialize(arg), null, null, exchange, identity, ct)
                .ConfigureAwait(false);
            await AcceptHeadersAsync(response, exchange, identity, read, ct).ConfigureAwait(false);
            var inner = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            exchange.Request?.Dispose();
            return new ResponseStream(response, inner, attempt, ct, exchange.Wait, exchange.WaitStarted,
                result => _limiter.ReportHttpOutcome(identity, (result, true), null, null), observation.End);
        }
        catch (Exception error)
        {
            try
            {
                PublishObservation(exchange);
                var cleanup = await ReleaseAsync(exchange, response, null).ConfigureAwait(false);
                var terminal = CompleteFailure(attempt, exchange, error, cleanup, read, false, ct);
                if (ReferenceEquals(terminal, error)) throw;
                throw terminal;
            }
            finally { observation.Dispose(); }
        }
    }

    /// <summary>Returns a completely validated typed reply and completes the supplied attempt; body remains caller-owned.</summary>
    public partial Task<T> UploadAsync<T>(string route, object arg, Stream body, DropboxReply<T> reply,
        RequestAttempt attempt, CancellationToken ct);

    public async partial Task<T> UploadAsync<T>(string route, object arg, Stream body, DropboxReply<T> reply,
        RequestAttempt attempt, CancellationToken ct) =>
        (await ExecuteAsync(route, arg, body, null, reply, attempt, false, ct).ConfigureAwait(false)).Reply;

    /// <summary>Hashes the same serialized reads and returns their successfully written count; only a proved unsent
    /// attempt, which read nothing into the hash, may run again.</summary>
    public async Task<(T Reply, long UploadedBytes)> UploadAsync<T>(string route, object arg, Stream body, DropboxReply<T> reply,
        RequestAttempt attempt, CancellationToken ct, ContentBlockHasher hash)
    {
        var result = await ExecuteAsync(route, arg, body, hash, reply, attempt, false, ct).ConfigureAwait(false);
        return (result.Reply, result.UploadedBytes);
    }

    /// <summary>Hashes each direct-upload attempt independently and returns only the successful digest.</summary>
    public async Task<(T Reply, long UploadedBytes, string ContentHash)> UploadDirectAsync<T>(object arg, Stream body,
        DropboxReply<T> reply, RequestAttempt attempt, CancellationToken ct)
    {
        ContentBlockHasher? hash = null;
        try
        {
            var result = await ExecuteAsync(DropboxProtocol.Upload, arg, body, null, reply, attempt, false, ct,
                createHash: () =>
                {
                    hash?.Dispose();
                    return hash = new ContentBlockHasher();
                }).ConfigureAwait(false);
            return (result.Reply, result.UploadedBytes, hash!.Finish());
        }
        finally { hash?.Dispose(); }
    }

    /// <summary>Validates positional batch replies through the sole decoder; no uncertain mutation is replayed.</summary>
    public async Task<IReadOnlyList<(Entry? Entry, Exception? Error, bool KnownNoEffect, bool WriteThrottled)>>
        FinishBatchAsync(object arg, PairConfig pair, IReadOnlyList<(string Path, bool ExpectedIsNull)> targets,
            RequestAttempt attempt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(arg);
        using (var document = JsonDocument.Parse(json))
        {
            if (!document.RootElement.TryGetProperty(DropboxListing.EntriesName, out var entries) ||
                entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != targets.Count)
                throw new ArgumentException(null, nameof(arg));
        }
        var response = await ExecuteAsync(DropboxProtocol.UploadSessionFinishBatch, arg, null, null,
            DropboxReplyShapes.FinishBatch(pair, targets), attempt, false, ct, json).ConfigureAwait(false);
        foreach (var member in response.Reply)
            if (member.Error?.InnerException is ProviderFailureException failure)
                failure.HttpStatus = response.HttpStatus;
        return response.Reply;
    }

    public void ReportBatchOutcome(bool writeThrottled) => ReportBatchOutcome(writeThrottled, null);

    internal partial void ReportBatchOutcome(bool writeThrottled, TimeSpan? providerWait);

    internal partial void ReportBatchOutcome(bool writeThrottled, TimeSpan? providerWait) =>
        _limiter.ReportBatchOutcome(AdmissionIdentity(DropboxProtocol.UploadSessionFinishBatch), writeThrottled, providerWait);

    // The retry claim, the current attempt and its wait are one loop: the claim authorizes the wait, so a separate attempt could retry without it.
    // Request alone owns this loop. Listener, Prerequisite, Transfer and Recovery retain their supplied identity.
    private async Task<(T Reply, long UploadedBytes, int HttpStatus)> ExecuteAsync<T>(string name, object arg, Stream? body,
        ContentBlockHasher? hash, DropboxReply<T> reply, RequestAttempt attempt, bool page, CancellationToken ct,
        string? json = null, Func<ContentBlockHasher>? createHash = null, string subject = "")
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var route = RequireRoute(name);
        // A direct upload may replay only when no caller-owned hasher would see bytes from a failed attempt.
        var replayDirect = name == DropboxProtocol.Upload && (hash is null || createHash is not null);
        var start = replayDirect && body?.CanSeek == true ? body.Position : (long?)null;
        json ??= JsonSerializer.Serialize(arg);
        // The episode exists only once a retryable Request failure needs it; a clean call allocates none.
        FailureRetryOwner.Episode? episode = null;
        while (true)
        {
            var current = attempt.Owner == AttemptOwner.Request
                ? new RequestAttempt(attempt.Operation, attempt.Owner) : attempt;
            // The retry Wait names this exact request, so the run log renders it from the request it tracks.
            var requestId = new object();
            try
            {
                var result = await ConsumeAsync(route, json, body, createHash?.Invoke() ?? hash, reply, current, page, ct,
                    replayDirect, start, subject, requestId).ConfigureAwait(false);
                CopyCompletion(current, attempt);
                return result;
            }
            catch
            {
                var completion = current.Completion;
                if (!RequestAttempt.RetriesAtRequest(attempt.Owner, completion?.Result) ||
                    completion is not { NoEffectProven: true } failed || !current.TryClaimCompletion())
                {
                    CopyCompletion(current, attempt);
                    throw;
                }
                episode ??= new FailureRetryOwner.Episode(providerWaitCounts: true);
                var wait = episode.Failed(failed.ProviderWait, current.ProviderWaitStartedTimestamp, time,
                    backoff, Random.Shared.NextDouble);
                // One line per selected retry, before waiting; the failed attempt's own end line already carries its cause.
                Publish(RequestObserver.Value,
                    RunEvent.Wait(TextKey.RequestWaiting, subject, retryDelay: wait) with { RequestId = requestId });
                try
                {
                    await Task.Delay(wait, time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException stopped)
                {
                    attempt.TryComplete(RequestResult.CallerStopped, stopped, null, true);
                    throw;
                }
            }
        }
    }

    // The outer Request wrapper takes its child's terminal facts; a supplied attempt was completed in place.
    private static void CopyCompletion(RequestAttempt current, RequestAttempt attempt)
    {
        if (ReferenceEquals(current, attempt) || current.Completion is not { } terminal) return;
        attempt.TryComplete(terminal.Result, terminal.Error, terminal.ProviderWait, terminal.NoEffectProven,
            current.ProviderWaitStartedTimestamp);
    }

    // This method owns the whole provisional response lifetime, including cleanup before retry admission.
    private async Task<(T Reply, long UploadedBytes, int HttpStatus)> ConsumeAsync<T>(DropboxRoute route, string json, Stream? body,
        ContentBlockHasher? hash, DropboxReply<T> reply, RequestAttempt attempt, bool page, CancellationToken ct,
        bool replayDirect, long? start, string subject, object requestId)
    {
        using var observation = new RequestObservation(route.Name, subject, attempt, requestId);
        var exchange = new Exchange { ReplayDirect = replayDirect };
        var identity = AdmissionIdentity(route.Name);
        var read = route.Policy == DropboxRoutePolicy.Read;
        var sourceFailure = false;
        HttpResponseMessage? response = null;
        T result;
        long uploaded;
        try
        {
            if (start is { } position && body!.Position != position) body.Position = position;
            response = await SendAsync(route, json, body, hash, exchange, identity, ct).ConfigureAwait(false);
            await AcceptHeadersAsync(response, exchange, identity, read, ct).ConfigureAwait(false);
            exchange.Spool = page ? await createPageSpool(ct).ConfigureAwait(false) : null;
            await reply.Begin(exchange.Spool, ct).ConfigureAwait(false);
            exchange.Spool = null;
            var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            exchange.Input = input;
            var buffer = ArrayPool<byte>.Shared.Rent(SyncContentIdentity.DefaultReadBufferBytes);
            try
            {
                while (true)
                {
                    int count;
                    try { count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); }
                    catch { sourceFailure = true; throw; }
                    if (count == 0) break;
                    await reply.Read(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            result = await reply.Complete(ct).ConfigureAwait(false);
            uploaded = exchange.Content is { } content ? await content.CompleteAsync(ct).ConfigureAwait(false) : 0;
        }
        catch (Exception error)
        {
            PublishObservation(exchange);
            // Until Begin returns the spool is this exchange's alone, and an unstarted reply holds nothing to abort.
            var abort = exchange.Spool is null ? reply.Abort : (Func<ValueTask>?)null;
            var cleanup = await ReleaseAsync(exchange, response, abort).ConfigureAwait(false);
            var terminal = CompleteFailure(attempt, exchange, error, cleanup, read, sourceFailure, ct);
            if (ReferenceEquals(terminal, error)) throw;
            throw terminal;
        }
        var released = await ReleaseAsync(exchange, response, null).ConfigureAwait(false);
        if (released is not null)
        {
            // A sealed page never reached its caller, so its spool is still this attempt's to dispose.
            try { if (result is DropboxPage sealedPage) await sealedPage.DisposeAsync().ConfigureAwait(false); }
            catch (Exception failure) { released.Add(failure); }
            throw CompleteFailure(attempt, exchange, new AggregateException(released), [], read, false, ct);
        }
        _limiter.ReportHttpOutcome(identity, (RequestResult.Success, false), null, null, isRead: read);
        attempt.TryComplete(RequestResult.Success, null, exchange.Wait, false, exchange.WaitStarted);
        return (result, uploaded, (int)response.StatusCode);
    }

    // The body joins before anything it may be writing to closes; every step runs and each failure is kept.
    private static async Task<List<Exception>?> ReleaseAsync(Exchange exchange, HttpResponseMessage? response,
        Func<ValueTask>? abort)
    {
        List<Exception>? failures = null;
        try { if (abort is not null) await abort().ConfigureAwait(false); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { if (exchange.Content is { } content) await content.StopAsync().ConfigureAwait(false); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { if (exchange.Spool is { } spool) await spool.DisposeAsync().ConfigureAwait(false); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { if (exchange.Input is { } input) await input.DisposeAsync().ConfigureAwait(false); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { response?.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { exchange.Request?.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        return failures;
    }

    private static DropboxRoute RequireRoute(string name) =>
        DropboxRouteTable.Embedded.Find(name) ?? throw new ArgumentException(null, nameof(name));

    private Task<HttpResponseMessage> SendAsync(DropboxRoute route, string json, Stream? body, ContentBlockHasher? hash,
        Exchange exchange, (string AccountId, string RouteName, string SourceIdentity) identity, CancellationToken ct) =>
        _limiter.SendAsync(route.Policy == DropboxRoutePolicy.Read, identity,
            async () => exchange.Request = await BuildRequestAsync(route, json, body, hash, exchange, ct).ConfigureAwait(false),
            (request, option, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                exchange.Dispatched = true;
                return _http.SendAsync(request, option, cancellation);
            }, HttpCompletionOption.ResponseHeadersRead, ct);

    private async Task<HttpRequestMessage> BuildRequestAsync(DropboxRoute route, string json, Stream? body,
        ContentBlockHasher? hash, Exchange exchange, CancellationToken ct)
    {
        if (route.Style == DropboxRouteStyle.Upload)
        {
            ArgumentNullException.ThrowIfNull(body);
            if (!body.CanSeek) throw new ArgumentException(null, nameof(body));
        }
        var token = route.IsNoAuth ? null : await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var request = new HttpRequestMessage(HttpMethod.Post, route.Endpoint)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        try
        {
            if (!route.IsNoAuth)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(DropboxProtocol.BearerScheme, token);
                if (route.Name != DropboxProtocol.GetCurrentAccount)
                    request.Headers.Add(DropboxProtocol.PathRootHeader, _pathRoot);
            }
            if (route.Style == DropboxRouteStyle.Rpc)
                request.Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);
            else
            {
                request.Headers.Add(DropboxProtocol.ArgHeader, DropboxArgHeader.Encode(json));
                if (route.Style == DropboxRouteStyle.Upload)
                {
                    exchange.Content = new UploadContent(body!, hash);
                    request.Content = exchange.Content;
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(DropboxProtocol.OctetStream);
                }
            }
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    // A supplied wait is observed at header receipt. A success publishes it now, so a blocked or abandoned body cannot
    // lose it; a refusal publishes it with its one classified feedback, before any cleanup can fail.
    private async Task AcceptHeadersAsync(HttpResponseMessage response, Exchange exchange,
        (string AccountId, string RouteName, string SourceIdentity) identity, bool read, CancellationToken ct)
    {
        var retry = response.Headers.RetryAfter;
        var header = retry?.Delta ?? (retry?.Date is { } date ? Later(date - time.GetUtcNow()) : (TimeSpan?)null);
        exchange.Wait = DropboxProtocol.ProviderWait(header, default);
        exchange.WaitStarted = exchange.Wait is null ? null : time.GetTimestamp();
        if (response.IsSuccessStatusCode)
        {
            PublishObservation(exchange);
            return;
        }
        var (summary, failure, writeThrottled, wait) = await ParseErrorAsync(response, exchange, header, ct).ConfigureAwait(false);
        if (wait != exchange.Wait)
        {
            exchange.Wait = wait;
            exchange.WaitStarted = time.GetTimestamp();
        }
        var error = new DropboxApiException(response.StatusCode, summary) { Route = identity.RouteName };
        error.Data[typeof(JsonElement)] = failure;
        if (writeThrottled || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            error.Data[typeof(bool)] = true;
        bool? throttled = writeThrottled ? true : HttpThrottled(error) ? false : null;
        exchange.Reported = true;
        try
        {
            _limiter.ReportHttpOutcome(identity, Classify(error, read, exchange, false, ct), throttled,
                Constraint(exchange));
        }
        catch (Exception notification)
        {
            exchange.Feedback = notification;
        }
        throw error;
    }

    private static TimeSpan Later(TimeSpan remaining) => remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;

    private DropboxProtocol.ProviderConstraint? Constraint(Exchange exchange) =>
        DropboxProtocol.ProviderConstraintFor(accountId, exchange.Wait, exchange.WaitStarted ?? 0);

    // Publishes a learned wait that no feedback carried; once only, and cleanup failure cannot discard it.
    private void PublishObservation(Exchange exchange)
    {
        if (exchange.Reported || Constraint(exchange) is not { } floor) return;
        exchange.Reported = true;
        FailureRetryOwner.InstallProviderConstraint(floor.Scope, floor.StartedTimestamp, floor.Duration, time);
    }

    /// <summary>Returns the retained structured provider union, never a re-parsed error summary.</summary>
    internal static JsonElement Failure(DropboxApiException error) =>
        error.Data[typeof(JsonElement)] is JsonElement failure ? failure : default;

    /// <summary>Whether this refusal was already reported as an HTTP throttle, so no caller may reset on it.</summary>
    internal static bool HttpThrottled(DropboxApiException error) => error.Data.Contains(typeof(bool));

    private static async Task<(string Summary, JsonElement Failure, bool WriteThrottled, TimeSpan? ProviderWait)>
        ParseErrorAsync(HttpResponseMessage response, Exchange exchange, TimeSpan? header, CancellationToken ct)
    {
        // The bounded reader alone: the error body stays open in the exchange, so an upload it answers can join first.
        var oversized = Text.Get(TextKey.DropboxRefused);
        SyncResponseBody.RequireWithinSize(response, DropboxApiLimits.ErrorBodyMaxBytes, oversized);
        exchange.Input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var text = await SyncResponseBody.ReadTextAsync(exchange.Input, DropboxApiLimits.ErrorBodyMaxBytes,
            Timeout.InfiniteTimeSpan, oversized, ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var failure = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(DropboxErrors.ErrorName, out var value)
                ? value.Clone() : default;
            return (DropboxErrors.Summary(root), failure, DropboxErrors.IsWriteThrottle(root),
                DropboxProtocol.ProviderWait(header, failure));
        }
        catch (JsonException)
        {
            return (string.Empty, default, false, DropboxProtocol.ProviderWait(header, default));
        }
    }

    private static (RequestResult Result, bool NoEffectProven) Classify(Exception error, bool read, Exchange exchange,
        bool sourceFailure, CancellationToken ct)
    {
        var noEffect = read || !exchange.Dispatched || ((exchange.Content is null || exchange.ReplayDirect) &&
            error is DropboxApiException { StatusCode: HttpStatusCode.TooManyRequests });
        var result = !noEffect ? RequestResult.UnknownWrite
            : ct.IsCancellationRequested ? RequestResult.CallerStopped
            : ProviderTransientFailure.IsTransient(error, ct) ||
                (sourceFailure && error is IOException && error is not InvalidDataException)
                ? RequestResult.TransientFailure : RequestResult.PermanentFailure;
        return (result, noEffect);
    }

    // A notification or cleanup failure ends the attempt: it is surfaced with the original error and never retried.
    private static Exception CompleteFailure(RequestAttempt? attempt, Exchange exchange, Exception error,
        List<Exception>? cleanup, bool read, bool sourceFailure, CancellationToken ct)
    {
        if (cleanup is null && exchange.Feedback is null)
        {
            var (result, noEffect) = Classify(error, read, exchange, sourceFailure, ct);
            attempt?.TryComplete(result, error, exchange.Wait, noEffect, exchange.WaitStarted);
            return error;
        }
        List<Exception> failures = [error];
        if (exchange.Feedback is { } feedback) failures.Add(feedback);
        if (cleanup is not null) failures.AddRange(cleanup);
        var terminal = new AggregateException(failures);
        attempt?.TryComplete(!read && exchange.Dispatched ? RequestResult.UnknownWrite : RequestResult.PermanentFailure,
            terminal, exchange.Wait, !exchange.Dispatched, exchange.WaitStarted);
        return terminal;
    }

    // One wire exchange's owned resources and learned facts, kept apart from request disposal.
    private sealed class Exchange
    {
        internal HttpRequestMessage? Request;
        internal UploadContent? Content;
        internal Stream? Input;
        internal Stream? Spool;
        internal bool ReplayDirect;
        internal bool Dispatched;
        internal bool Reported;
        internal TimeSpan? Wait;
        internal long? WaitStarted;
        internal Exception? Feedback;
    }
}
