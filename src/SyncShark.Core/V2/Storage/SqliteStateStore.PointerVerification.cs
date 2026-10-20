using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private void QueuePointerMoveAcknowledgment(SqliteTransaction tx, PairDef pair, SyncAction action, ActionResult result)
    {
        if (pair.A.Provider != SyncProviders.Local || pair.B.Provider != SyncProviders.Dropbox || action.Target != Side.B ||
            action.Source is not { Kind: ItemKind.File } local || (local.Flags & ItemFlags.OwnedPointer) == 0 ||
            (local.Flags & ~ItemFlags.OwnedPointer) != 0 || result.NewB is not { Kind: ItemKind.File } remote ||
            !PairPath.Same(local.Path, remote.Path) || local.Hash is null || remote.Hash != local.Hash || remote.Size != local.Size)
            return;
        var json = StoreSchema.Scalar(_connection, tx,
            "SELECT remote_json FROM storage_item WHERE root_id=$r AND object_id=$o AND remote_binding=$b;",
            ("$r", pair.A.RootId), ("$o", local.ObjectId), ("$b", pair.B.RootId)) as string;
        var receipt = json is null ? null : JsonSerializer.Deserialize<PilotRemoteEntry>(json);
        if (receipt is null || receipt.Id != remote.ObjectId || receipt.Revision != remote.VersionId ||
            receipt.ContentHash != remote.Hash || receipt.Size != remote.Size) return;
        // Retain the native FileIdentity receipt (including its timestamp); only the verified location changes.
        receipt = receipt with { RelativePath = local.Path };
        StoreSchema.Execute(_connection, tx, """
            INSERT INTO storage_settlement(root_id,object_id,remote_binding,remote_json,kind)
            VALUES($r,$o,$b,$j,$k) ON CONFLICT(root_id,object_id,remote_binding)
            DO UPDATE SET job_id=excluded.job_id,remote_json=excluded.remote_json,kind=excluded.kind,state='pending',due=0;
            """, ("$r", pair.A.RootId), ("$o", local.ObjectId), ("$b", pair.B.RootId),
            ("$j", JsonSerializer.Serialize(receipt)), ("$k", StorageSettlement.AcknowledgeMove));
    }

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
