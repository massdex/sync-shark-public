using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Same transaction as settling the non-applied intent. Retain any held evidence already owned by
    // the retry row, but wake it now; unchanged inventory cursors must not lose this item's replan debt.
    private void QueueUnconfirmedDeleteSource(SqliteTransaction transaction, long pairNo, string pairId, SyncAction action,
        ActionResult result)
    {
        if (result.DeleteSourceIdentityMissing)
        {
            var review = MissingDeleteIdentityReview.Create(pairId, action);
            StoreSchema.Execute(_connection, transaction,
                "INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES($p,$key,$json,$due) " +
                "ON CONFLICT(pair_no,path_key) DO UPDATE SET json=excluded.json,retry_after=excluded.retry_after;",
                ("$p", pairNo), ("$key", RowCodec.Key(action.Path)), ("$json", JsonSerializer.Serialize(review, RowCodec.Json)),
                ("$due", review.RetryAfter.ToUnixTimeMilliseconds()));
            _notices.Add(transaction, pairNo, review.PendingNotice!, NowMs());
            return;
        }
        var retry = new SyncRetry(action.Path, "sync", 0, result.Reason ?? DeleteSourceNotice.ReasonKey,
            result.RetryAfterSeconds is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(NowMs()).AddSeconds(result.RetryAfterSeconds.Value) : DateTimeOffset.MinValue)
        {
            DeleteBlockedDetail = result.Reason ?? "The deletion source could not be verified. Nothing was deleted; sync will retry.",
            DeleteBlockedDeferred = result.RetryAfterSeconds is > 0
        };
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES($p,$key,$json,$due) " +
            "ON CONFLICT(pair_no,path_key) DO UPDATE SET retry_after=excluded.retry_after," +
            "json=json_set(sync_retry.json,'$.RetryAfter',json_extract(excluded.json,'$.RetryAfter')," +
            "'$.DeleteBlockedDetail',json_extract(excluded.json,'$.DeleteBlockedDetail')," +
            "'$.DeleteBlockedDeferred',json(CASE WHEN json_extract(excluded.json,'$.DeleteBlockedDeferred')=1 THEN 'true' ELSE 'false' END));",
            ("$p", pairNo), ("$key", RowCodec.Key(action.Path)), ("$json", JsonSerializer.Serialize(retry, RowCodec.Json)),
            ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()));
    }
}
