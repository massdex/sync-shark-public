using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Massdex.SyncShark;

/// <summary>Positive transport evidence only; never infer retryability from local I/O or error text.</summary>
internal static class ProviderTransientFailure
{
    internal const int VerificationRetrySeconds = 30;
    internal static bool IsTransient(Exception error, CancellationToken callerToken = default) =>
        !callerToken.IsCancellationRequested && (error switch
    {
        SyncCliException or V2.EndpointAccessException => false,
        DropboxApiException api => api.StatusCode is { } status && RetryStatus(status),
        HttpRequestException http => http.HttpRequestError != HttpRequestError.SecureConnectionError &&
            !HasAuthenticationCause(http) && (http.StatusCode is null || RetryStatus(http.StatusCode.Value)),
        SocketException => true,
        ProviderTransportTimeoutException => true,
        OperationCanceledException { InnerException: TimeoutException } => true,
        DropboxEffectUncertainException { InnerException: { } inner } => IsTransient(inner),
        V2.UncertainEffectException { InnerException: { } inner } => IsTransient(inner),
        _ => false
    });

    private static bool HasAuthenticationCause(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is AuthenticationException) return true;
        return false;
    }

    private static bool RetryStatus(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests ||
        status == HttpStatusCode.RequestTimeout || (int)status is >= 500 and <= 599;
}
