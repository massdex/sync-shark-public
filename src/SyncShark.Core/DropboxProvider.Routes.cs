using System.Net;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// The provider-generic Dropbox route caller (design dropbox-cli.md §2.4). It reuses the client's token
// resolution, path-root rules, request pacing, download throttle, retry delay and bounded/effect JSON
// reading, and is the only new HTTP code the Dropbox CLI adds. A request whose outcome is uncertain is
// never replayed.
public sealed partial class DropboxPilotClient
{
    private const string PathRootHeader = "Dropbox-API-Path-Root";
    private const string ApiArgHeader = "Dropbox-API-Arg";
    private const string ApiResultHeader = "Dropbox-API-Result";
    private const int DownloadBufferBytes = 128 * 1024;

    /// <summary>Calls one Dropbox route with the client's auth, pacing, retry and path-root rules.</summary>
    public async Task<DropboxRouteResult> CallRouteAsync(DropboxRouteCall call, DropboxRetryLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(limits);
        if (call.Route.Policy == DropboxRoutePolicy.Denied)
            throw new InvalidOperationException($"The Dropbox route {call.Route.Name} is refused by policy.");
        ValidateCall(call);
        if (call.Route.Policy == DropboxRoutePolicy.Read)
            return await ReadWithRetryAsync(async ct =>
            {
                if (call.ContentOut is not null) { call.ContentOut.Position = 0; call.ContentOut.SetLength(0); }
                return await BuildRequestAsync(call, ct);
            }, async (response, ct) =>
            {
                if (response.IsSuccessStatusCode) return await ReadSuccessAsync(call, response, 0, ct);
                var (summary, errorJson) = await DescribeFailureAsync(response, ct);
                throw new DropboxRouteException(response.StatusCode, summary, errorJson);
            }, call.Route.Name, cancellationToken, limits.ReadTransientBudget, limits.MaxBackoff, retryPolicy: limits);
        var startPosition = call.ContentIn?.Position ?? 0;
        for (var attempt = 0; ; attempt++)
        {
            if (call.ContentIn is not null) call.ContentIn.Position = startPosition;
            if (call.ContentOut is not null) { call.ContentOut.Position = 0; call.ContentOut.SetLength(0); }
            using var request = await BuildRequestAsync(call, cancellationToken);
            HttpResponseMessage? response = null;
            try
            {
                DropboxRouteResult? success = null;
                var interrupted = false;
                try
                {
                    if (attempt > 0) V2.RunInstrumentation.Current?.RecordRetry();
                    response = await SendOnceAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                        success = await ReadSuccessAsync(call, response,
                            call.ContentIn is { CanSeek: true } content ? content.Length - startPosition : 0L,
                            cancellationToken);
                }
                catch (Exception error) when (IsInterruption(call, error, cancellationToken)) { interrupted = true; }
                if (success is not null) return success;
                var status = interrupted ? (HttpStatusCode?)null : response!.StatusCode;
                var verdict = DropboxRetryDecision.Decide(call.Route.Policy, status, interrupted, attempt, limits);
                if (verdict == DropboxRetryDecision.Verdict.Retry)
                {
                    var delay = status == HttpStatusCode.TooManyRequests
                        ? RetryDelay(response!, attempt) : TimeSpan.FromSeconds(1 << Math.Min(attempt, 5));
                    if (status == HttpStatusCode.TooManyRequests && delay > limits.MaxBackoff)
                        throw new DropboxRouteException(status.Value,
                            "Dropbox requires more backoff than the configured maximum.", null);
                    response?.Dispose();
                    response = null;
                    await SyncByteProgressStream.ReportCurrentProcessAsync("Dropbox route retry",
                        $"Retrying {call.Route.Name} in {delay.TotalSeconds:N0}s (attempt {attempt + 1}, " +
                        (status is { } code ? $"HTTP {(int)code}" : "transport") + ")", ProcessMessageKind.Notice);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
                await ThrowFailureAsync(call, response, verdict, interrupted, attempt, cancellationToken);
            }
            finally
            {
                // Every attempt releases its response on every exit: success, retry, failure or exception.
                response?.Dispose();
            }
        }
    }

    private static void ValidateCall(DropboxRouteCall call)
    {
        if (call.ContentIn is not null && !call.ContentIn.CanSeek)
            throw new ArgumentException("The Dropbox upload content stream must be seekable.", nameof(call));
        if (call.ContentOut is not null && !call.ContentOut.CanSeek)
            throw new ArgumentException("The Dropbox download destination stream must be seekable.", nameof(call));
        if (call.Route.Style == DropboxRouteStyle.Upload && call.ContentIn is null)
            throw new InvalidOperationException("A Dropbox upload route requires a content stream.");
        if (call.Route.Style == DropboxRouteStyle.Download && call.ContentOut is null)
            throw new InvalidOperationException("A Dropbox download route requires a destination stream.");
    }

    // A send failure or an HttpClient timeout is retried within the read budget and is uncertain for a
    // write; an interrupted read-only body is retried too. A write body failure was already turned into
    // the uncertain outcome by ReadEffectJsonAsync, and the caller's own cancellation is never caught.
    private static bool IsInterruption(DropboxRouteCall call, Exception error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (error is HttpRequestException { StatusCode: null } or System.Net.Sockets.SocketException or OperationCanceledException) return true;
        return call.Route.Policy == DropboxRoutePolicy.Read && error is IOException and not DropboxEffectUncertainException;
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(DropboxRouteCall call, CancellationToken cancellationToken)
    {
        HttpRequestMessage request;
        if (call.Route.IsNoAuth)
        {
            request = new HttpRequestMessage(HttpMethod.Post, call.Route.Endpoint);
        }
        else
        {
            // Official SDKs apply path root to every route; the root namespace is the default and a
            // requested namespace binding replaces it, so no earlier header is kept.
            request = await AuthorizedAsync(HttpMethod.Post, call.Route.Endpoint.AbsoluteUri, cancellationToken);
            request.Headers.Remove(PathRootHeader);
            var body = call.PathRootNamespaceId is null
                ? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                    { [".tag"] = "root", ["root"] = expectedConnection.RootNamespaceId }
                : new Dictionary<string, string>
                    { [".tag"] = "namespace_id", ["namespace_id"] = call.PathRootNamespaceId };
            if (!request.Headers.TryAddWithoutValidation(PathRootHeader, JsonSerializer.Serialize(body)))
                throw new InvalidOperationException("The Dropbox path-root binding could not be applied.");
        }
        switch (call.Route.Style)
        {
            case DropboxRouteStyle.Rpc:
                request.Content = new StringContent(call.ArgumentJson, Encoding.UTF8, "application/json");
                break;
            case DropboxRouteStyle.Upload:
                request.Headers.TryAddWithoutValidation(ApiArgHeader, DropboxArgHeader.Encode(call.ArgumentJson));
                request.Content = new UploadPayloadContent(new ThrottledLeaveOpenReadStream(call.ContentIn!, _transferLimiter));
                request.Content.Headers.ContentType = new("application/octet-stream");
                break;
            case DropboxRouteStyle.Download:
                request.Headers.TryAddWithoutValidation(ApiArgHeader, DropboxArgHeader.Encode(call.ArgumentJson));
                break;
        }
        return request;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request,
        CancellationToken cancellationToken, SyncProviderOperation operation = SyncProviderOperation.Write)
    {
        Interlocked.Increment(ref _providerCallCount);
        return await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken,
            operation: operation);
    }

    private async Task<DropboxRouteResult> ReadSuccessAsync(DropboxRouteCall call, HttpResponseMessage response,
        long uploadBytes, CancellationToken cancellationToken)
    {
        if (call.Route.Style != DropboxRouteStyle.Download)
            return new DropboxRouteResult(call.Route.Policy == DropboxRoutePolicy.Write
                ? await ReadEffectJsonAsync(response,
                    $"The Dropbox route {call.Route.Name} may have taken effect but its success body was unavailable.",
                    cancellationToken)
                : await ReadBoundedJsonAsync(response, cancellationToken), uploadBytes);
        var destination = call.ContentOut!;
        await using (var content = V2.RunOperationStream.Track(await response.Content.ReadAsStreamAsync(cancellationToken), response))
        {
            var buffer = new byte[DownloadBufferBytes];
            using var idle = new SyncResponseBody.IdleBound(cancellationToken, ReadStallTimeout);
            while (true)
            {
                var read = await idle.ReadAsync(content, buffer);
                if (read == 0) break;
                await _transferLimiter.ThrottleAsync(SyncTransferDirection.Download, read, cancellationToken);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        var json = response.Headers.TryGetValues(ApiResultHeader, out var values) ? values.FirstOrDefault() : null;
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException($"The Dropbox route {call.Route.Name} returned no result header.");
        return new DropboxRouteResult(JsonDocument.Parse(json), destination.Length);
    }

    // Ends an attempt that must not be replayed: an uncertain write (missing response, connection drop,
    // timeout or 5xx), an interrupted read after its budget, or a Dropbox refusal.
    private async Task ThrowFailureAsync(DropboxRouteCall call, HttpResponseMessage? response,
        DropboxRetryDecision.Verdict verdict, bool interrupted, int attempt, CancellationToken cancellationToken)
    {
        if (verdict == DropboxRetryDecision.Verdict.Uncertain)
        {
            response?.Dispose();
            throw new DropboxEffectUncertainException(
                $"The Dropbox route {call.Route.Name} may have taken effect but its outcome is unknown.");
        }
        if (interrupted || response is null)
            throw new HttpRequestException(
                $"The Dropbox route {call.Route.Name} was interrupted after {attempt} attempts; nothing was confirmed.");
        using (response)
        {
            var (summary, errorJson) = await DescribeFailureAsync(response, cancellationToken);
            throw new DropboxRouteException(response.StatusCode, summary, errorJson);
        }
    }

    // The caller's cancellation is never swallowed here; only a real body-read problem falls back to the
    // reason phrase.
    private async Task<(string Summary, string? ErrorJson)> DescribeFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            await using var content = V2.RunOperationStream.Track(await response.Content.ReadAsStreamAsync(cancellationToken), response);
            body = await SyncResponseBody.ReadTextAsync(content, DropboxApiLimits.ErrorBodyMaxBytes, ReadStallTimeout,
                "Dropbox returned an oversized error response.", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException or
                                           TimeoutException)
        {
            return (response.ReasonPhrase ?? "Dropbox refused the request.", null);
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (body, null);
            var summary = document.RootElement.TryGetProperty("error_summary", out var value) &&
                          value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : body;
            var errorJson = document.RootElement.TryGetProperty("error", out var error) ? error.GetRawText() : null;
            return (summary, errorJson);
        }
        catch (JsonException)
        {
            // HTTP 400 answers with plain text, not JSON.
            return (body, null);
        }
    }

}
