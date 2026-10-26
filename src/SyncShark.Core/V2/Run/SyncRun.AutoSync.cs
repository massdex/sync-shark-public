namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    Task<RunReport> ISyncRun.AutoSyncAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct) =>
        IncrementalAsync(pairId, progress, ct, new RunOptions { AutoSyncFreshDelta = true });
}
