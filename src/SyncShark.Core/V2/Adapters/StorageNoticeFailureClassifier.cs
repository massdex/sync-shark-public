using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Massdex.SyncShark.V2;

internal static class StorageNoticeFailureClassifier
{
    /// <summary>
    /// Positive-evidence classification shared by every storage lane (hydration, its durable offline lane, settlement
    /// and pointer refresh). A pair-level cause (network, account auth or a disconnected provider) fails every file,
    /// so it is reported as ONE pair notice; anything item-specific (a missing revision/hash, a changed remote
    /// revision, a range/disk fault) stays a per-item notice. Never infers from message text, and a local
    /// cancellation is not a failure to report.
    /// </summary>
    internal static StoragePairException? ClassifyFailure(Exception error)
    {
        foreach (var current in Chain(error))
        {
            switch (current)
            {
                case StoragePairException pair:
                    return pair;
                case EndpointAccessException access:
                    // Created only by DropboxItems.MapAccessError from IsAccessRefusal (401, OAuth invalid_grant or a
                    // missing protected credential): an account-access refusal, not an item permission.
                    return new StoragePairException("auth", AuthDetail, access);
                case DropboxCredentialUnavailableException credential:
                    return new StoragePairException("auth", AuthDetail, credential);
                case DropboxApiException api when api.OAuthError == DropboxOAuthError.InvalidGrant:
                    return new StoragePairException("auth", AuthDetail, api);
                case DropboxApiException api when api.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new StoragePairException("auth", AuthDetail, api);
                case HttpRequestException http when http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new StoragePairException("auth", AuthDetail, http);
                case SyncRunRefusedException refusal:
                    // The connection is missing, signed out or not connected: every file under the pair fails.
                    return new StoragePairException("provider", ProviderDetail, refusal);
                case SocketException socket:
                    return new StoragePairException("network", NetworkDetail, socket);
                case ProviderTransportTimeoutException timeout:
                    return new StoragePairException("network", NetworkDetail, timeout);
                case AuthenticationException tls:
                    // A TLS handshake/secure-connection failure is transport, not an account sign-in refusal.
                    return new StoragePairException("network", NetworkDetail, tls);
                case HttpRequestException httpServer when httpServer.StatusCode is null ||
                    httpServer.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                    (int?)httpServer.StatusCode is >= 500 and <= 599:
                    return new StoragePairException("network", NetworkDetail, httpServer);
                case OperationCanceledException { InnerException: TimeoutException } cancelled:
                    return new StoragePairException("network", NetworkDetail, cancelled);
            }
        }
        return null;
    }

    private const string AuthDetail =
        "Dropbox sign-in was refused during storage work. Reconnect this Dropbox account, then Retry, or Ignore this cause.";
    private const string NetworkDetail =
        "Dropbox could not be reached during storage work. Check the network connection; the affected work stays deferred and retries, then Retry, or Ignore this cause.";
    private const string ProviderDetail =
        "The Dropbox connection is unavailable during storage work. Reconnect the provider, then Retry, or Ignore this cause.";

    private static IEnumerable<Exception> Chain(Exception error)
    {
        // Flatten at any depth: a native wrapper may carry an AggregateException as its InnerException.
        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                foreach (var nested in Chain(inner))
                    yield return nested;
            yield break;
        }
        yield return error;
        if (error.InnerException is { } innerException)
            foreach (var nested in Chain(innerException))
                yield return nested;
    }
}
