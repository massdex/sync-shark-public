using System.Globalization;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private RunReport DeferOversizedMovingUnit(PairDef pair, SqliteStateStore sql, ReconcileBatch batch,
        IProgress<RunProgress> progress)
    {
        var identity = batch.MovingFolder!;
        var folder = identity.Item.Path;
        var reason = string.Format(CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("reconciliation-moving-too-large"), folder, batch.MovingItems);
        // The folder identity owns the notice even if its path changes again; durable planning debt remains.
        var id = ShortId.Make("moving-limit", pair.PairId, identity.Side.ToString(), identity.Item.ObjectId);
        _store.AddNotices([new Notice(pair.PairId, 0, NoticeKind.Info, folder, reason,
            "reconciliation-moving-too-large", id, Repair: identity)]);
        sql.DeferMovingSubtrees(batch.Session, batch.MovingFolders);
        progress.Report(new RunProgress(RunPhases.Repairing, folder, 0, batch.MovingItems) { Message = reason });
        RunTrace.Emit(reason);
        return new RunReport(pair.PairId, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, []) { Deferred = 1 };
    }

    private void ClearConvergedMovingNotices(PairDef pair, SqliteStateStore sql)
    {
        if (sql.CountDeferredPlanning(pair.PairId) != 0) return;
        foreach (var notice in _store.UnresolvedNotices(pair.PairId).Where(notice =>
            notice.ReasonKey == "reconciliation-moving-too-large" && notice.Repair is not null))
        {
            var identity = notice.Repair!;
            var basis = sql.BaselinesTouching(pair.PairId, identity.Side, [identity.Item.ObjectId], []).FirstOrDefault();
            var deleted = basis is null && sql.ByObject((identity.Side == Side.A ? pair.A : pair.B).RootId,
                identity.Item.ObjectId) is null && sql.Live(pair.A.RootId, [notice.Path]).Count == 0 &&
                sql.Live(pair.B.RootId, [notice.Path]).Count == 0;
            deleted |= basis is not null && sql.ByObject(pair.A.RootId, basis.A.ObjectId) is null &&
                sql.ByObject(pair.B.RootId, basis.B.ObjectId) is null;
            if (deleted || basis is not null && sql.ByObject(pair.A.RootId, basis.A.ObjectId) is { } a &&
                sql.ByObject(pair.B.RootId, basis.B.ObjectId) is { } b && PairPath.Same(a.Path, b.Path) &&
                PairPath.Same(a.Path, basis.A.Path) && PairPath.Same(b.Path, basis.B.Path))
                _store.CloseNotice(notice.NoticeId!, "converged");
        }
    }
}
