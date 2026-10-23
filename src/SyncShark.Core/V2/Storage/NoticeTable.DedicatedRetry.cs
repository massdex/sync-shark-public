using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal sealed partial class NoticeTable
{
    private bool SuppressDedicatedRetry(SqliteTransaction transaction, long pairNo, ref Notice notice)
    {
        // Dedicated explanations own the retry in either publication order. Ignore suppresses
        // their cause only; conflict successors and unresolved evidence remain protected.
        if (notice.ReasonKey is ExecutionReasons.ConflictCopyBlocked or InterruptedDelete.UnverifiedReason or
            "deletion-evidence-pending" or "unreadable")
            CloseResolved(transaction, pairNo,
                ShortId.Make("n", notice.PairId, "40", RowCodec.Key(notice.Path), "sync-retry"),
                notice.ReasonKey == "deletion-evidence-pending" ? "evidence-pending" : "superseded");
        if (notice.ReasonKey is not ("content-unconfirmed" or "sync-retry")) return false;
        if (HasDedicatedRetry(transaction, pairNo, notice.Path, ExecutionReasons.ConflictCopyBlocked) ||
            HasDedicatedRetry(transaction, pairNo, notice.Path, InterruptedDelete.UnverifiedReason)) return true;
        if (notice.ReasonKey == "sync-retry")
        {
            if (HasDedicatedRetry(transaction, pairNo, notice.Path, "deletion-evidence-pending")) return true;
            notice = FoldUnreadableRetry(transaction, pairNo, notice);
        }
        return false;
    }

    private bool HasDedicatedRetry(SqliteTransaction transaction, long pairNo, string path, string reason) =>
        StoreSchema.Scalar(_connection, transaction,
            "SELECT 1 FROM notice INDEXED BY nt_pair_reason_path WHERE pair_no=$p AND reason_key=$reason " +
            "AND path_key=$key AND " + ActionableSql("state") + " LIMIT 1;",
            ("$p", pairNo), ("$key", RowCodec.Key(path)), ("$reason", reason)) is not null;

    private Notice FoldUnreadableRetry(SqliteTransaction transaction, long pairNo, Notice notice)
    {
        // Refresh exact scan debt even while its cause is suppressed, rather than duplicating it.
        var id = ShortId.Make("n", notice.PairId, "30", RowCodec.Key(notice.Path), "unreadable");
        return ById(pairNo, id, transaction) is { Closed: false } unreadable
            ? unreadable.Notice with { Detail = notice.Detail, CauseKey = notice.CauseKey,
                FailureRetry = notice.FailureRetry ?? unreadable.Notice.FailureRetry }
            : notice;
    }
}
