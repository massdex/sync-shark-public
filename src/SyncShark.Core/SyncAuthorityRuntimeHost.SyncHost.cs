namespace Massdex.SyncShark;

/// <summary>Core v1's host satisfies the window's <see cref="ISyncHost"/> surface.</summary>
/// <remarks>
/// The methods the host already declares satisfy the interface implicitly. The eight members the window used to reach
/// through <see cref="SyncAuthorityRuntimeHost.Authority"/> — the run/activity history, retention window, activity
/// sequence, last-successful-sync time, diagnostic append and health check — are lifted onto
/// <see cref="ISyncHistoryHost"/> and implemented here explicitly, forwarding to the authority database.
/// </remarks>
public sealed partial class SyncAuthorityRuntimeHost : ISyncHost
{
    Task<IReadOnlyList<SyncAuthorityRunHistory>> ISyncHistoryHost.LoadRunHistoryAsync(int limit, int offset,
        CancellationToken cancellationToken) => _authority.LoadRunHistoryAsync(limit, offset, cancellationToken);

    Task<IReadOnlyList<SyncAuthorityRunHistory>> ISyncHistoryHost.LoadRunRetriesAsync(string runId,
        CancellationToken cancellationToken) => _authority.LoadRunRetriesAsync(runId, cancellationToken);

    Task<SyncAuthorityRunHistory?> ISyncHistoryHost.LoadRunByIdAsync(string runId, CancellationToken cancellationToken,
        Action<string>? onReadProgress) => _authority.LoadRunByIdAsync(runId, cancellationToken, onReadProgress);

    Task<SyncActivityRetentionWindow> ISyncHistoryHost.LoadActivityRetentionWindowAsync(CancellationToken cancellationToken,
        string? runId) => _authority.LoadActivityRetentionWindowAsync(cancellationToken, runId);

    Task<long> ISyncHistoryHost.LoadActivitySequenceAsync(CancellationToken cancellationToken) =>
        _authority.LoadActivitySequenceAsync(cancellationToken);

    Task<DateTimeOffset?> ISyncHistoryHost.LoadLastSuccessfulSyncUtcAsync(CancellationToken cancellationToken,
        IReadOnlyCollection<string>? allowedPairIds) =>
        _authority.LoadLastSuccessfulSyncUtcAsync(cancellationToken, allowedPairIds);

    Task ISyncHistoryHost.AppendDiagnosticActivityAsync(SyncActivityEvent item, CancellationToken cancellationToken) =>
        _authority.AppendDiagnosticActivityAsync(item, cancellationToken);

    Task ISyncHistoryHost.VerifyHealthAsync(CancellationToken cancellationToken, Action<string>? startupProgress) =>
        _authority.VerifyHealthAsync(cancellationToken, startupProgress);
}
