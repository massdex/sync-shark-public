using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public void SetStoragePolicy(string rootId, string objectId, string mode) => InStore(() =>
    {
        if (mode is not ("inherit" or "offline" or "online-only")) throw new ArgumentException("Unknown storage mode.", nameof(mode));
        // Policy is item-owned, not path-owned: a move preserves it; a replacement does not inherit it.
        if (mode == "inherit")
            StoreSchema.Execute(_connection, null, "DELETE FROM storage_policy WHERE root_id=$r AND object_id=$o;", ("$r", rootId), ("$o", objectId));
        else
            StoreSchema.Execute(_connection, null,
                "INSERT INTO storage_policy(root_id,object_id,mode,revision) VALUES($r,$o,$m,1) " +
                "ON CONFLICT(root_id,object_id) DO UPDATE SET mode=excluded.mode,revision=revision+1;",
                ("$r", rootId), ("$o", objectId), ("$m", mode));
    });

    public string ResolveStoragePolicy(RootDef root, string path) => InStore(() =>
    {
        var rootNo = RootNo(root.RootId);
        // Walk only path ancestors (bounded by path depth), never a subtree or every rule.
        var current = path.Replace('\\', '/').Trim('/');
        while (current.Length > 0 && rootNo is not null)
        {
            var items = _reads.AtKey(rootNo.Value, current, null);
            foreach (var row in items)
            {
                var mode = StoreSchema.Scalar(_connection, null,
                    "SELECT mode FROM storage_policy WHERE root_id=$r AND object_id=$o;",
                    ("$r", root.RootId), ("$o", row.Item.ObjectId)) as string;
                if (mode is not null) return mode;
            }
            var slash = current.LastIndexOf('/');
            current = slash < 0 ? "" : current[..slash];
        }
        return StoreSchema.Scalar(_connection, null,
            "SELECT mode FROM storage_policy WHERE root_id=$r AND object_id=$o;",
            ("$r", root.RootId), ("$o", root.RootObjectId)) as string ?? "offline";
    });

    public IReadOnlyList<StorageFolderPolicy> StoragePolicies(RootDef root, string afterObjectId = "", int limit = 128) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null, """
            SELECT p.object_id,CASE WHEN p.object_id=$root THEN '' ELSE
              CASE WHEN d.path='' THEN f.name ELSE d.path||'/'||f.name END END,p.mode,p.revision
            FROM storage_policy p LEFT JOIN root r ON r.root_id=p.root_id
            LEFT JOIN file_state f ON f.root_no=r.root_no AND f.object_id=p.object_id
            LEFT JOIN dir d ON d.dir_no=f.dir_no
            WHERE p.root_id=$r AND p.object_id>$after AND (f.kind=1 OR p.object_id=$root)
            ORDER BY p.object_id LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$r", root.RootId);
        command.Parameters.AddWithValue("$root", root.RootObjectId);
        command.Parameters.AddWithValue("$after", afterObjectId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 512));
        var result = new List<StorageFolderPolicy>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        return (IReadOnlyList<StorageFolderPolicy>)result;
    });

    public string SetStoragePolicyAtPath(RootDef root, string path, string mode) => InStore(() =>
    {
        var relative = path.Replace('\\', '/').Trim('/');
        if (relative.Split('/').Any(part => part is "." or "..") || Path.IsPathRooted(path) || path.Contains(':'))
            throw new ArgumentException("Use a relative path inside the pair, or an empty path for its root.", nameof(path));
        var objectId = root.RootObjectId;
        if (relative.Length != 0)
        {
            var rootNo = RootNo(root.RootId);
            var rows = rootNo is null ? [] : _reads.AtKey(rootNo.Value, relative, null);
            if (rows.Count != 1) throw new SyncCliTargetException("The path has no unique indexed identity. Index the pair first, then retry; no policy changed.");
            objectId = rows[0].Item.ObjectId;
        }
        SetStoragePolicy(root.RootId, objectId, mode);
        return relative;
    });

    /// <summary>Bounded policy page with SQL-only descendant counts; unknown observations stay unknown.</summary>
    public IReadOnlyList<StorageFolderSummary> StorageFolderSummaries(RootDef root, int limit = 50, int offset = 0) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null, """
            WITH folders AS (
              SELECT p.object_id,CASE WHEN p.object_id=$root THEN '' ELSE
                CASE WHEN d.path='' THEN f.name ELSE d.path||'/'||f.name END END AS path,
                CASE WHEN p.object_id=$root THEN '' ELSE
                  CASE WHEN d.path_key='' THEN f.name_key ELSE d.path_key||'/'||f.name_key END END AS path_key
              FROM storage_policy p LEFT JOIN root r ON r.root_id=p.root_id
              LEFT JOIN file_state f ON f.root_no=r.root_no AND f.object_id=p.object_id
              LEFT JOIN dir d ON d.dir_no=f.dir_no
              WHERE p.root_id=$r AND p.mode='online-only' AND (f.kind=1 OR p.object_id=$root)
              ORDER BY p.object_id LIMIT $limit OFFSET $offset
            )
            SELECT folders.object_id,folders.path,COUNT(f.object_id),
              COALESCE(SUM(CASE WHEN s.availability='AvailableOffline' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN s.availability='OnlineOnly' THEN 1 ELSE 0 END),0)
            FROM folders LEFT JOIN root r ON r.root_id=$r
            LEFT JOIN dir d ON folders.path_key='' OR d.path_key=folders.path_key
              OR (d.path_key>=folders.path_key||'/' AND d.path_key<folders.path_key||'0')
            LEFT JOIN file_state f ON f.root_no=r.root_no AND f.dir_no=d.dir_no AND f.kind=0
            LEFT JOIN (SELECT root_id,object_id,CASE WHEN MIN(availability)=MAX(availability) THEN MIN(availability) ELSE 'Unknown' END AS availability FROM storage_item
              WHERE root_id=$r GROUP BY root_id,object_id) s ON s.root_id=$r AND s.object_id=f.object_id
            GROUP BY folders.object_id,folders.path ORDER BY folders.object_id;
            """);
        command.Parameters.AddWithValue("$r", root.RootId);
        command.Parameters.AddWithValue("$root", root.RootObjectId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var result = new List<StorageFolderSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        return (IReadOnlyList<StorageFolderSummary>)result;
    });

    // Runs in the same transaction that retires the upload intent, including reconciled
    // effects. A restart can see either the unresolved intent or the durable receipt, never neither.
    private void QueueConfirmedStorageReceipt(Microsoft.Data.Sqlite.SqliteTransaction transaction,
        PairDef pair, SyncAction action, ActionResult result)
    {
        if (action.Kind is not (ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth)) return;
        var localRoot = action.Target == Side.B ? pair.A : pair.B;
        var remoteRoot = action.Target == Side.B ? pair.B : pair.A;
        var remote = action.Target == Side.B ? result.NewB : result.NewA;
        var local = action.Source;
        if (localRoot.Provider != SyncProviders.Local || remoteRoot.Provider != SyncProviders.Dropbox ||
            local is not { Kind: ItemKind.File, Hash: not null } ||
            remote is not { Kind: ItemKind.File, VersionId: not null, Hash: not null } ||
            !string.Equals(local.Path, remote.Path, StringComparison.OrdinalIgnoreCase) ||
            local.Size != remote.Size || !string.Equals(local.Hash, remote.Hash, StringComparison.OrdinalIgnoreCase)) return;
        var receipt = new PilotRemoteEntry(local.Path, PilotEntryKind.File, remote.ObjectId,
            remote.VersionId, remote.Size, remote.Hash, local.Mtime);
        StoreSchema.Execute(_connection, transaction, """
            INSERT INTO storage_settlement(root_id,object_id,remote_binding,remote_json)
            VALUES($r,$o,$b,$j) ON CONFLICT(root_id,object_id,remote_binding)
            DO UPDATE SET remote_json=excluded.remote_json,state='pending';
            """, ("$r", localRoot.RootId), ("$o", local.ObjectId), ("$b", remoteRoot.RootId),
            ("$j", JsonSerializer.Serialize(receipt)));
    }

    // Called only with an exact confirmed upload/remote receipt. Conversion retries do not upload.
    public void QueueStorageSettlement(string rootId, string objectId, string remoteBinding, PilotRemoteEntry remote) => InStore(() =>
    {
        if (remote.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(remote.Revision) || string.IsNullOrWhiteSpace(remote.ContentHash))
            throw new ArgumentException("Settlement requires a confirmed revision and content hash.", nameof(remote));
        StoreSchema.Execute(_connection, null, """
            INSERT INTO storage_settlement(root_id,object_id,remote_binding,remote_json)
            VALUES($r,$o,$b,$j) ON CONFLICT(root_id,object_id,remote_binding)
            DO UPDATE SET remote_json=excluded.remote_json,state='pending';
            """, ("$r", rootId), ("$o", objectId), ("$b", remoteBinding), ("$j", JsonSerializer.Serialize(remote)));
    });

    public IReadOnlyList<StorageSettlement> StorageSettlementPage(long afterJobId = 0, int limit = 128) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT job_id,root_id,object_id,remote_binding,remote_json FROM storage_settlement " +
            "WHERE state='pending' AND job_id>$after ORDER BY job_id LIMIT $limit;");
        command.Parameters.AddWithValue("$after", afterJobId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 512));
        var result = new List<StorageSettlement>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            JsonSerializer.Deserialize<PilotRemoteEntry>(reader.GetString(4))!));
        return (IReadOnlyList<StorageSettlement>)result;
    });

    public void CompleteStorageSettlement(StorageSettlement job, StorageObserved observed) => InStore(() =>
    {
        using var tx = _connection.BeginTransaction();
        // CAS protects a newer upload receipt arriving while native conversion was running.
        var json = JsonSerializer.Serialize(job.Remote);
        var current = StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM storage_settlement WHERE job_id=$id AND remote_json=$j AND state='pending';",
            ("$id", job.JobId), ("$j", json));
        if (current is null) return;
        StoreSchema.Execute(_connection, tx, """
            INSERT INTO storage_item(root_id,object_id,remote_binding,remote_json,availability,pinned,in_sync)
            VALUES($r,$o,$b,$j,$a,$p,$s) ON CONFLICT(root_id,object_id,remote_binding)
            DO UPDATE SET remote_json=excluded.remote_json,availability=excluded.availability,
              pinned=excluded.pinned,in_sync=excluded.in_sync;
            """, ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding), ("$j", json),
            ("$a", observed.Availability), ("$p", observed.Pinned ? 1 : 0), ("$s", observed.InSync ? 1 : 0));
        StoreSchema.Execute(_connection, tx, "DELETE FROM storage_settlement WHERE job_id=$id;", ("$id", job.JobId));
        tx.Commit();
    });
}
