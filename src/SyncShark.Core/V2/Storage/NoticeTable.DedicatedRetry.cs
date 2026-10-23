using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal sealed partial class NoticeTable
{
    private bool SuppressDedicatedRetry(SqliteTransaction transaction, long pairNo, Notice notice)
    {
        // A blocked conflict successor owns its explanation and Retry-only policy, not the generic
        // path retry (whose Ignore would silently abandon the established conflict).
        if (notice.ReasonKey == ExecutionReasons.ConflictCopyBlocked)
            CloseResolved(transaction, pairNo,
                ShortId.Make("n", notice.PairId, "40", RowCodec.Key(notice.Path), "sync-retry"), "superseded");
        if (notice.ReasonKey is "content-unconfirmed" or "sync-retry" &&
            StoreSchema.Scalar(_connection, transaction,
                "SELECT 1 FROM notice INDEXED BY nt_pair_reason WHERE pair_no=$p AND reason_key=$reason " +
                "AND path_key=$key AND " + NoticeTable.ActionableSql("state") + " LIMIT 1;",
                ("$p", pairNo), ("$key", RowCodec.Key(notice.Path)), ("$reason", ExecutionReasons.ConflictCopyBlocked)) is not null)
            return true;
        // The uncertain-delete explanation owns this failure, regardless of publication order.
        // Aggregated rows still own the per-item explanation behind their summary.
        if (notice.ReasonKey == InterruptedDelete.UnverifiedReason)
            CloseResolved(transaction, pairNo,
                ShortId.Make("n", notice.PairId, "40", RowCodec.Key(notice.Path), "sync-retry"), "superseded");
        if (notice.ReasonKey is "content-unconfirmed" or "sync-retry" &&
            StoreSchema.Scalar(_connection, transaction,
                "SELECT 1 FROM notice INDEXED BY nt_pair_reason WHERE pair_no=$p AND reason_key='interrupted-delete-unverified' " +
                "AND path_key=$key AND " + ActionableSql("state") + " LIMIT 1;",
                ("$p", pairNo), ("$key", RowCodec.Key(notice.Path))) is not null)
            return true;
        // Unanswered deletion evidence owns its explanation, regardless of publication order.
        if (notice.ReasonKey == "deletion-evidence-pending")
            CloseResolved(transaction, pairNo,
                ShortId.Make("n", notice.PairId, "40", RowCodec.Key(notice.Path), "sync-retry"), "evidence-pending");
        if (notice.ReasonKey == "sync-retry" &&
            StoreSchema.Scalar(_connection, transaction,
                "SELECT 1 FROM notice INDEXED BY nt_pair_reason WHERE pair_no=$p AND reason_key='deletion-evidence-pending' " +
                "AND path_key=$key AND " + ActionableSql("state") + " LIMIT 1;",
                ("$p", pairNo), ("$key", RowCodec.Key(notice.Path))) is not null)
            return true;
        return false;
    }
}
