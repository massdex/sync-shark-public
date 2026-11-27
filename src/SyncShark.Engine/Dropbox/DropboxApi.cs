using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The HTTP and response-consumption boundary;</summary>
public sealed class DropboxApi
{
    // Dropbox HTTP reference #files-list_folder-longpoll: the requested server wait is at most 480 s.
    private const int LongpollMaxSeconds = 480;
    // Established client allowance beyond the provider's requested poll duration (known-facts D12, D16).
    private const int LongpollClientAllowanceSeconds = 90;
    // HTTP-date resolution is one second (RFC 9110 5.6.7).
    private const int DateResolutionSeconds = 1;
    private static readonly TimeSpan LongpollAllowance = TimeSpan.FromSeconds(LongpollMaxSeconds + LongpollClientAllowanceSeconds);
    private const string SpaceUsageRoute = "users/get_space_usage";

    private readonly HttpClient _transport;
    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, ValueTask<string?>>? _accessToken;
    private readonly string? _pathRootNamespaceId;
    private readonly Lazy<RateLimit> _limiter;
    private readonly DropboxApi _host;
    private readonly ConcurrentDictionary<string, DropboxApi>? _accounts; // process host only: one root per verified AccountId
    private readonly Func<RateLimit>? _accountLimiter;                    // process host only: builds an account root's limiter
    private long? _busyStarted;
    private long _busyFloorReceipt;
    private TimeSpan _busyDuration;
    private ClockSample? _clockOffset;

    private sealed record ClockSample(long Received, TimeSpan Offset, TimeSpan Uncertainty);

    /// <summary>Create the HTTP/1.1 pool with progress bounds on connection reads and upload write slices.</summary>
    public static HttpClient CreateHttpClient(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var notify = Routes.Endpoint(DropboxProtocol.ListFolderLongpoll);
        var ordinary = ResponseStream.OrdinaryAllowance;
        return DropboxHttp.CreateHttpClient((context, _) => ValueTask.FromResult<Stream>(new ProviderActivityStream(
                context.PlaintextStream, Uri.Compare(context.InitialRequestMessage.RequestUri, notify,
                    UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0
                    ? LongpollAllowance : ordinary, ordinary, clock)));
    }

    /// <summary>Uses the transport and clock supplied by the composition root; builds this host's limiter for unbound calls and,
    /// per verified account, that account's limiter (CreateAccountScope), from settings.InitialLimiterLevel, settings.Backoff and the sink.</summary>
    public DropboxApi(HttpClient transport, TimeProvider clock, EngineSettings settings, Action<RunEvent> events,
        Func<CancellationToken, ValueTask<string?>>? accessToken = null, string? pathRootNamespaceId = null)
        : this(transport, clock, accessToken, pathRootNamespaceId,
            new Lazy<RateLimit>(() => new RateLimit(settings.InitialLimiterLevel, settings.Backoff, events),
                LazyThreadSafetyMode.ExecutionAndPublication))
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(events);
        _accounts = new(StringComparer.Ordinal);
        _accountLimiter = () => new RateLimit(settings.InitialLimiterLevel, settings.Backoff, events);
    }

