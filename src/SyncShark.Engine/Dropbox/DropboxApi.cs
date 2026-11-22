namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The HTTP and response-consumption boundary;</summary>
public sealed class DropboxApi
{
    /// <summary>Send operation on the supplied facts.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Uses the transport and clock supplied by the composition root for requests and response timing. Construct the transport limiter from settings.InitialLimiterLevel, settings.Backoff and the run’s event sink.</summary>
    public DropboxApi(HttpClient transport, TimeProvider clock, EngineSettings settings, Action<RunEvent> events) => throw new NotImplementedException();

    /// <summary>Latest successful reply offset and its measured uncertainty; no extra request.</summary>
    public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset => throw new NotImplementedException();

}
