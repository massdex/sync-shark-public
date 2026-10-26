using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static class CliRecoveryMappingChecks
{
    internal static async Task RunAsync()
    {
        ProviderTransientContract();
        foreach (var exception in new Exception[] { new HttpRequestException("offline"),
            new SocketException((int)SocketError.HostNotFound), new ProviderTransportTimeoutException("idle stall"),
            new OperationCanceledException("HTTP timeout", new TimeoutException("request deadline")),
            new DropboxApiException(HttpStatusCode.ServiceUnavailable, "outage"),
            new DropboxApiException(HttpStatusCode.TooManyRequests, "rate limited"),
            new HttpRequestException("gateway", null, HttpStatusCode.BadGateway) })
            await CheckAsync(exception, 7, "transient_unavailable", true);
        foreach (var exception in new Exception[] { new IOException("disk full"),
            new TimeoutException("local spool admission"), new TimeoutException("interactive OAuth deadline"),
            new HttpRequestException("certificate", new System.Security.Authentication.AuthenticationException("untrusted")),
            new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS failed"),
            new InvalidDataException("damaged state"), new OperationCanceledException("internal cancellation"),
            new DropboxApiException(HttpStatusCode.Conflict, "conflict"),
            new DropboxApiException(HttpStatusCode.Unauthorized, "sign in") })
            await CheckAsync(exception, 6, "operation_failed", false);
        await CheckAsync(new SyncCliReviewException("approval required"), 5, "review_required", false);
        await CheckAsync(new SyncRunPendingVerificationException("pending effect", 117), 8,
            "pending_verification", true, 117);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        if (ProviderTransientFailure.IsTransient(new OperationCanceledException("canceled",
            new TimeoutException("request deadline")), canceled.Token))
            throw new InvalidOperationException("Caller cancellation must never become retryable transport timeout.");
        Console.WriteLine("CLI recovery mapping checks passed.");
    }

    private static void ProviderTransientContract()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        foreach (var code in new[] { 200, 400, 401, 403, 404, 408, 409, 429, 499, 500, 503, 599, 600 })
        {
            var expected = code is 408 or 429 || code is >= 500 and <= 599;
            Check(new DropboxApiException((HttpStatusCode)code, "status"), expected);
            Check(new HttpRequestException("status", null, (HttpStatusCode)code), expected);
        }
        Check(new HttpRequestException("no status"), true);
        Check(new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS"), false);
        Check(new HttpRequestException("auth", new IOException("nested",
            new System.Security.Authentication.AuthenticationException("auth"))), false);
        Check(new ProviderTransportTimeoutException("transport"), true);
        Check(new TimeoutException("ordinary timeout"), false);
        Check(new OperationCanceledException("caller"), false);
        Check(new OperationCanceledException("deadline", new TimeoutException()), true);
        Check(new EndpointAccessException("access"), false);
        Check(new SyncCliReviewException("review"), false);
        Check(new DropboxEffectUncertainException("no cause"), false);
        Check(new UncertainEffectException("no cause"), false);
        if (ProviderTransientFailure.VerificationRetrySeconds != 30 ||
            typeof(DropboxEffectUncertainException).FullName != "Massdex.SyncShark.DropboxEffectUncertainException" ||
            typeof(ProviderTransportTimeoutException).FullName != "Massdex.SyncShark." + nameof(ProviderTransportTimeoutException) ||
            typeof(ProviderTransientFailure).FullName != "Massdex.SyncShark.ProviderTransientFailure" ||
            typeof(UncertainEffectException).GetProperty("JobIdUnavailable") is not null)
            throw new InvalidOperationException("Provider identities, retry interval or mapped flag behavior changed.");

        void Check(Exception cause, bool expected)
        {
            foreach (var error in new Exception[] { cause,
                new DropboxEffectUncertainException("raw", cause) { JobIdUnavailable = true },
                new UncertainEffectException("mapped", cause),
                new UncertainEffectException("nested", new DropboxEffectUncertainException("raw", cause)) })
            {
                if (ProviderTransientFailure.IsTransient(error) != expected ||
                    ProviderTransientFailure.IsTransient(error, canceled.Token))
                    throw new InvalidOperationException("Provider transient classification changed: " + cause.GetType().Name);
            }
        }
    }

    private static async Task CheckAsync(Exception exception, int expectedExit, string code, bool retryable,
        int? retryAfter = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await SyncCli.ExecuteAsync(["--cli", "status", "--json"], output, error, () => throw exception);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        var failure = root.GetProperty("error");
        if (exit != expectedExit || root.GetProperty("ok").GetBoolean() || failure.GetProperty("code").GetString() != code ||
            (failure.TryGetProperty("retryable", out var retry) && retry.GetBoolean()) != retryable ||
            retryAfter is { } seconds && failure.GetProperty("retryAfterSeconds").GetInt32() != seconds)
            throw new InvalidOperationException("Incorrect CLI recovery contract: " + output);
    }
}
