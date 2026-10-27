namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    Task<RunReport> ISyncRun.AutoSyncAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct) =>
        IncrementalAsync(pairId, progress, ct, new RunOptions { AutoSyncFreshDelta = true });

    private IReadOnlyList<SqliteStateStore.DeferredPlanning> AutoSyncPlanningPage(PairDef pair, BothScan scan)
    {
        var urgent = AdmitPlanningPage(pair.PairId, pair, userOnly: true);
        if (_store is not SqliteStateStore sql) return urgent;
        // These entities were already observed by this run, not admitted from bulk debt.
        // Keep the captured versions: later independent observations still defeat the CAS.
        return urgent.Concat(sql.DeferredPlanningFor(pair.PairId, pair.A.RootId, scan.A.ChangeObjectIds))
            .Concat(sql.DeferredPlanningFor(pair.PairId, pair.B.RootId, scan.B.ChangeObjectIds))
            .DistinctBy(row => (row.Root, row.ObjectId)).ToArray();
    }
}
