using System.Net;

namespace Massdex.SyncShark;

/// <summary>The one pure retry verdict for the generic Dropbox route caller (design dropbox-cli.md §2.4).</summary>
public static class DropboxRetryDecision
{
    /// <summary>The caller's next step after one attempt that did not succeed.</summary>
    public enum Verdict
    {
        /// <summary>Send the same request again after the caller's delay.</summary>
        Retry,

        /// <summary>A write may have been applied; stop and report an uncertain effect.</summary>
        Uncertain,

        /// <summary>Stop and surface the failure.</summary>
        Fail
    }

    /// <summary>
    /// Decides the caller's next step after a transport failure or a non-success status, from the route's
    /// policy and the per-attempt retry limits. Success is handled by the caller before this is consulted.
    /// </summary>
    public static Verdict Decide(DropboxRoutePolicy policy, HttpStatusCode? status, bool transportFailure,
        int attempt, DropboxRetryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (transportFailure)
            return policy == DropboxRoutePolicy.Read
                ? (attempt < limits.ReadTransportRetries ? Verdict.Retry : Verdict.Fail)
                : Verdict.Uncertain;
        var code = (int?)status ?? 0;
        if (code == (int)HttpStatusCode.TooManyRequests)
            return attempt < limits.RateLimitRetries ? Verdict.Retry : Verdict.Fail;
        if (code >= 500 || code == (int)HttpStatusCode.RequestTimeout)
            return policy == DropboxRoutePolicy.Read
                ? (attempt < limits.ReadServerRetries ? Verdict.Retry : Verdict.Fail)
                : Verdict.Uncertain;
        return Verdict.Fail;
    }
}
