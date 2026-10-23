namespace Massdex.SyncShark.V2;

/// <summary>A fresh read established that reconciliation needs time or restored transport, not new approval.</summary>
public sealed class SyncRunPendingVerificationException(string message, int retryAfterSeconds) : Exception(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
