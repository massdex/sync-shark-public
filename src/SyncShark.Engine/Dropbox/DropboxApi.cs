namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The HTTP and response-consumption boundary;</summary>
public sealed class DropboxApi
{
    /// <summary>Create the shared Dropbox transport using the supplied clock for activity timeouts.</summary>
    public static HttpClient CreateHttpClient(TimeProvider clock) => throw new NotImplementedException();

    /// <summary>Send a prepared request through the shared transport. Return HTTP status and body unchanged, including provider refusals; the caller owns the response.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Uses the transport and clock supplied by the composition root for requests and response timing. Construct the transport limiter from settings.InitialLimiterLevel, settings.Backoff and the run’s event sink.</summary>
    public DropboxApi(HttpClient transport, TimeProvider clock, EngineSettings settings, Action<RunEvent> events, Func<CancellationToken, ValueTask<string?>>? accessToken = null, string? pathRootNamespaceId = null) => throw new NotImplementedException();

    /// <summary>The transport clock used for retry dates and receipt timestamps.</summary>
    internal TimeProvider Clock { get => throw new NotImplementedException(); }

    /// <summary>Latest successful reply offset and its measured uncertainty; no extra request.</summary>
    public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset => throw new NotImplementedException();

    /// <summary>Read Dropbox storage usage using the account route from the route catalog.</summary>
    public Task<(long Used,long Allocated)> SpaceUsageAsync(CancellationToken ct) => throw new NotImplementedException();

    /// <summary>CallJsonAsync for the selected DropboxApi operation.</summary>
    public Task<System.Text.Json.JsonDocument> CallJsonAsync(string route, string argumentJson, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Decode one authenticated listing page from the response stream; the decoder owns its spool.</summary>
    public Task<DropboxPage> ReadPageAsync(string route, string argumentJson, PairConfig pair, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Return the change flag and the provider backoff for this feed only.</summary>
    public Task<(bool Changes, TimeSpan? Backoff)> LongpollAsync(string cursor, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>DownloadAsync for the selected DropboxApi operation.</summary>
    public Task<Stream> DownloadAsync(string route, string argumentJson, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Bind an account and namespace while sharing the host's transport limiter.</summary>
    public DropboxApi CreateAccountScope(Action<RunEvent> events, Func<CancellationToken, ValueTask<string?>>? accessToken, string? pathRootNamespaceId) => throw new NotImplementedException();
    /// <summary>Classify a refusal; only a supplied read path and proven absence permit a normal return.</summary>
    internal Task ThrowFailureAsync(HttpResponseMessage response, string route, CancellationToken ct, string? readPath = null) => throw new NotImplementedException();
    /// <summary>Read one bounded refusal envelope for classification.</summary>
    internal static Task<System.Text.Json.JsonElement> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read the provider retry delay without consuming the response body.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response, TimeProvider clock) => throw new NotImplementedException();
    /// <summary>Get the monotonic timestamp for receipt of the response headers.</summary>
    internal static long ReceivedAt(HttpResponseMessage response, TimeProvider clock) => throw new NotImplementedException();
    /// <summary>The existing terminal feedback handler for completed transfers.</summary>
    internal Action<bool> TransferFinished => throw new NotImplementedException();
    /// <summary>Accept busy feedback once per classified HTTP refusal, not once per batch member.</summary>
    internal void ReportBusy(System.Net.HttpStatusCode status, DropboxErrors.Classified failure, TimeSpan? wait, long received) => throw new NotImplementedException();
    /// <summary>WaitAsync for the selected DropboxApi operation.</summary>
    public Task WaitAsync(TimeSpan delay, CancellationToken ct) => throw new NotImplementedException();
}
