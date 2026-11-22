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
    private const string GetCurrentAccount = "users/get_current_account";

    private readonly HttpClient _transport;
    private readonly TimeProvider _clock;
    private readonly EngineSettings _settings;
    private readonly Action<RunEvent> _events;
    private readonly Func<CancellationToken, ValueTask<string?>>? _accessToken;
    private readonly string? _pathRootNamespaceId;
    private RateLimit? _limiter;
    private (TimeSpan Offset, TimeSpan Uncertainty)? _clockOffset;
    private TimeSpan? _longpollFloor;

    /// <summary>Uses the transport and clock supplied by the composition root for requests and response timing.
    /// The token source and namespace id, when supplied, bind authenticated requests to one verified account.</summary>
    public DropboxApi(HttpClient transport, TimeProvider clock, EngineSettings settings, Action<RunEvent> events,
        Func<CancellationToken, ValueTask<string?>>? accessToken = null, string? pathRootNamespaceId = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(events);
        _transport = transport;
        _clock = clock;
        _settings = settings;
        _events = events;
        _accessToken = accessToken;
        _pathRootNamespaceId = pathRootNamespaceId;
    }

    /// <summary>Latest successful reply offset and its measured uncertainty; no extra request.</summary>
    public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset => _clockOffset;

    /// <summary>Send the supplied prepared request and record the server clock from its Date header.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = _clock.GetTimestamp();
        var response = await _transport
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        RecordClock(response, started);
        return response;
    }

    /// <summary>Read Dropbox storage usage using the account route from the route catalog.</summary>
    public async Task<(long Used, long Allocated)> SpaceUsageAsync(CancellationToken ct)
    {
        using var response = await SendRouteAsync("users/get_space_usage", "{}", null, ct).ConfigureAwait(false);
        await using var stream = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var used = document.RootElement.GetProperty("used").GetInt64();
        var allocated = document.RootElement.GetProperty("allocation").GetProperty("allocated").GetInt64();
        return (used, allocated);
    }

    /// <summary>Sends one JSON route call and returns its parsed reply; the caller owns the document.</summary>
    public async Task<JsonDocument> CallJsonAsync(string route, string argumentJson, CancellationToken ct)
    {
        using var response = await SendRouteAsync(route, argumentJson, null, ct).ConfigureAwait(false);
        await using var stream = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Waits on one cursor; a returned backoff is installed as the floor for this source's next longpoll.</summary>
    public async Task<bool> LongpollAsync(string cursor, CancellationToken ct)
    {
        var argument = JsonSerializer.Serialize(new { cursor, timeout = LongpollMaxSeconds });
        if (_longpollFloor is { } floor && floor > TimeSpan.Zero)
            await Task.Delay(floor, _clock, ct).ConfigureAwait(false);
        using var response = await SendRouteAsync("files/list_folder/longpoll", argument, null, ct)
            .ConfigureAwait(false);
        await using var stream = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        var changes = root.TryGetProperty("changes", out var changesElement) && changesElement.GetBoolean();
        _longpollFloor = root.TryGetProperty("backoff", out var backoffElement) &&
            backoffElement.TryGetDouble(out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds) : null;
        return changes;
    }

    /// <summary>Reads one list page into the caller-opened spool and seals it as an attempt page (R26: the page
    /// decoder opens its own delete-on-close spool and hands it here; this module never calls Local).</summary>
    public async Task<DropboxPage> ReadPageAsync(string route, string argumentJson, PairConfig pair, Stream spool,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(spool);
        using var response = await SendRouteAsync(route, argumentJson, null, ct).ConfigureAwait(false);
        await using var content = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // shortcut: whole-page buffered decode, upgrade to a streaming spool when the page decoder lands in DropboxReplyShapes.
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        var cursor = root.TryGetProperty("cursor", out var cursorElement) ? cursorElement.GetString() ?? string.Empty
            : string.Empty;
        var hasMore = root.TryGetProperty("has_more", out var hasMoreElement) && hasMoreElement.GetBoolean();
        try
        {
            var decoder = new DropboxEntries();
            var writer = new BinaryWriter(spool, Encoding.UTF8, leaveOpen: true);
            try
            {
                if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    foreach (var element in entries.EnumerateArray())
                        DropboxPage.WriteEntry(writer, decoder.Decode(element, pair));
                writer.Flush();
            }
            finally
            {
                writer.Dispose();
            }
            return await DropboxPage.SealAsync(spool, cursor, hasMore, ct).ConfigureAwait(false);
        }
        catch
        {
            await spool.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Streams one download reply; EOF is not staging success and the caller owns the returned stream.</summary>
    public async Task<Stream> DownloadAsync(string route, string argumentJson, CancellationToken ct)
    {
        var response = await SendRouteAsync(route, argumentJson, null, ct).ConfigureAwait(false);
        try
        {
            var content = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new ResponseStream().Open(response, content, ct, null);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Sends one upload route with the supplied content and returns its parsed reply.</summary>
    public async Task<JsonDocument> UploadAsync(string route, string argumentJson, Stream body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var response = await SendRouteAsync(route, argumentJson, body, ct).ConfigureAwait(false);
        await using var stream = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Waits through the shared transport limiter, created from the settings on first use.</summary>
    public Task WaitAsync(TimeSpan delay, CancellationToken ct) =>
        (_limiter ??= new RateLimit(_settings.InitialLimiterLevel, _settings.Backoff, _events))
        .WaitAsync(delay, _clock, ct);

    private async Task<HttpResponseMessage> SendRouteAsync(string route, string argumentJson, Stream? body,
        CancellationToken ct)
    {
        var request = BuildRequest(route, argumentJson, body);
        try
        {
            string? token = null;
            if (!Routes.IsNoAuth(route) && _accessToken is { } source)
                token = await source(ct).ConfigureAwait(false);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (!string.Equals(route, GetCurrentAccount, StringComparison.Ordinal) &&
                    _pathRootNamespaceId is { } pathRoot)
                    request.Headers.Add("Dropbox-API-Path-Root", PathRootJson(pathRoot));
            }
            var response = await SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    var detail = await ReadErrorAsync(response, ct).ConfigureAwait(false);
                    throw new HttpRequestException(detail, null, response.StatusCode);
                }
            }
            return response;
        }
        finally
        {
            request.Dispose();
        }
    }

    private static HttpRequestMessage BuildRequest(string route, string argumentJson, Stream? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(route))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (Routes.StyleOf(route) == Routes.Style.Rpc)
        {
            request.Content = new StringContent(argumentJson, Encoding.UTF8, "application/json");
        }
        else
        {
            request.Headers.Add("Dropbox-API-Arg", Routes.ArgHeader(argumentJson));
            if (Routes.StyleOf(route) == Routes.Style.Upload)
            {
                ArgumentNullException.ThrowIfNull(body);
                var upload = new StreamContent(body);
                upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content = upload;
            }
        }
        return request;
    }

    private void RecordClock(HttpResponseMessage response, long startedTimestamp)
    {
        if (response.Headers.Date is not { } serverDate) return;
        var elapsed = _clock.GetElapsedTime(startedTimestamp);
        _clockOffset = (serverDate - _clock.GetUtcNow(), elapsed / 2);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[ErrorBodyMaxBytes];
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            return read > 0 ? Encoding.UTF8.GetString(buffer, 0, read) : Text.Get(TextKey.DropboxRefused);
        }
        catch (Exception error) when (error is IOException or HttpRequestException)
        {
            return Text.Get(TextKey.DropboxRefused);
        }
    }

    private static string PathRootJson(string namespaceId) => JsonSerializer.Serialize(
        new Dictionary<string, string>(StringComparer.Ordinal) { [".tag"] = "root", ["root"] = namespaceId });
}
