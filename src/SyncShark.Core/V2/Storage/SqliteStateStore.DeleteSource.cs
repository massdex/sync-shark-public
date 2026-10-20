using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Same transaction as settling the non-applied intent. Retain any held evidence already owned by
    // the retry row, but wake it now; unchanged inventory cursors must not lose this item's replan debt.
    private void QueueUnconfirmedDeleteSource(SqliteTransaction transaction, long pairNo, SyncAction action,
        ActionResult result)
    {
        var retry = new SyncRetry(action.Path, "sync", 0, result.Reason ?? DeleteSourceGuard.ReasonKey,
            DateTimeOffset.MinValue);
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES($p,$key,$json,$due) " +
            "ON CONFLICT(pair_no,path_key) DO UPDATE SET retry_after=excluded.retry_after," +
            "json=json_set(sync_retry.json,'$.RetryAfter',json_extract(excluded.json,'$.RetryAfter'));",
            ("$p", pairNo), ("$key", RowCodec.Key(action.Path)), ("$json", JsonSerializer.Serialize(retry, RowCodec.Json)),
            ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()));
    }
}
