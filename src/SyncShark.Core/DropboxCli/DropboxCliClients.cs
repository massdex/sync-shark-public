namespace Massdex.SyncShark;

/// <summary>The one native Dropbox CLI client policy every CLI runtime uses: the caller's transfer handler, the
/// native request timeout, and the authority-bound account client composition (design DESIGN-115-CLI-OUTPUT
/// §2.2). The runtimes differ only in the authority they pass, never in this composition.</summary>
internal static class DropboxCliClients
{
    /// <summary>Creates the native client. The caller records ownership before anything after it can throw.</summary>
    internal static HttpClient Create(Func<HttpMessageHandler> handlerFactory) =>
        new(handlerFactory()) { Timeout = DropboxCliSettings.Default.HttpTimeout };

    /// <summary>Composes the account clients over the supplied authority, the already-owned HTTP client and
    /// credentials, and the runtime's own limiter.</summary>
    internal static DropboxAccountClients Compose(PilotStateAuthority authority, HttpClient http,
        IDropboxRefreshTokenStore credentials, SyncTransferRateLimiter limiter) =>
        new(http, new DropboxConnectionStore(authority), credentials,
            binding => new RefreshingDropboxAccessTokenSource(http, credentials, binding), limiter);
}
