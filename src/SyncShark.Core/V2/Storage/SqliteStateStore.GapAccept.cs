namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal const string GapAcceptMetadataSql =
        "SELECT 1 FROM file_state INDEXED BY fs_obj WHERE root_no=$r AND object_id=$o AND (size!=$s OR mtime!=$m) LIMIT 1;";
    internal const string GapAcceptPublishSql =
        "UPDATE file_state INDEXED BY fs_obj SET hash=$h,flags=flags & ~256 WHERE root_no=$r AND object_id=$o;";

    // One entity is the page: all aliases and its durable handoff commit atomically,
    // then the caller releases admission before accepting the next result.
    bool ILocalGapStore.AcceptGapResult(string rootId, LocalGapResult result) => InIndexingStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || ReadGap(root)?.Epoch != result.Epoch ||
            _reads.ByObject(root.Value, result.Expected.ObjectId) is not { } current || !SameGapItem(current, result.Expected)) return false;
        using var tx = _connection.BeginTransaction();
        var exists = StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM local_gap_item WHERE root_no=$r AND object_id=$o AND epoch=$e AND result_seq=$s AND hash=$h;",
            ("$r", root.Value), ("$o", current.ObjectId), ("$e", result.Epoch), ("$s", result.Sequence), ("$h", result.Hash));
        if (exists is null || StoreSchema.Scalar(_connection, tx,
            GapAcceptMetadataSql,
            ("$r", root.Value), ("$o", current.ObjectId), ("$s", current.Size), ("$m", current.Mtime.UtcTicks)) is not null) return false;
        // Alias publication and durable planning handoff are atomic with retiring the gap obligation.
        StoreSchema.Execute(_connection, tx,
            GapAcceptPublishSql,
            ("$r", root.Value), ("$o", current.ObjectId), ("$h", RowCodec.HashBlob(result.Hash)));
        StoreSchema.Execute(_connection, tx,
            "INSERT INTO local_rehash(root_no,object_id,last_attempt,reason,evidence_revision) VALUES($r,$o,$t,$why,$t) " +
            "ON CONFLICT(root_no,object_id) DO UPDATE SET last_attempt=excluded.last_attempt,reason=excluded.reason;",
            ("$r", root.Value), ("$o", current.ObjectId), ("$t", NextRehashOrder(root.Value, tx)),
            ("$why", "journal gap verified; planning pending"));
        StoreSchema.Execute(_connection, tx, "DELETE FROM local_gap_item WHERE root_no=$r AND object_id=$o;",
            ("$r", root.Value), ("$o", current.ObjectId));
        tx.Commit();
        // Progress describes the latest completed action, not a failure whose entity row was
        // just retired. Other unresolved failures still come from their durable work rows.
        _gapStatus[(rootId, false)] = "content proof accepted; indexing continues";
        return true;
    });

}
