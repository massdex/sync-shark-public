using System.Globalization;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private RunReport DeferOversizedMovingUnit(PairDef pair, SqliteStateStore sql, ReconcileBatch batch,
        IProgress<RunProgress> progress)
    {
        var folder = batch.MovingFolders[0];
        var reason = string.Format(CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("reconciliation-moving-too-large"), folder, batch.MovingItems);
        // One stable path owns this notice across repair-tail retries. Save durable debt before
        // moving the selection; no member is observed, planned or released from this unit.
        SaveFailedRetry(pair, folder, reason, _store.SyncRetryAt(pair.PairId, folder));
        sql.DeferMovingSubtrees(batch.Session, batch.MovingFolders);
        progress.Report(new RunProgress(RunPhases.Repairing, folder, 0, batch.MovingItems) { Message = reason });
        RunTrace.Emit(reason);
        return new RunReport(pair.PairId, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, []) { Deferred = 1 };
    }
}
