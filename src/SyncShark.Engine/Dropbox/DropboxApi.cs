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
    private const int DateResolutionSeconds = 1;
    private const string BootstrapAccountRoute = "users/get_current_account";
    private const string SpaceUsageRoute = "users/get_space_usage";
    private const string LongpollRoute = "files/list_folder/longpoll";
    private const string ContinueRoute = "files/list_folder/continue";
    private const string ErrorSummaryField = "error_summary";
    private const string ResetTag = "reset";

    private readonly HttpClient _transport;
    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, ValueTask<string?>>? _accessToken;
    private readonly string? _pathRootNamespaceId;
    private readonly Lazy<RateLimit> _limiter;
    private volatile ClockSample? _clockOffset;

    private sealed record ClockSample(TimeSpan Offset, TimeSpan Uncertainty);

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
        Func<CancellationToken, ValueTask<string?>>? accessToken, string? pathRootNamespaceId, Lazy<RateLimit> limiter)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limiter);
        _transport = transport;
        _clock = clock;
        _accessToken = accessToken;
        _pathRootNamespaceId = pathRootNamespaceId;
        _limiter = limiter;
    }

    /// <summary>One account-scoped binding sharing this host's single limiter, so all pair clients use one RateLimit
    /// (W24). The shared limiter keeps the host's run sink as the one wait-log owner.</summary>
    public DropboxApi CreateAccountScope(Action<RunEvent> events,
        Func<CancellationToken, ValueTask<string?>>? accessToken, string? pathRootNamespaceId)
    {
        ArgumentNullException.ThrowIfNull(events);
        return new DropboxApi(_transport, _clock, accessToken, pathRootNamespaceId, _limiter);
    }

    /// <summary>Latest successful dated reply clock sample (signed offset and measured uncertainty).</summary>
    public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset
    {
        get
        {
            var sample = _clockOffset;
            return sample is null ? null : (sample.Offset, sample.Uncertainty);
        }
    }

    /// <summary>Send a prepared request through the single transport: a known catalog route gets the account's token
    /// and verified path root; a non-catalog (bootstrap/OAuth) endpoint is sent without credentials. The status and
    /// body are returned to the caller for W10/W14 classification; nothing is rejected by body shape (R35).</summary>
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

    /// <summary>Waits on one cursor and returns its changes with the provider's backoff so the feed owner can defer
    /// only that source's next longpoll; no limit is occupied while it waits (R38).</summary>
    public async Task<(bool Changes, TimeSpan? Backoff)> LongpollAsync(string cursor, CancellationToken ct)
    {
        Routes.RequireStyle(LongpollRoute, Routes.Style.Rpc);
        var argument = JsonSerializer.Serialize(new { cursor, timeout = LongpollMaxSeconds });
        using var document = await SendJsonAsync(LongpollRoute, argument, ct).ConfigureAwait(false);
        var root = document.RootElement;
        var changes = root.TryGetProperty("changes", out var changesElement) && changesElement.GetBoolean();
        var defer = root.TryGetProperty("backoff", out var backoffElement) &&
            backoffElement.TryGetDouble(out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        return (changes, defer);
    }

    /// <summary>Reads one list page body and decodes it through W11's streaming page decoder, which opens and owns
    /// its own delete-on-close spool (R26/R28). No page envelope is buffered here.</summary>
    public async Task<DropboxPage> ReadPageAsync(string route, string argumentJson, PairConfig pair,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pair);
        Routes.RequireStyle(route, Routes.Style.Rpc);
        var request = BuildRequest(route, argumentJson);
        try
        {
            using var response = await SendPreparedAsync(request, route, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) await ThrowFailureAsync(response, route, ct).ConfigureAwait(false);
            await using var body = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await DropboxReply.ReadAsync(body, DropboxReplyShapes.Page(pair), ct).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
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
        var request = BuildRequest(route, argumentJson);
        HttpResponseMessage? response = null;
        try
        {
            response = await SendPreparedAsync(request, route, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) await ThrowFailureAsync(response, route, ct).ConfigureAwait(false);
            var content = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new ResponseStream().Open(response, content, ct, ended);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>Waits through the shared transport limiter.</summary>
    public Task WaitAsync(TimeSpan delay, CancellationToken ct) => _limiter.Value.WaitAsync(delay, _clock, ct);

    private async Task<JsonDocument> SendJsonAsync(string route, string argumentJson, CancellationToken ct)
    {
        var request = BuildRequest(route, argumentJson);
        try
        {
            using var response = await SendPreparedAsync(request, route, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) await ThrowFailureAsync(response, route, ct).ConfigureAwait(false);
            await using var stream = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return DropboxProtocol.Parse(stream);
        }
        finally
        {
            request.Dispose();
        }
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

    // Binding runs before any permit (a refresh issues its own no-permit token call), and timing starts after
    // admission; the wall clock is read at header receipt before the elapsed read so a pause can only overstate
    // uncertainty, never understate it (data safety, BC15). Longpoll and non-catalog (bootstrap/OAuth) calls are idle
    // or binding calls and take no permit; every other HTTP call admits exactly once (R35/R38).
    private async Task<HttpResponseMessage> SendPreparedAsync(HttpRequestMessage request, string? route,
        CancellationToken ct)
    {
        await ApplyBindingAsync(request, route, ct).ConfigureAwait(false);
        var takePermit = route is not null && !string.Equals(route, LongpollRoute, StringComparison.Ordinal);
        IAsyncDisposable? permit = null;
        var admitted = false;
        var success = false;
        try
        {
            if (takePermit)
            {
                permit = await _limiter.Value.AdmitAsync(ct).ConfigureAwait(false);
                admitted = true;
            }
            var started = _clock.GetTimestamp();
            var response = await _transport
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var receivedWall = _clock.GetUtcNow();
            var elapsed = _clock.GetElapsedTime(started);
            success = response.IsSuccessStatusCode;
            if (admitted && IsBusy(response.StatusCode)) _limiter.Value.SlowDown();
            if (success) RecordClock(response, receivedWall, elapsed);
            return response;
        }
        finally
        {
            if (admitted) _limiter.Value.TransferFinished(success);
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

    private void RecordClock(HttpResponseMessage response, DateTimeOffset receivedWall, TimeSpan elapsed)
    {
        if (response.Headers.Date is not { } serverDate) return;
        _clockOffset = new ClockSample(serverDate - receivedWall, elapsed + TimeSpan.FromSeconds(DateResolutionSeconds));
    }

    private async Task ThrowFailureAsync(HttpResponseMessage response, string? route, CancellationToken ct)
    {
        var status = response.StatusCode;
        var retryAfter = ParseRetryAfter(response);
        JsonElement body = default;
        string? summary = null;
        try
        {
            await using var stream = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var text = await ReadBoundedTextAsync(stream, ct).ConfigureAwait(false);
            if (text.Length > 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    body = document.RootElement.Clone();
                    if (body.ValueKind == JsonValueKind.Object &&
                        body.TryGetProperty(ErrorSummaryField, out var summaryElement))
                        summary = summaryElement.GetString();
                }
                catch (JsonException) { }
            }
        }
        catch (Exception read) when (read is IOException or HttpRequestException) { }

        if (string.Equals(route, ContinueRoute, StringComparison.Ordinal) &&
            status == HttpStatusCode.Conflict && IsReset(body, summary))
            throw new ListingCursorResetException(
                new HttpRequestException(Text.Get(TextKey.DropboxRefused), null, status));

        var evidence = new DropboxErrors.RequestEvidence(false, true, false, IsBusy(status));
        var classified = new DropboxErrors().Classify(status, body, route ?? string.Empty, expectedIsNull: false, evidence);
        var wait = DropboxProtocol.ProviderWait(retryAfter, body);
        var exception = new HttpRequestException(Text.Get(TextKey.DropboxRefused), null, status);
        exception.Data["Route"] = route ?? string.Empty;
        exception.Data["RetryAfterTicks"] = wait?.Ticks ?? 0L;
        exception.Data["Dispatched"] = true;
        exception.Data["Classified"] = classified;
        if (body.ValueKind != JsonValueKind.Undefined) exception.Data["Body"] = body;
        throw exception;
    }

    private static bool IsReset(JsonElement body, string? summary)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object && error.TryGetProperty(".tag", out var tag) &&
            string.Equals(tag.GetString(), ResetTag, StringComparison.Ordinal)) return true;
        return summary is not null && summary.StartsWith(ResetTag, StringComparison.Ordinal);
    }

    private TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry is null) return null;
        if (retry.Delta is { } delta) return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        if (retry.Date is { } date)
        {
            var remaining = date - _clock.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    private static async Task<string> ReadBoundedTextAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[ErrorBodyMaxBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static bool IsBusy(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    private static string PathRootJson(string namespaceId) => JsonSerializer.Serialize(
        new Dictionary<string, string>(StringComparer.Ordinal) { [".tag"] = "root", ["root"] = namespaceId });
}
