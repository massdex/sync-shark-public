using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private void ClearWrittenNameRefusal(SqliteTransaction transaction, long pairNo, string pairId,
        long rootNo, SyncAction action, ItemState written)
    {
        _files.ClearProviderNameRefused(transaction, rootNo, written, action.ExpectedTarget);
        // The inventory still describes the pre-write version. Retire the repaired entity's notice
        // from this committed effect, rather than waiting for a later scan to match the new baseline.
        var item = action.ExpectedTarget ?? written;
        if (!string.Equals(item.Path, written.Path, StringComparison.Ordinal)) return;
        var candidate = new Notice(pairId, 31, NoticeKind.Info, item.Path, string.Empty,
            ProviderNameRefusedException.NoticeKey, Repair: new NoticeRepair(action.Target, item));
        var id = NoticeTable.Id(candidate);
        if (_notices.ById(pairNo, id, transaction) is not { Closed: false, Notice.Repair: { } repair } saved ||
            repair.Side != action.Target || repair.Item.ObjectId != item.ObjectId ||
            !string.Equals(saved.Notice.Path, written.Path, StringComparison.Ordinal) ||
            repair.Destination is not null && !PairPath.Same(repair.Destination, written.Path)) return;
        _notices.CloseResolved(transaction, pairNo, id, "resolved");
    }
}
