namespace Massdex.SyncShark.V2;

/// <summary>Coordinates one pair's runs from inventory to commit.</summary>
public interface ISyncRun
{
    Task<IReadOnlyList<RecoveryRestoreItem>> RestoreRecoveryPageAsync(string pairId, RecoveryQuery query,
        bool asCopy, bool dryRun, CancellationToken ct) =>
        throw new NotSupportedException("This sync runner does not support Recovery restore.");
    Task<Plan> PreviewFirstSyncAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct);
    Task<RunReport> ApplyAsync(string pairId, Plan approved, IProgress<RunProgress> p, CancellationToken ct);
    Task<RunReport> IncrementalAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct);
    internal Task<RunReport> AutoSyncAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct) =>
        IncrementalAsync(pairId, progress, ct);
    internal Task<bool> MonitorAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct) =>
        Task.FromResult(false);
    Task StopAsync();
}
