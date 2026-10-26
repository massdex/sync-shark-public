using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal sealed partial class NoticeTable
{
    private bool SuppressDedicatedRetry(SqliteTransaction transaction, long pairNo, ref Notice notice)
    {
        var incoming = notice;
        return RetryNoticeFold.SuppressDedicated(ref notice,
            reason => HasDedicatedRetry(transaction, pairNo, incoming.Path, reason),
            choice => CloseResolved(transaction, pairNo,
                ShortId.Make("n", incoming.PairId, "40", RowCodec.Key(incoming.Path), "sync-retry"), choice),
            retry => FoldUnreadableRetry(transaction, pairNo, retry));
    }

    private bool HasDedicatedRetry(SqliteTransaction transaction, long pairNo, string path, string reason) =>
        StoreSchema.Scalar(_connection, transaction,
            "SELECT 1 FROM notice INDEXED BY nt_pair_reason_path WHERE pair_no=$p AND reason_key=$reason " +
            "AND path_key=$key AND " + ActionableSql("state") + " LIMIT 1;",
            ("$p", pairNo), ("$key", RowCodec.Key(path)), ("$reason", reason)) is not null;

    private Notice FoldUnreadableRetry(SqliteTransaction transaction, long pairNo, Notice notice)
    {
        // Refresh exact scan debt even while its cause is suppressed, rather than duplicating it.
        foreach (var reason in PlanNotices.ObservationReasons)
        {
            var id = ShortId.Make("n", notice.PairId, "30", RowCodec.Key(notice.Path), reason.Key);
            if (ById(pairNo, id, transaction) is { Closed: false } observation)
                return observation.Notice with { Detail = notice.Detail, CauseKey = notice.CauseKey, CauseIdentity = notice.CauseIdentity,
                    FailureRetry = notice.FailureRetry ?? observation.Notice.FailureRetry };
        }
        return notice;
    }
}
