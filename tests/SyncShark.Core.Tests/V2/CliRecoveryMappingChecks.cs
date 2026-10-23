using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static class CliRecoveryMappingChecks
{
    internal static async Task RunAsync()
    {
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
