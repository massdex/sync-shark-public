using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class DropboxPilotClient
{
    /// <summary>
    /// Test seam: replaces the closed-session-start retry wait. The delay the schedule actually asked for is
    /// passed in, so a check can observe the backoff without sleeping through it. Null uses the real timer.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task>? SessionStartDelay { get; set; }

    // Retries for a closed-session start the endpoint answers with HTTP 500/502/503/504. A burst of
    // simultaneous starts is the observed cause, so the ceiling is higher than the transport-loss count.
    private const int ClosedSessionStartServerRetryCount = 4;

    // A closed session only stages bytes: even a lost acknowledgement cannot create a visible file.
    // Replay is safe here ONLY; commits and the general visible-effect sender retain their own boundaries.
    private async Task<string> SendClosedSessionStartAsync(Stream source, int count, V2.V2Settings settings,
        CancellationToken cancellationToken)
    {
        if (settings.DropboxSessionStartRetryCount is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(settings.DropboxSessionStartRetryCount));
        if (settings.DropboxSessionStartRetryBaseDelayMs is < 0 or > 30_000)
            throw new ArgumentOutOfRangeException(nameof(settings.DropboxSessionStartRetryBaseDelayMs));
        if (settings.UploadSessionStartConcurrency is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(settings.UploadSessionStartConcurrency));
        // A large worker pool would otherwise issue one start per file at once, and the start endpoint answers
        // that burst with HTTP 500. The gate is shared per account/namespace across bound clients, exactly like
        // the commit gate. Waiting workers hold no payload bytes. Each attempt streams from the retained
        // source handle, with byte admission before allocating its bounded chunk.
        var startGate = DropboxUploadSessionStartGate.For(ExpectedConnection.ConnectionId,
            expectedConnection.RootNamespaceId, settings.UploadSessionStartConcurrency);
        await V2.RunOperations.WaitAsync(startGate, "upload session-start gate", cancellationToken, V2.RunOperationKind.TransferSlot).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = await AuthorizedAsync(HttpMethod.Post,
                    "https://content.dropboxapi.com/2/files/upload_session/start", cancellationToken).ConfigureAwait(false);
                request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(
                    new { close = true, session_type = new Dictionary<string, string> { [".tag"] = "sequential" } }, JsonOptions));
                source.Position = 0;
                request.Content = new UploadPayloadContent(new ThrottledLeaveOpenReadStream(source, _transferLimiter), count);
                request.Content.Headers.ContentType = new("application/octet-stream");
                var delay = TimeSpan.FromMilliseconds((long)settings.DropboxSessionStartRetryBaseDelayMs * (1 << attempt));
                try
                {
                    using var timing = V2.RunInstrumentation.Current?.Measure(V2.RunTimingStage.UploadSessionStart);
                    Interlocked.Increment(ref _providerCallCount);
                    using var response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken, () =>
                        {
                            if (attempt > 0) V2.RunInstrumentation.Current?.RecordRetry();
                        }).ConfigureAwait(false);
                    // The pacing seam records throttles and shares the account cooldown, including Retry-After dates.
                    if (SyncProviderRequestPacing.RetryAfter(response) is { } retryAfter && retryAfter > delay)
                        delay = retryAfter;
                    if (!response.IsSuccessStatusCode)
                        throw new DropboxApiException(response.StatusCode,
                            $"Dropbox closed upload-session start returned HTTP {(int)response.StatusCode}; no file was committed.");
                    using var document = await ReadBoundedJsonAsync(response, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var sessionId = document.RootElement.GetProperty("session_id").GetString();
                        if (string.IsNullOrWhiteSpace(sessionId))
                            throw new InvalidDataException("Dropbox omitted the upload session identity.");
                        return sessionId;
                    }
                    catch (Exception error) when (IsEffectBodyShapeException(error))
                    {
                        throw new InvalidDataException("Dropbox returned an invalid closed upload-session identity. " + error.Message, error);
                    }
                }
                catch (HttpRequestException error) when (!IsRetryableClosedSessionStartFailure(error))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsAccessRefusal(error)) throw;
                    // A staging 409 is not a target-revision mismatch; do not let write-error mapping turn it into Skipped.
                    throw new IOException("Dropbox closed upload-session start was rejected; no file was committed. " + error.Message, error);
                }
                catch (Exception error) when (IsRetryableClosedSessionStartFailure(error))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A transient server error at the start endpoint earns its own longer, jittered schedule and a
                    // higher retry ceiling than a transport loss or a 429; the two are counted and paced separately.
                    var serverError = IsClosedSessionStartServerError(error);
                    if (serverError)
                    {
                        V2.RunInstrumentation.Current?.RecordServerError();
                        var serverBackoff = ClosedSessionStartServerBackoff(attempt);
                        if (serverBackoff > delay) delay = serverBackoff;
                    }
                    var retryLimit = serverError ? ClosedSessionStartServerRetryCount : settings.DropboxSessionStartRetryCount;
                    if (attempt >= retryLimit)
                        throw new IOException($"Dropbox closed upload-session start failed after {attempt + 1} attempt(s); " +
                            "no file was committed. " + error.Message, error);
                    if (delay > TimeSpan.FromMinutes(5))
                        throw new IOException($"Dropbox closed upload-session start requires {delay.TotalSeconds:N0} seconds of backoff; " +
                            "this exceeds the five-minute automatic retry window. No earlier retry was sent and no file was committed. " +
                            error.Message, error);
                }
                // Cancellation remains cancellation, including while backing off. Retry-After is never shortened.
                if (SessionStartDelay is { } delayAsync) await delayAsync(delay, cancellationToken).ConfigureAwait(false);
                else await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { startGate.Release(); }
    }

    // The staged server-error schedule: about 1 s, 3 s, 8 s, 20 s, with bounded jitter so a fleet of workers
    // does not retry in lockstep. The five-minute guard upstream still caps any Retry-After-driven wait.
    private static TimeSpan ClosedSessionStartServerBackoff(int attempt)
    {
        var milliseconds = attempt switch { 0 => 1_000d, 1 => 3_000d, 2 => 8_000d, _ => 20_000d };
        return TimeSpan.FromMilliseconds(milliseconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    private static bool IsClosedSessionStartServerError(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => (int)status is 500 or 502 or 503 or 504,
        _ => false
    };

    private static bool IsRetryableClosedSessionStartFailure(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests || (int)status >= 500,
        HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException => true,
        _ => false
    };
}