    private DropboxApi(HttpClient transport, TimeProvider clock,
        Func<CancellationToken, ValueTask<string?>>? accessToken, string? pathRootNamespaceId, Lazy<RateLimit> limiter, DropboxApi? host = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limiter);
        _transport = transport;
        _clock = clock;
        _accessToken = accessToken;
        _pathRootNamespaceId = pathRootNamespaceId;
        _limiter = limiter;
        _host = host ?? this;
    }

    /// <summary>Bind a verified account: every scope of one AccountId shares that account's limiter and busy period.</summary>
    public DropboxApi CreateAccountScope(string accountId,
        Func<CancellationToken, ValueTask<string?>>? accessToken, string? pathRootNamespaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var root = (_accounts ?? throw new InvalidOperationException()).GetOrAdd(accountId, _ =>
            new DropboxApi(_transport, _clock, null, null,
                new Lazy<RateLimit>(_accountLimiter!, LazyThreadSafetyMode.ExecutionAndPublication)));
        return new DropboxApi(_transport, _clock, accessToken, pathRootNamespaceId, root._limiter, root);
    }

    /// <summary>Latest successful dated reply clock sample (signed offset and measured uncertainty).</summary>
    public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset
    {
        get
        {
            var sample = Volatile.Read(ref _clockOffset);
            return sample is null ? null : (sample.Offset, sample.Uncertainty);
        }
    }

    /// <summary>The clock supplied by the composition root.</summary>
    internal TimeProvider Clock => _clock;

    /// <summary>Send one prepared request; the caller owns its response, including a provider refusal.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendPreparedAsync(request, Routes.NameOf(request.RequestUri), ct).ConfigureAwait(false);
    }

    /// <summary>Read Dropbox storage usage using the account route; the void body is the provider's JSON null.</summary>
    public async Task<(long Used, long Allocated)> SpaceUsageAsync(CancellationToken ct)
    {
        Routes.RequireStyle(SpaceUsageRoute, Routes.Style.Rpc);
        using var document = await SendJsonAsync(SpaceUsageRoute, "{}", ct).ConfigureAwait(false);
        return DropboxHttp.SpaceUsage(document.RootElement);
    }

    /// <summary>Sends one JSON route call that fully consumes its bounded reply; the caller owns the document.</summary>
    public Task<JsonDocument> CallJsonAsync(string route, string argumentJson, CancellationToken ct)
    {
        Routes.RequireStyle(route, Routes.Style.Rpc);
        return SendJsonAsync(route, argumentJson, ct);
    }

    /// <summary>Read changes and the next-poll delay for this cursor without taking a transfer permit.</summary>
    public async Task<(bool Changes, TimeSpan? Backoff)> LongpollAsync(string cursor, CancellationToken ct)
    {
        try
        {
            return await ReadReplyAsync(DropboxProtocol.ListFolderLongpoll, JsonSerializer.Serialize(new { cursor, timeout = LongpollMaxSeconds }),
                DropboxReplyShapes.Longpoll, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (ct.IsCancellationRequested || error is OperationCanceledException or TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            throw new ItemFailedException(string.Empty, TextKey.ReadDropboxFailed, error)
                { Disposition = FailureDisposition.TemporaryNoEffect };
        }
    }

    /// <summary>Stream one listing page into its attempt-owned spool.</summary>
    public Task<DropboxPage> ReadPageAsync(string route, string argumentJson, PairConfig pair, CancellationToken ct, bool isolate = false)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return ReadReplyAsync(route, argumentJson, () => DropboxReplyShapes.Page(pair, isolate), ct);
    }

    private async Task<T> ReadReplyAsync<T>(string route, string argumentJson, Func<DropboxReply<T>> decoder, CancellationToken ct)
    {
        Routes.RequireStyle(route, Routes.Style.Rpc);
        using var request = DropboxHttp.BuildRequest(route, argumentJson);
        using var deadline = route == DropboxProtocol.ListFolderLongpoll ? new CancellationTokenSource(LongpollAllowance, _clock) : null;
        using var poll = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var requestToken = poll?.Token ?? ct;
        using var response = await SendPreparedAsync(request, route, requestToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            deadline?.Dispose(); // Once refused, only caller cancellation and read inactivity apply.
            await DropboxHttp.ThrowFailureAsync(this, response, route, ct).ConfigureAwait(false);
        }
        await using var body = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
        return await DropboxReply.ReadAsync(body, decoder(), requestToken).ConfigureAwait(false);
    }

    /// <summary>Streams one download reply with no outcome observer; the returned body owns the response.</summary>
    public Task<Stream> DownloadAsync(string route, string argumentJson, CancellationToken ct) =>
        DownloadAsync(route, argumentJson, null, ct);

    /// <summary>Streams one download reply and reports its terminal body outcome to the caller's download attempt
    /// owner, outside the read classifier; EOF is not staging success.</summary>
    public async Task<Stream> DownloadAsync(string route, string argumentJson,
        Action<ResponseStream.BodyOutcome>? ended, CancellationToken ct)
    {
        Routes.RequireStyle(route, Routes.Style.Download);
        if (Routes.PolicyOf(route) != Routes.Policy.Read) throw new ArgumentException(null, nameof(route));
        using var request = DropboxHttp.BuildRequest(route, argumentJson);
        IAsyncDisposable? bodyPermit = null;
        HttpResponseMessage? response = null;
        try
        {
            response = await SendPreparedAsync(request, route, ct, permit => bodyPermit = permit).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) await DropboxHttp.ThrowFailureAsync(this, response, route, ct).ConfigureAwait(false);
            var content = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new ResponseStream().Open(response, content, ct, outcome =>
            {
                try { TransferFinished(outcome == ResponseStream.BodyOutcome.Completed); }
                finally { bodyPermit!.DisposeAsync().GetAwaiter().GetResult(); } // The limiter releases synchronously.
                ended?.Invoke(outcome);
            });
        }
        catch
        {
            try { response?.Dispose(); }
            finally { if (bodyPermit is not null) await bodyPermit.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }

    /// <summary>The shared limiter's terminal feedback for a completed file transfer, never a content chunk.</summary>
    internal Action<bool> TransferFinished => _limiter.Value.TransferFinished;

    /// <summary>Waits through the shared transport limiter.</summary>
    public Task WaitAsync(TimeSpan delay, CancellationToken ct) => _limiter.Value.WaitAsync(delay, _clock, ct);

    /// <summary>The adopted backoff delay after this many consecutive transient failures.</summary>
    public TimeSpan RetryDelay(int failures) => _limiter.Value.RetryDelay(failures);

    private async Task<JsonDocument> SendJsonAsync(string route, string argumentJson, CancellationToken ct)
    {
        using var request = DropboxHttp.BuildRequest(route, argumentJson);
        using var response = await SendPreparedAsync(request, route, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await DropboxHttp.ThrowFailureAsync(this, response, route, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return DropboxProtocol.Parse(stream);
    }

    // Bind before admission so token refresh cannot deadlock. Sample wall time before elapsed time so a
    // scheduling gap enlarges the uncertainty rather than understating it.
    private async Task<HttpResponseMessage> SendPreparedAsync(HttpRequestMessage request, string? route,
        CancellationToken ct, Action<IAsyncDisposable>? retainPermit = null)
    {
        await ApplyBindingAsync(request, route, ct).ConfigureAwait(false);
        var takePermit = route is not null && !string.Equals(route, DropboxProtocol.ListFolderLongpoll, StringComparison.Ordinal);
        IAsyncDisposable? permit = null;
        try
        {
            if (takePermit)
                permit = await _limiter.Value.AdmitAsync(ct).ConfigureAwait(false);
            var started = _clock.GetTimestamp();
            var response = await _transport
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var received = _clock.GetTimestamp();
            var receivedWall = _clock.GetUtcNow();
            response.RequestMessage ??= request;
            response.RequestMessage.Options.Set(DropboxHttp.ReceiptKey, (received, receivedWall));
            if (response.IsSuccessStatusCode)
            {
                RecordClock(response, received, receivedWall, _clock.GetElapsedTime(started));
                if (retainPermit is not null && permit is not null)
                {
                    retainPermit(permit);
                    permit = null;
                }
            }
            return response;
        }
        finally
        {
            if (permit is not null) await permit.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ApplyBindingAsync(HttpRequestMessage request, string? route, CancellationToken ct)
    {
        if (route is null || Routes.IsNoAuth(route)) return;
        string? token = null;
        try { if (_accessToken is { } source) token = await source(ct).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            throw new ItemFailedException(string.Empty, TextKey.RequestIncomplete, error) { Disposition = FailureDisposition.TemporaryNoEffect };
        }
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue(DropboxProtocol.BearerScheme, token);
        if (route == DropboxProtocol.GetCurrentAccount) return;
        if (token is null) throw new InvalidOperationException(nameof(_accessToken));
        if (_pathRootNamespaceId is null) throw new InvalidOperationException(nameof(_pathRootNamespaceId));
        request.Headers.Add(DropboxProtocol.PathRootHeader, DropboxHttp.PathRootJson(_pathRootNamespaceId));
    }

    private void RecordClock(HttpResponseMessage response, long received, DateTimeOffset receivedWall, TimeSpan elapsed)
    {
        if (response.Headers.Date is not { } serverDate) return;
        var sample = new ClockSample(received, serverDate - receivedWall, elapsed + TimeSpan.FromSeconds(DateResolutionSeconds));
        var previous = Volatile.Read(ref _clockOffset);
        while (previous is null || previous.Received < received)
        {
            var observed = Interlocked.CompareExchange(ref _clockOffset, sample, previous);
            if (ReferenceEquals(observed, previous)) return;
            previous = observed;
        }
    }

    // Raw callers already own the error stream: they report its classified HTTP refusal once, not each batch member.
    internal void ReportBusy(HttpStatusCode status, DropboxErrors.Classified failure, TimeSpan? wait, long received)
    {
        lock (_limiter)
        {
            if (_host.BeginRequestThrottle(status, failure, wait, received)) _limiter.Value.SlowDown();
        }
    }

    // Called under the host limiter lock. The period coalesces feedback; it does not delay admission.
    private bool BeginRequestThrottle(HttpStatusCode status, DropboxErrors.Classified failure, TimeSpan? wait, long received)
    {
        if (failure.Cause == TextKey.WaitTooManyWrites || !DropboxHttp.IsBusy(status)) return false;
        if (_busyStarted is { } start && received < start) return false;
        var duration = wait.GetValueOrDefault();
        var elapsed = _clock.GetElapsedTime(_busyFloorReceipt, received);
        if (!_busyStarted.HasValue || elapsed >= _busyDuration)
        {
            _busyStarted = _busyFloorReceipt = received;
            _busyDuration = duration;
            return true;
        }
        // Compare ends without converting provider seconds to clock ticks or overflowing a deadline.
        if (elapsed >= TimeSpan.Zero ? duration > _busyDuration - elapsed : duration + elapsed > _busyDuration)
        {
            _busyFloorReceipt = received;
            _busyDuration = duration;
        }
        return false;
    }
}
