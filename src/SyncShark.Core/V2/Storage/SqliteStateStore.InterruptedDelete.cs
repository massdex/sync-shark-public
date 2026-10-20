using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal const string InterruptedDeleteRestoreKind = "interrupted-delete-restore";

    // Same transaction as retiring the intent and baseline: a crash cannot lose the path's
    // scheduling debt, even with unchanged inventory cursors on the following run.
    private void QueueInterruptedDeleteRestore(SqliteTransaction transaction, long pairNo, string pairId,
        long rootNoA, long rootNoB, SyncAction action, ActionResult result)
    {
        if (action.Kind != ActionKind.Delete || action.Rule is 26 or 32)
            throw new InvalidOperationException("Only an absence-propagating delete can queue interrupted-delete restoration.");
        var source = (action.Target == Side.A ? result.NewB : result.NewA) ??
            throw new InvalidOperationException("Interrupted-delete restoration needs its surviving source identity.");
        var proposal = new SyncAction(action.ActionId + "-restore", action.Rule,
            source.Kind == ItemKind.Folder ? ActionKind.MkDir : ActionKind.CopyNew, action.Target,
            action.Path, null, source, null, false, null);
        var retry = new SyncRetry(action.Path, InterruptedDeleteRestoreKind, 0,
            "The original source survived an interrupted delete; the missing copy is queued for restore.", DateTimeOffset.MinValue,
            ProposedActions: [proposal]);
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES($p,$key,$json,$due) " +
            "ON CONFLICT(pair_no,path_key) DO UPDATE SET json=excluded.json,retry_after=excluded.retry_after;",
            ("$p", pairNo), ("$key", RowCodec.Key(action.Path)), ("$json", JsonSerializer.Serialize(retry, RowCodec.Json)),
            ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()));
        // The absence is attributable to our own interrupted effect, not a user. Retire any
        // held deletion evidence at this exact path, on either side, in the same transaction.
        StoreSchema.Execute(_connection, transaction,
            "DELETE FROM deletion_evidence WHERE pair_no=$p AND root_no IN ($a,$b) AND path_key=$key;",
            ("$p", pairNo), ("$a", rootNoA), ("$b", rootNoB), ("$key", RowCodec.Key(action.Path)));
    }

    public void RefreshInterruptedDeleteNotice(string pairId) => InStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        RefreshInterruptedDeleteNotice(transaction, RequirePair(pairId), pairId, NowMs());
        transaction.Commit();
    });

    private void RefreshInterruptedDeleteNotice(SqliteTransaction transaction, long pairNo, string pairId, long now)
    {
        // The pair primary-key prefix bounds this count to its durable retry work, not file inventory.
        var count = (long)StoreSchema.Scalar(_connection, transaction,
            "SELECT COUNT(*) FROM sync_retry WHERE pair_no=$p AND json_extract(json,'$.Kind')=$kind;",
            ("$p", pairNo), ("$kind", InterruptedDeleteRestoreKind))!;
        if (count > 0)
            _notices.Add(transaction, pairNo, new Notice(pairId, 40, NoticeKind.Info, string.Empty,
                $"{count} items are queued for restore after an interrupted delete. Surviving copies are preserved; failed restores remain available for Retry.",
                InterruptedDeleteRestoreKind), now);
        else
            StoreSchema.Execute(_connection, transaction,
                "UPDATE notice SET state='closed',choice='resolved' WHERE pair_no=$p AND reason_key=$kind AND state='open';",
                ("$p", pairNo), ("$kind", InterruptedDeleteRestoreKind));
    }
}
