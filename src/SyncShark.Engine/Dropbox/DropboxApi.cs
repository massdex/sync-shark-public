namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The HTTP and response-consumption boundary;</summary>
public sealed class DropboxApi
{
    /// <summary>Send operation on the supplied facts.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Uses the transport and clock supplied by the composition root for requests and response timing. Construct the transport limiter from settings.InitialLimiterLevel, settings.Backoff and the run’s event sink.</summary>
    public DropboxApi(HttpClient transport, TimeProvider clock, EngineSettings settings, Action<RunEvent> events, Func<CancellationToken, ValueTask<string?>>? accessToken = null, string? pathRootNamespaceId = null) => throw new NotImplementedException();

    /// <summary>Latest successful reply offset and its measured uncertainty; no extra request.</summary>
    public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset => throw new NotImplementedException();

    /// <summary>Read Dropbox storage usage using the account route from the route catalog.</summary>
    public Task<(long Used,long Allocated)> SpaceUsageAsync(CancellationToken ct) => throw new NotImplementedException();

    /// <summary>CallJsonAsync for the selected DropboxApi operation.</summary>
    public Task<System.Text.Json.JsonDocument> CallJsonAsync(string route, string argumentJson, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>LongpollAsync for the selected DropboxApi operation.</summary>
    public Task<bool> LongpollAsync(string cursor, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>DownloadAsync for the selected DropboxApi operation.</summary>
    public Task<Stream> DownloadAsync(string route, string argumentJson, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>UploadAsync for the selected DropboxApi operation.</summary>
    public Task<System.Text.Json.JsonDocument> UploadAsync(string route, string argumentJson, Stream body, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>WaitAsync for the selected DropboxApi operation.</summary>
    public Task WaitAsync(TimeSpan delay, CancellationToken ct) => throw new NotImplementedException();
}
