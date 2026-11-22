using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The HTTP and response-consumption boundary;</summary>
public sealed class DropboxApi
{
    private const long BytesPerMiB = 1024L * 1024L;
    private const int ErrorBodyMaxBytes = 64 * 1024;
    private const int LongpollMaxSeconds = 480;
    private const int DateResolutionSeconds = 1;
    private const string BootstrapAccountRoute = "users/get_current_account";
    private const string SpaceUsageRoute = "users/get_space_usage";
    private const string LongpollRoute = "files/list_folder/longpoll";

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

    /// <summary>One account-scoped binding (events, credential and verified namespace) that shares this host's
    /// single limiter, so all pair clients use one RateLimit (W24).</summary>
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

    /// <summary>Send a prepared request through the single catalog-validated owner: a known route gets its
    /// credential and verified path root and its policy/style checked; a non-catalog (bootstrap/OAuth) endpoint is
    /// sent without credentials. The caller owns the request and its content.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = Routes.NameOf(request.RequestUri);
        if (name is null) return await SendPreparedAsync(request, null, ct).ConfigureAwait(false);
        if (Routes.PolicyOf(name) == Routes.Policy.Denied) throw new ArgumentException(null, nameof(request));
        var expectedContent = Routes.StyleOf(name) == Routes.Style.Upload;
        if ((request.Content is not null) != expectedContent) throw new ArgumentException(null, nameof(request));
        await using var permit = await _limiter.Value.AdmitAsync(ct).ConfigureAwait(false);
        var success = false;
        try
        {
            var response = await SendPreparedAsync(request, name, ct).ConfigureAwait(false);
            success = true;
            return response;
        }
        finally
        {
            _limiter.Value.TransferFinished(success);
        }
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

    /// <summary>Waits on one cursor; the returned backoff is the source's own floor, applied by the feed owner.</summary>
    public async Task<bool> LongpollAsync(string cursor, CancellationToken ct)
    {
        Routes.RequireStyle(LongpollRoute, Routes.Style.Rpc);
        var argument = JsonSerializer.Serialize(new { cursor, timeout = LongpollMaxSeconds });
        using var document = await SendJsonAsync(LongpollRoute, argument, ct).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("changes", out var changes) && changes.GetBoolean();
    }

    /// <summary>Reads one list page body and decodes it through W11's streaming page decoder, which opens and owns
    /// its own delete-on-close spool (R26/R28). No page envelope is buffered here.</summary>
    public async Task<DropboxPage> ReadPageAsync(string route, string argumentJson, PairConfig pair,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pair);
        Routes.RequireStyle(route, Routes.Style.Rpc);
        var request = BuildRequest(route, argumentJson);
        await using var permit = await _limiter.Value.AdmitAsync(ct).ConfigureAwait(false);
        var success = false;
        try
        {
            using var response = await SendPreparedAsync(request, route, ct).ConfigureAwait(false);
            await using var body = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var page = await DropboxReply.ReadAsync(body, DropboxReplyShapes.Page(pair), ct).ConfigureAwait(false);
            success = true;
            return page;
        }
        finally
        {
            request.Dispose();
            _limiter.Value.TransferFinished(success);
        }
    }

    /// <summary>Streams one download reply; the returned body owns the response and the transfer slot until its
    /// terminal outcome, and EOF is not staging success.</summary>
    public async Task<Stream> DownloadAsync(string route, string argumentJson, CancellationToken ct)
    {
        Routes.RequireStyle(route, Routes.Style.Download);
        if (Routes.PolicyOf(route) != Routes.Policy.Read) throw new ArgumentException(null, nameof(route));
        var request = BuildRequest(route, argumentJson);
        var permit = await _limiter.Value.AdmitAsync(ct).ConfigureAwait(false);
        HttpResponseMessage? response = null;
        try
        {
            response = await SendPreparedAsync(request, route, ct).ConfigureAwait(false);
            var content = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new ResponseStream().Open(response, content, ct, outcome =>
            {
                _limiter.Value.TransferFinished(outcome == ResponseStream.BodyOutcome.Completed);
                Release(permit);
            });
        }
        catch
        {
            response?.Dispose();
            _limiter.Value.TransferFinished(false);
            Release(permit);
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
        await using var permit = await _limiter.Value.AdmitAsync(ct).ConfigureAwait(false);
        var success = false;
        try
        {
            using var response = await SendPreparedAsync(request, route, ct).ConfigureAwait(false);
            await using var stream = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var document = DropboxProtocol.Parse(stream);
            success = true;
            return document;
        }
        finally
        {
            request.Dispose();
            _limiter.Value.TransferFinished(success);
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

    private async Task<HttpResponseMessage> SendPreparedAsync(HttpRequestMessage request, string? route,
        CancellationToken ct)
    {
        var started = _clock.GetTimestamp();
        await ApplyBindingAsync(request, route, ct).ConfigureAwait(false);
        var response = await _transport
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            try { await ThrowFailureAsync(response, route, ct).ConfigureAwait(false); }
            finally { response.Dispose(); }
            throw new InvalidOperationException(nameof(SendPreparedAsync));
        }
        RecordClock(response, started);
        return response;
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

    // The approved uncertainty is the full measured round trip plus the Date header's one-second resolution; only a
    // successful dated reply replaces the sample, and the sample is published as one reference (never a split tuple).
    private void RecordClock(HttpResponseMessage response, long started)
    {
        if (response.Headers.Date is not { } serverDate) return;
        var uncertainty = _clock.GetElapsedTime(started) + TimeSpan.FromSeconds(DateResolutionSeconds);
        _clockOffset = new ClockSample(serverDate - _clock.GetUtcNow(), uncertainty);
    }

    private async Task ThrowFailureAsync(HttpResponseMessage response, string? route, CancellationToken ct)
    {
        var retryAfter = ParseRetryAfter(response);
        JsonElement failure = default;
        try
        {
            await using var stream = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var text = await ReadBoundedTextAsync(stream, ct).ConfigureAwait(false);
            if (text.Length > 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("error", out var error))
                        failure = error.Clone();
                }
                catch (JsonException) { }
            }
        }
        catch (Exception read) when (read is IOException or HttpRequestException) { }
        var wait = DropboxProtocol.ProviderWait(retryAfter, failure);
        _limiter.Value.SlowDown();
        var exception = new HttpRequestException(Text.Get(TextKey.DropboxRefused), null, response.StatusCode);
        exception.Data["Route"] = route ?? string.Empty;
        exception.Data["RetryAfterTicks"] = wait?.Ticks ?? 0L;
        exception.Data["Dispatched"] = true;
        if (failure.ValueKind != JsonValueKind.Undefined) exception.Data["Failure"] = failure;
        throw exception;
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

    private static void Release(IAsyncDisposable permit)
    {
        try { permit.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception) { }
    }

    private static string PathRootJson(string namespaceId) => JsonSerializer.Serialize(
        new Dictionary<string, string>(StringComparer.Ordinal) { [".tag"] = "root", ["root"] = namespaceId });
}
