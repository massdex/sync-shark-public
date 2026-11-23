using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The HTTP and response-consumption boundary;</summary>
public sealed class DropboxApi
{
    private const int ErrorBodyMaxBytes = 64 * 1024;
    private const int LongpollMaxSeconds = 480;
    // Established client allowance beyond the provider's requested poll duration.
    private const int LongpollClientAllowanceSeconds = 90;
    private const int DateResolutionSeconds = 1;
    private const int ConnectAllowanceSeconds = 20;
    private const int MaximumWriteSliceBytes = 64 * 1024;
    private static readonly TimeSpan LongpollAllowance = TimeSpan.FromSeconds(LongpollMaxSeconds + LongpollClientAllowanceSeconds);
    private const string BootstrapAccountRoute = "users/get_current_account";
    private const string SpaceUsageRoute = "users/get_space_usage";
    private const string LongpollRoute = "files/list_folder/longpoll";

    private readonly HttpClient _transport;
    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, ValueTask<string?>>? _accessToken;
    private readonly string? _pathRootNamespaceId;
    private readonly Lazy<RateLimit> _limiter;
    private readonly DropboxApi _host;
    private static readonly HttpRequestOptionsKey<(long Timestamp, DateTimeOffset Wall)> ReceiptKey = new(nameof(ReceiptKey));
    private long? _busyStarted;
    private long _busyFloorReceipt;
    private TimeSpan _busyDuration;
    private ClockSample? _clockOffset;

    private sealed record ClockSample(long Received, TimeSpan Offset, TimeSpan Uncertainty);

