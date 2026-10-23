using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    /// <summary>
    /// Writes the per-item verification notice while the pair has at most <see cref="StorageNotices.NoticeAggregateLimit"/>
    /// open item notices, and replaces the whole item family with one pair summary above it. The entity debts
    /// (rehash flags, receipts, durable queues) are never touched here.
    /// </summary>
    internal void RecordPointerVerificationNotice(string pairId, Notice itemNotice, string itemFamily,
        Notice summaryNotice, string summaryFamily) => InStore(() =>
    {
        var pair = RequirePair(pairId);
        using var tx = _connection.BeginTransaction();
        // One pair summary already represents every item; never add a per-file notice beside it. This is the
        // steady-state path above the limit: one indexed point lookup per reported item, no catalog scan.
        if (HasOpenFamily(tx, pair, summaryFamily))
        {
            tx.Commit();
            return;
        }
        // An ignored pair cause suppresses the whole family: the debt stays durable, but no notice reopens.
        if (StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM notice INDEXED BY nt_pair_reason WHERE pair_no=$p AND reason_key=$reason AND choice='ignore' LIMIT 1;",
            ("$p", pair), ("$reason", summaryNotice.ReasonKey)) is not null)
        {
            CloseFamily(tx, pair, summaryFamily);
            tx.Commit();
            return;
        }
        // The item family is object-scoped, so the threshold must count the whole operation family for the
        // pair: "storage:pointer-verify:<entity hash>:...". Bounded at limit+1.
        var operationPrefix = StorageNotices.OperationPrefix(StorageNotices.PointerVerificationOperation);
        var operationEnd = StorageNotices.OperationEnd(StorageNotices.PointerVerificationOperation);
        var openItems = Convert.ToInt64(StoreSchema.Scalar(_connection, tx, $"""
            SELECT COUNT(*) FROM (
              SELECT 1 FROM notice INDEXED BY nt_pair_reason
              WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND {NoticeTable.ActionableSql("state")}
                AND COALESCE(choice,'')<>'ignore' LIMIT $cap);
            """, ("$p", pair), ("$lo", operationPrefix), ("$hi", operationEnd),
            ("$cap", StorageNotices.NoticeAggregateLimit + 1)));
        // Existing deployments can already have a per-file flood. Re-reporting an existing
        // family must collapse that flood too, but exactly twenty repeated items stay per-item.
        if (openItems < StorageNotices.NoticeAggregateLimit ||
            openItems == StorageNotices.NoticeAggregateLimit && HasOpenFamily(tx, pair, itemFamily))
        {
            ReportStorageNotice(tx, pair, itemNotice, itemFamily);
        }
        else
        {
            // Replace every per-file verification notice with the one summary; the pair summary operation is
            // deliberately outside this range.
            CloseRange(tx, pair, operationPrefix, operationEnd);
            ReportStorageNotice(tx, pair, summaryNotice, summaryFamily);
        }
        tx.Commit();
    });

    /// <summary>Closes the pair verification summary once every affected entity's ordinary verification debt is
    /// gone. Called after each successful entity verification; any remaining debt keeps it open.</summary>
    internal void RetirePointerVerificationSummary(string pairId, StorageNotice summary) => InStore(() =>
    {
        if (PairNo(pairId) is { } pair) RetirePointerVerificationSummary(pair, summary);
    });

    /// <summary>Scan-end retirement, so a run whose verification debt was deleted or otherwise cleared without a
    /// per-item completion still closes the standing summary. A scan with no failures writes no item notice.</summary>
    internal void RetirePointerVerificationSummaries(string pairId) => InStore(() =>
    {
        if (PairNo(pairId) is not { } pair || PairRecord(pairId) is not { } definition) return;
        RetirePointerVerificationSummary(pair, new StorageNotice(definition.A.RootId, string.Empty,
            definition.B.RootId, StorageNotices.PointerVerificationPairOperation));
        RetirePointerVerificationSummary(pair, new StorageNotice(definition.B.RootId, string.Empty,
            definition.A.RootId, StorageNotices.PointerVerificationPairOperation));
    });

    private void RetirePointerVerificationSummary(long pair, StorageNotice summary)
    {
        if (!_storageEnabled) return;
        var family = StorageNotices.Prefix(summary);
        if (!HasOpenFamily(null, pair, family)) return;
        // Deaggregation would hide unresolved failures until the next scan, so the summary stands until every
        // affected entity's durable debt is gone, not merely until the count is back under the limit.
        if (PointerVerificationDebtCount(summary.RootId, summary.RemoteBinding, 1) != 0) return;
        CloseFamily(null, pair, family);
    }

    /// <summary>Queues the same durable offline request that per-item Retry uses, for every affected entity in the
    /// pair. Returns how many rows were inserted or re-requested; never loads item states into memory.</summary>
    internal int QueuePointerVerificationHydrationForPair(string rootId, string remoteBinding) => InStore(() =>
    {
        if (!_storageEnabled) return 0;
        if (RootNo(rootId) is not { } root) return 0;
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, """
            INSERT INTO storage_hydration(root_id,object_id,due)
            SELECT $root, f.object_id, $due
            FROM file_state f INDEXED BY fs_pointer_verify
            WHERE f.root_no=$r AND (f.flags & 1280) = 1280
              AND EXISTS(SELECT 1 FROM storage_item s
                         WHERE s.root_id=$root AND s.object_id=f.object_id AND s.remote_binding=$b)
            ON CONFLICT(root_id,object_id) DO UPDATE SET due=excluded.due;
            """, ("$root", rootId), ("$r", root), ("$b", remoteBinding), ("$due", StorageDue.UserRequested));
        var changed = StoreSchema.Changes(_connection, tx);
        tx.Commit();
        return (int)Math.Min(int.MaxValue, changed);
    });

    // Counts only OwnedPointer|NeedsRehash entities with a durable receipt. The partial index holds exactly those
    // debt rows, so the bounded subquery stops at cap matches and never scans healthy owned pointers.
    private int PointerVerificationDebtCount(string rootId, string remoteBinding, int cap)
    {
        if (!_storageEnabled || RootNo(rootId) is not { } root || cap <= 0) return 0;
        var count = Convert.ToInt64(StoreSchema.Scalar(_connection, null, """
            SELECT COUNT(*) FROM (
              SELECT 1 FROM file_state f INDEXED BY fs_pointer_verify
              WHERE f.root_no=$r AND (f.flags & 1280) = 1280
                AND EXISTS(SELECT 1 FROM storage_item s
                           WHERE s.root_id=$root AND s.object_id=f.object_id AND s.remote_binding=$b)
              LIMIT $cap);
            """, ("$r", root), ("$root", rootId), ("$b", remoteBinding), ("$cap", cap)));
        return (int)Math.Min(int.MaxValue, count);
    }

    private bool HasOpenFamily(SqliteTransaction? tx, long pair, string familyPrefix) =>
        StoreSchema.Scalar(_connection, tx, $"""
            SELECT 1 FROM notice INDEXED BY nt_pair_reason
            WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND {NoticeTable.ActionableSql("state")}
              AND COALESCE(choice,'')<>'ignore' LIMIT 1;
            """, ("$p", pair), ("$lo", familyPrefix), ("$hi", familyPrefix + "￿")) is not null;

    private void CloseFamily(SqliteTransaction? tx, long pair, string familyPrefix) =>
        CloseRange(tx, pair, familyPrefix, familyPrefix + "￿");

    private void CloseRange(SqliteTransaction? tx, long pair, string lo, string hi) =>
        _notices.CloseWhere(tx, "resolved", $"""
            pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND {NoticeTable.ActionableSql("state")}
              AND COALESCE(choice,'')<>'ignore';
            """, ("$p", pair), ("$lo", lo), ("$hi", hi));
}
