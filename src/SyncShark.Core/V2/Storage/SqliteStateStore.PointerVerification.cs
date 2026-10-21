using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private void QueuePointerMoveAcknowledgment(SqliteTransaction tx, PairDef pair, ActionResult result)
    {
        var local = result.NewA;
        var remote = result.NewB;
        if (pair.A.Provider != SyncProviders.Local || pair.B.Provider != SyncProviders.Dropbox ||
            local is not { Kind: ItemKind.File } || (local.Flags & ItemFlags.OwnedPointer) == 0 ||
            remote is not { Kind: ItemKind.File, VersionId: not null } ||
            !PairPath.Same(local.Path, remote.Path) || local.Hash is null || remote.Hash != local.Hash || remote.Size != local.Size)
            return;
        var json = StoreSchema.Scalar(_connection, tx,
            "SELECT remote_json FROM storage_item WHERE root_id=$r AND object_id=$o AND remote_binding=$b;",
            ("$r", pair.A.RootId), ("$o", local.ObjectId), ("$b", pair.B.RootId)) as string;
        var receipt = json is null ? null : JsonSerializer.Deserialize<PilotRemoteEntry>(json);
        if (receipt is null || receipt.Id != remote.ObjectId ||
            receipt.ContentHash != remote.Hash || receipt.Size != remote.Size) return;
        // Move may mint a new revision without changing bytes. Keep the proven native timestamp,
        // but publish the resulting path/revision durably in the same transaction as the Move.
        receipt = receipt with { RelativePath = local.Path, Revision = remote.VersionId };
        var updatedJson = JsonSerializer.Serialize(receipt);
        StoreSchema.Execute(_connection, tx, """
            UPDATE storage_item SET remote_json=$j,in_sync=0
            WHERE root_id=$r AND object_id=$o AND remote_binding=$b;
            INSERT INTO storage_settlement(root_id,object_id,remote_binding,remote_json,kind)
            VALUES($r,$o,$b,$j,$k) ON CONFLICT(root_id,object_id,remote_binding)
            DO UPDATE SET job_id=excluded.job_id,remote_json=excluded.remote_json,kind=excluded.kind,state='pending',due=0
            WHERE storage_settlement.remote_json<>excluded.remote_json OR storage_settlement.kind IS NOT excluded.kind;
            """, ("$r", pair.A.RootId), ("$o", local.ObjectId), ("$b", pair.B.RootId),
            ("$j", updatedJson), ("$k", StorageSettlement.AcknowledgeMove));
    }

    // The scanner queues from a verified, fully dehydrated observation at the published entity path.
    // A stale Unreadable cache bit is allowed for intent only; the service waits for clean published
    // flags and repeats native proof before any effect. NeedsRehash and other holds stay excluded.
    // Folder children converge one entity at a time, using both indexed sides as relocation proof.
    // Never replace an existing job or walk/materialize an entire renamed subtree.
    internal bool QueueVerifiedPointerAcknowledgment(string rootId, string objectId, string remoteBinding,
        PilotRemoteEntry verifiedReceipt) => InStore(() =>
    {
        if (!_storageEnabled || ByObject(rootId, objectId) is not { Kind: ItemKind.File } local ||
            (local.Flags & ~ItemFlags.Unreadable) != ItemFlags.OwnedPointer || local.Path != verifiedReceipt.RelativePath ||
            local.Size != verifiedReceipt.Size || local.Hash != verifiedReceipt.ContentHash) return false;
        // Read through the normal inventory API before opening a transaction: its reader does
        // not carry a transaction. InStore keeps this evidence and the following writes serialized.
        var indexedRemote = verifiedReceipt.Id is { } remoteId ? ByObject(remoteBinding, remoteId) : null;
        using var tx = _connection.BeginTransaction();
        if (StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM storage_settlement WHERE root_id=$r AND object_id=$o AND remote_binding=$b;",
            ("$r", rootId), ("$o", objectId), ("$b", remoteBinding)) is not null) return false;
        var priorJson = StoreSchema.Scalar(_connection, tx,
            "SELECT remote_json FROM storage_item WHERE root_id=$r AND object_id=$o AND remote_binding=$b;",
            ("$r", rootId), ("$o", objectId), ("$b", remoteBinding)) as string;
        var prior = priorJson is null ? null : JsonSerializer.Deserialize<PilotRemoteEntry>(priorJson);
        if (prior is null || (prior with { RelativePath = local.Path }) != verifiedReceipt) return false;
        if (prior.RelativePath != local.Path)
        {
            // A parent Move does not return child receipts. Both indexed entities must now agree
            // before this metadata-only child acknowledgment can adopt the new path/revision.
            var remote = indexedRemote;
            if (remote is not { Kind: ItemKind.File, VersionId: not null } ||
                !PairPath.Same(remote.Path, local.Path) || remote.Hash != prior.ContentHash || remote.Size != prior.Size)
                return false;
            verifiedReceipt = verifiedReceipt with { Revision = remote.VersionId };
        }
        var json = JsonSerializer.Serialize(verifiedReceipt);
        StoreSchema.Execute(_connection, tx, """
            UPDATE storage_item SET remote_json=$j,in_sync=0
            WHERE root_id=$r AND object_id=$o AND remote_binding=$b;
            INSERT INTO storage_settlement(root_id,object_id,remote_binding,remote_json,kind)
            VALUES($r,$o,$b,$j,$k) ON CONFLICT(root_id,object_id,remote_binding) DO NOTHING;
            """, ("$r", rootId), ("$o", objectId), ("$b", remoteBinding),
            ("$j", json), ("$k", StorageSettlement.AcknowledgeMove));
        var inserted = Convert.ToInt64(StoreSchema.Scalar(_connection, tx, "SELECT changes();")) != 0;
        tx.Commit();
        return inserted;
    });

    internal void RequestPointerVerification(string pairId, string rootId, string objectId, string path, PilotRemoteEntry receipt)
    {
        var pair = Pair(pairId);
        if (pair is null || pair.A.RootId != rootId) return;
        StorageNotices.Report(this, pair, path,
            new(rootId, objectId, pair.B.RootId, "pointer-verify", StorageNotices.ReceiptScope(receipt)),
            new IOException("This online-only file changed locally; Sync Shark will verify it. Retry downloads missing ranges without discarding edits, then normal sync verifies the content."));
    }

    internal bool QueuePointerVerificationHydration(string rootId, string objectId) => InStore(() =>
    {
        if (!_storageEnabled || ByObject(rootId, objectId) is not { } local ||
            (local.Flags & ItemFlags.NeedsRehash) == 0) return false;
        StoreSchema.Execute(_connection, null, """
            INSERT INTO storage_hydration(root_id,object_id,due) VALUES($r,$o,$due)
            ON CONFLICT(root_id,object_id) DO UPDATE SET due=excluded.due;
            """, ("$r", rootId), ("$o", objectId), ("$due", StorageDue.UserRequested));
        return true;
    });
}
