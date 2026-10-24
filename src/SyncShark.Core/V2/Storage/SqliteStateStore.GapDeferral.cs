namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // A concurrent save has no verified result to rotate and is not an indexing failure.
    void ILocalGapStore.DeferGapHash(string rootId, LocalGapEpoch epoch, ItemState item, string reason) => InIndexingStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return;
        var retry = FailureRetryOwner.ReportedFailure(null, _clock.GetUtcNow(), retainSleeping: false, countAttempt: false);
        StoreSchema.Execute(_connection, null,
            "UPDATE local_gap_item SET defer_count=defer_count+1,defer_reason=$why,retry_after=$due " +
            "WHERE root_no=$r AND object_id=$o AND epoch=$e AND hash IS NULL AND expected_json=$item;",
            ("$r", root.Value), ("$o", item.ObjectId), ("$e", epoch.Epoch),
            ("$item", System.Text.Json.JsonSerializer.Serialize(item)), ("$why", reason),
            ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()));
    });

    void ILocalGapStore.DeferGapResult(string rootId, LocalGapResult result, string reason, bool recordFailure) => InIndexingStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return;
        var owner = IndexNoticeOwner(rootId);
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, "UPDATE local_gap SET next_result=next_result+1 WHERE root_no=$r;", ("$r", root.Value));
        // One attempt per run: the new sequence is beyond its captured watermark. No result is discarded.
        StoreSchema.Execute(_connection, tx,
            "UPDATE local_gap_item SET result_seq=(SELECT next_result FROM local_gap WHERE root_no=$r)," +
            "defer_count=1,defer_reason=$why WHERE root_no=$r AND object_id=$o AND epoch=$e AND result_seq=$s;",
            ("$r", root.Value), ("$o", result.Expected.ObjectId), ("$e", result.Epoch), ("$s", result.Sequence), ("$why", reason));
        var changed = StoreSchema.Changes(_connection, tx) != 0;
        IndexFailureTransitionForTest?.Invoke();
        if (changed && recordFailure)
            SaveGapFailure(tx, rootId, root.Value, result.Epoch, result.Expected.ObjectId, reason, ProofCause, owner);
        tx.Commit();
    });
}