    /// <summary>Create the HTTP/1.1 pool with progress bounds on connection reads and upload write slices.</summary>
    public static HttpClient CreateHttpClient(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var notify = Routes.Endpoint(LongpollRoute);
        var ordinary = ResponseStream.OrdinaryAllowance;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(ConnectAllowanceSeconds),
            PlaintextStreamFilter = (context, _) => ValueTask.FromResult<Stream>(new ResponseStream.ProviderActivityStream(
                context.PlaintextStream, Uri.Compare(context.InitialRequestMessage.RequestUri, notify,
                    UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0
                    ? LongpollAllowance : ordinary, ordinary, MaximumWriteSliceBytes, clock))
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    /// <summary>Uses the transport and clock supplied by the composition root for requests and response timing,
    /// and builds the single transport limiter from settings.InitialLimiterLevel, settings.Backoff and the sink.</summary>
    public DropboxApi(HttpClient transport, TimeProvider clock, EngineSettings settings, Action<RunEvent> events,
        Func<CancellationToken, ValueTask<string?>>? accessToken = null, string? pathRootNamespaceId = null)
        : this(transport, clock, accessToken, pathRootNamespaceId,
            new Lazy<RateLimit>(() => new RateLimit(settings.InitialLimiterLevel, settings.Backoff, events),
                LazyThreadSafetyMode.ExecutionAndPublication))
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(events);
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

    /// <summary>Bind an account while retaining the host limiter and its wait log.</summary>
    public DropboxApi CreateAccountScope(Action<RunEvent> events,
        Func<CancellationToken, ValueTask<string?>>? accessToken, string? pathRootNamespaceId)
    {
        ArgumentNullException.ThrowIfNull(events);
        return new DropboxApi(_transport, _clock, accessToken, pathRootNamespaceId, _limiter, _host);
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
        var root = document.RootElement;
        var allocation = root.GetProperty("allocation");
        var team = allocation.TryGetProperty(".tag", out var tag) &&
            string.Equals(tag.GetString(), "team", StringComparison.Ordinal);
        var used = team ? allocation.GetProperty("used").GetInt64() : root.GetProperty("used").GetInt64();
        var allocated = allocation.GetProperty("allocated").GetInt64();
        if (used < 0 || allocated < 0) throw new InvalidDataException(Text.Get(TextKey.FailureDetail));
        return (used, allocated);
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
        using var deadline = new CancellationTokenSource(LongpollAllowance, _clock);
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            return await ReadReplyAsync(LongpollRoute, JsonSerializer.Serialize(new { cursor, timeout = LongpollMaxSeconds }),
                DropboxReplyShapes.Longpoll, poll.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (ct.IsCancellationRequested || error is OperationCanceledException or TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            throw new ItemFailedException(string.Empty, TextKey.ReadDropboxFailed, error)
                { Disposition = FailureDisposition.TemporaryNoEffect };
        }
    }

    /// <summary>Stream one listing page into its attempt-owned spool.</summary>
    public Task<DropboxPage> ReadPageAsync(string route, string argumentJson, PairConfig pair, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return ReadReplyAsync(route, argumentJson, () => DropboxReplyShapes.Page(pair), ct);
    }

    private async Task<T> ReadReplyAsync<T>(string route, string argumentJson, Func<DropboxReply<T>> decoder, CancellationToken ct)
    {
        Routes.RequireStyle(route, Routes.Style.Rpc);
        using var request = BuildRequest(route, argumentJson);
        using var response = await SendPreparedAsync(request, route, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await ThrowFailureAsync(response, route, ct).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await DropboxReply.ReadAsync(body, decoder(), ct).ConfigureAwait(false);
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
        using var request = BuildRequest(route, argumentJson);
        IAsyncDisposable? bodyPermit = null;
        HttpResponseMessage? response = null;
        try
        {
            response = await SendPreparedAsync(request, route, ct, permit => bodyPermit = permit).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) await ThrowFailureAsync(response, route, ct).ConfigureAwait(false);
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

    private async Task<JsonDocument> SendJsonAsync(string route, string argumentJson, CancellationToken ct)
    {
        using var request = BuildRequest(route, argumentJson);
        using var response = await SendPreparedAsync(request, route, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await ThrowFailureAsync(response, route, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return DropboxProtocol.Parse(stream);
    }

    private static HttpRequestMessage BuildRequest(string route, string argumentJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(route))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (Routes.StyleOf(route) == Routes.Style.Rpc)
        {
            var payload = Routes.VoidArg(route) ? "null" : argumentJson;
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }
        else
        {
            request.Headers.Add("Dropbox-API-Arg", Routes.ArgHeader(argumentJson));
        }
        return request;
    }

    // Bind before admission so token refresh cannot deadlock. Sample wall time before elapsed time so a
    // scheduling gap enlarges the uncertainty rather than understating it.
    private async Task<HttpResponseMessage> SendPreparedAsync(HttpRequestMessage request, string? route,
        CancellationToken ct, Action<IAsyncDisposable>? retainPermit = null)
    {
        await ApplyBindingAsync(request, route, ct).ConfigureAwait(false);
        var takePermit = route is not null && !string.Equals(route, LongpollRoute, StringComparison.Ordinal);
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
            response.RequestMessage.Options.Set(ReceiptKey, (received, receivedWall));
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
        var bootstrap = string.Equals(route, BootstrapAccountRoute, StringComparison.Ordinal);
        string? token = null;
        if (_accessToken is { } source) token = await source(ct).ConfigureAwait(false);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (bootstrap) return;
        if (token is null) throw new InvalidOperationException(nameof(_accessToken));
        if (_pathRootNamespaceId is null) throw new InvalidOperationException(nameof(_pathRootNamespaceId));
        request.Headers.Add("Dropbox-API-Path-Root", PathRootJson(_pathRootNamespaceId));
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
        if (failure.Cause == TextKey.WaitTooManyWrites || !IsBusy(status)) return false;
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

    internal async Task ThrowFailureAsync(HttpResponseMessage response, string route, CancellationToken ct,
        string? readPath = null)
    {
        var received = ReceivedAt(response, _clock);
        var status = response.StatusCode;
        var header = RetryAfter(response, _clock);
        JsonElement envelope;
        try { envelope = await ReadErrorAsync(response, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (route == LongpollRoute && ct.IsCancellationRequested)
        {
            // The poll owns caller cancellation; its deadline cannot erase an already received refusal.
            envelope = default;
        }
        var read = Routes.PolicyOf(route) == Routes.Policy.Read;
        var failure = new DropboxErrors().Classify(status, envelope, route, false,
            new(read, true, read || status == HttpStatusCode.TooManyRequests, IsBusy(status)));
        var providerWait = DropboxProtocol.ProviderWait(header, envelope);
        if (route != LongpollRoute) ReportBusy(status, failure, providerWait, received);
        var wait = failure.Disposition == FailureDisposition.TemporaryNoEffect ? providerWait : null;
        if (readPath is not null && failure.Absent) return;
        var refusal = new HttpRequestException(Text.Get(TextKey.DropboxRefused), null, status);
        if (failure.CursorReset) throw new ListingCursorResetException(refusal);
        throw new ItemFailedException(readPath ?? string.Empty, failure.Cause, refusal)
        {
            Disposition = failure.Disposition, ProviderWait = wait, ProviderWaitStarted = wait.HasValue ? received : null
        };
    }

    internal static long ReceivedAt(HttpResponseMessage response, TimeProvider clock) =>
        response.RequestMessage?.Options.TryGetValue(ReceiptKey, out var receipt) == true
            ? receipt.Timestamp : clock.GetTimestamp();

    internal static TimeSpan? RetryAfter(HttpResponseMessage response, TimeProvider clock)
    {
        var retry = response.Headers.RetryAfter;
        if (retry is null) return null;
        if (retry.Delta is { } delta) return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        if (retry.Date is { } date)
        {
            var wall = response.RequestMessage?.Options.TryGetValue(ReceiptKey, out var receipt) == true
                ? receipt.Wall : clock.GetUtcNow();
            var remaining = date - wall;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    internal static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            // Do not promote a valid-looking prefix of an oversized envelope to structured evidence.
            var buffer = new byte[ErrorBodyMaxBytes + 1];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: ct)
                .ConfigureAwait(false);
            if (count > ErrorBodyMaxBytes) return default;
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, count));
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException or TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            return default; // Unusable body; the caller still owns the status and header.
        }
    }

    private static bool IsBusy(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    private static string PathRootJson(string namespaceId) => JsonSerializer.Serialize(
        new Dictionary<string, string>(StringComparer.Ordinal) { [".tag"] = "root", ["root"] = namespaceId });
}
