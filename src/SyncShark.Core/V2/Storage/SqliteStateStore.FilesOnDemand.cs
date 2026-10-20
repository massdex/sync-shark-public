using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Observe an opt-in made by another process (for example a CLI command while the host runs).
    // This catalog probe belongs to control-plane reads, never per-item write paths.
    // InStore serializes callers: share one cold probe per 15 seconds across both idle loops.
    private bool StorageEnabled()
    {
        if (_storageEnabled) return true;
        var now = _clock.GetTimestamp();
        if (_clock.GetElapsedTime(_storageProbeTimestamp, now) < TimeSpan.FromSeconds(15)) return false;
        _storageProbeTimestamp = now;
        if (FilesOnDemandSchema.IsEnabled(_connection))
        {
            _storageEnabled = true;
            _reads.StorageEnabled = true;
        }
        return _storageEnabled;
    }

    public void EnableStorage() => InStore(() =>
    {
        if (_storageEnabled) return;
        FilesOnDemandSchema.Ensure(_connection);
        _storageEnabled = true;
        _reads.StorageEnabled = true;
    });

    public void SetStoragePolicy(string rootId, string objectId, string mode) => InStore(() =>
    {
        if (mode is not ("inherit" or "offline" or "online-only")) throw new ArgumentException("Unknown storage mode.", nameof(mode));
        if (mode != "online-only" && !IsStorageEnabled) return;
        EnableStorage();
        // Policy and its resumable sweep are one durable intent.
        using var tx = _connection.BeginTransaction();
        WriteStoragePolicy(tx, rootId, objectId, mode);
        tx.Commit();
    });

    private void WriteStoragePolicy(Microsoft.Data.Sqlite.SqliteTransaction tx, string rootId, string objectId, string mode)
    {
        // Policy is item-owned, not path-owned: a move preserves it; a replacement does not inherit it.
        if (mode == "inherit")
            StoreSchema.Execute(_connection, tx, "DELETE FROM storage_policy WHERE root_id=$r AND object_id=$o;", ("$r", rootId), ("$o", objectId));
        else
            StoreSchema.Execute(_connection, tx,
                "INSERT INTO storage_policy(root_id,object_id,mode,revision) VALUES($r,$o,$m,1) " +
                "ON CONFLICT(root_id,object_id) DO UPDATE SET mode=excluded.mode,revision=revision+1;",
                ("$r", rootId), ("$o", objectId), ("$m", mode));
        StoreSchema.Execute(_connection, tx, "INSERT INTO storage_subtree_sweep(root_id,object_id) VALUES($r,$o) " +
            "ON CONFLICT(root_id,object_id) DO UPDATE SET after_dir_key='',after_dir=-1,after_name_key='',after_name='',revision=revision+1;", ("$r", rootId), ("$o", objectId));
    }

    public string ResolveStoragePolicy(RootDef root, string path) => InStore(() => ResolveStoragePolicy(root, path, null));

    private string ResolveStoragePolicy(RootDef root, string path, Microsoft.Data.Sqlite.SqliteTransaction? transaction)
    {
        if (!_storageEnabled) return "offline";
        if (StoreSchema.Scalar(_connection, transaction, "SELECT 1 FROM storage_policy WHERE root_id=$r AND mode='online-only' LIMIT 1;", ("$r", root.RootId)) is null) return "offline";
        var rootNo = RootNo(root.RootId);
        // Walk only path ancestors (bounded by path depth), never a subtree or every rule.
        var current = path.Replace('\\', '/').Trim('/');
        while (current.Length > 0 && rootNo is not null)
        {
            var items = _reads.AtKey(rootNo.Value, current, transaction);
            foreach (var row in items)
            {
                var mode = StoreSchema.Scalar(_connection, transaction,
                    "SELECT mode FROM storage_policy WHERE root_id=$r AND object_id=$o;",
                    ("$r", root.RootId), ("$o", row.Item.ObjectId)) as string;
                if (mode is not null) return mode;
            }
            var slash = current.LastIndexOf('/');
            current = slash < 0 ? "" : current[..slash];
        }
        return StoreSchema.Scalar(_connection, transaction,
            "SELECT mode FROM storage_policy WHERE root_id=$r AND object_id=$o;",
            ("$r", root.RootId), ("$o", root.RootObjectId)) as string ?? "offline";
    }

    public string SetStoragePolicyAtPath(RootDef root, string path, string mode) => InStore(() =>
    {
        if (mode is not ("inherit" or "offline" or "online-only")) throw new ArgumentException("Unknown storage mode.", nameof(mode));
        var relative = StorageRelativePath(path);
        if (mode != "online-only" && !IsStorageEnabled) return relative;
        EnableStorage();
        var objectId = root.RootObjectId;
        if (relative.Length != 0)
        {
            var rootNo = RootNo(root.RootId);
            var rows = rootNo is null ? [] : _reads.AtKey(rootNo.Value, relative, null);
            if (rows.Count != 1)
            {
                if (mode == "inherit")
                {
                    StoreSchema.Execute(_connection, null, "DELETE FROM storage_path_intent WHERE root_id=$r AND path_key=$k;", ("$r", root.RootId), ("$k", RowCodec.Key(relative)));
                    return relative;
                }
                StoreSchema.Execute(_connection, null, "INSERT INTO storage_path_intent(root_id,path_key,path,root_object,mode) VALUES($r,$k,$p,$o,$m) ON CONFLICT(root_id,path_key) DO UPDATE SET path=excluded.path,mode=excluded.mode,next_attempt=0;",
                    ("$r", root.RootId), ("$k", RowCodec.Key(relative)), ("$p", relative), ("$o", root.RootObjectId), ("$m", mode));
                return relative;
            }
            objectId = rows[0].Item.ObjectId;
        }
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, "DELETE FROM storage_path_intent WHERE root_id=$r AND path_key=$k;", ("$r", root.RootId), ("$k", RowCodec.Key(relative)));
        WriteStoragePolicy(tx, root.RootId, objectId, mode);
        tx.Commit();
        return relative;
    });

    /// <summary>Bounded policy page with SQL-only descendant counts; unknown observations stay unknown.</summary>
    public IReadOnlyList<StorageFolderSummary> StorageFolderSummaries(RootDef root, int limit = 50, int offset = 0) => InStore(() =>
    {
        if (!_storageEnabled) return (IReadOnlyList<StorageFolderSummary>)[];
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
        if (!_storageEnabled) return;
        if (action.Kind is not (ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth)) return;
        var localSide = pair.A.Provider == SyncProviders.Local ? Side.A : Side.B;
        var localRoot = localSide == Side.A ? pair.A : pair.B;
        var remoteRoot = localSide == Side.A ? pair.B : pair.A;
        var local = localSide == action.Target ? (localSide == Side.A ? result.NewA : result.NewB) : action.Source;
        var remote = localSide == action.Target ? action.Source : (localSide == Side.A ? result.NewB : result.NewA);
        if (localRoot.Provider != SyncProviders.Local || remoteRoot.Provider != SyncProviders.Dropbox ||
            local is not { Kind: ItemKind.File, Hash: not null } ||
            remote is not { Kind: ItemKind.File, VersionId: not null, Hash: not null } ||
            !string.Equals(local.Path, remote.Path, StringComparison.OrdinalIgnoreCase) ||
            local.Size != remote.Size || !string.Equals(local.Hash, remote.Hash, StringComparison.OrdinalIgnoreCase)) return;
        var receipt = new PilotRemoteEntry(local.Path, PilotEntryKind.File, remote.ObjectId,
            remote.VersionId, remote.Size, remote.Hash, local.Mtime);
        if ((local.Flags & ItemFlags.OwnedPointer) != 0)
        {
            WriteStorageObservation(transaction, localRoot.RootId, local.ObjectId, remoteRoot.RootId, receipt, new("OnlineOnly", false, true));
            return;
        }
        if (ResolveStoragePolicy(localRoot, local.Path, transaction) != "online-only") return;
        StoreSchema.Execute(_connection, transaction, """
            INSERT INTO storage_settlement(root_id,object_id,remote_binding,remote_json)
            VALUES($r,$o,$b,$j) ON CONFLICT(root_id,object_id,remote_binding)
            DO UPDATE SET remote_json=excluded.remote_json,state=CASE WHEN json_extract(storage_settlement.remote_json,'$.Revision') IS NOT json_extract(excluded.remote_json,'$.Revision') OR json_extract(storage_settlement.remote_json,'$.ContentHash') IS NOT json_extract(excluded.remote_json,'$.ContentHash') OR json_extract(storage_settlement.remote_json,'$.Id') IS NOT json_extract(excluded.remote_json,'$.Id') THEN 'pending' ELSE storage_settlement.state END;
            """, ("$r", localRoot.RootId), ("$o", local.ObjectId), ("$b", remoteRoot.RootId),
            ("$j", JsonSerializer.Serialize(receipt)));
    }

    // Called only with an exact confirmed upload/remote receipt. Conversion retries do not upload.
    public void QueueStorageSettlement(string rootId, string objectId, string remoteBinding, PilotRemoteEntry remote) => InStore(() =>
    {
        if (!_storageEnabled) return;
        if (remote.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(remote.Revision) || string.IsNullOrWhiteSpace(remote.ContentHash))
            throw new ArgumentException("Settlement requires a confirmed revision and content hash.", nameof(remote));
        StoreSchema.Execute(_connection, null, """
            INSERT INTO storage_settlement(root_id,object_id,remote_binding,remote_json)
            VALUES($r,$o,$b,$j) ON CONFLICT(root_id,object_id,remote_binding)
            DO UPDATE SET remote_json=excluded.remote_json,state=CASE WHEN json_extract(storage_settlement.remote_json,'$.Revision') IS NOT json_extract(excluded.remote_json,'$.Revision') OR json_extract(storage_settlement.remote_json,'$.ContentHash') IS NOT json_extract(excluded.remote_json,'$.ContentHash') OR json_extract(storage_settlement.remote_json,'$.Id') IS NOT json_extract(excluded.remote_json,'$.Id') THEN 'pending' ELSE storage_settlement.state END;
            """, ("$r", rootId), ("$o", objectId), ("$b", remoteBinding), ("$j", JsonSerializer.Serialize(remote)));
    });

    public IReadOnlyList<StorageSettlement> StorageSettlementPage(long afterJobId = 0, int limit = 128, IReadOnlyCollection<string>? activeRoots = null) => InStore(() =>
    {
        if (!_storageEnabled) return (IReadOnlyList<StorageSettlement>)[];
        using var command = StoreSchema.Command(_connection, null,
            "SELECT w.job_id,w.root_id,w.object_id,w.remote_binding,w.remote_json FROM " + StorageRootsSql +
            " CROSS JOIN storage_settlement w ON w.job_id IN (SELECT sw.job_id FROM storage_settlement sw INDEXED BY storage_pending_root " +
            "WHERE sw.root_id=roots.value AND sw.state='pending' AND sw.due<=$now AND (sw.job_id>$after OR sw.due=$requested) " +
            "ORDER BY CASE WHEN sw.due=$requested THEN 0 ELSE 1 END,sw.job_id LIMIT $limit) " +
            "WHERE (NOT EXISTS(SELECT 1 FROM storage_root_backoff b WHERE b.root_id=roots.value AND b.due>$now) OR w.due=$requested) " +
            "AND w.state='pending' AND w.due<=$now AND (w.job_id>$after OR w.due=$requested) " +
            "ORDER BY CASE WHEN w.due=$requested THEN 0 ELSE 1 END,w.job_id LIMIT $limit;");
        StorageRootParameters(command, activeRoots);
        command.Parameters.AddWithValue("$after", afterJobId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 512));
        var result = new List<StorageSettlement>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            JsonSerializer.Deserialize<PilotRemoteEntry>(reader.GetString(4))!));
        return (IReadOnlyList<StorageSettlement>)result;
    });

    internal bool IsCurrentStorageSettlement(StorageSettlement job) => InStore(() =>
        _storageEnabled && StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM storage_settlement WHERE job_id=$id AND remote_json=$j AND state='pending';",
            ("$id", job.JobId), ("$j", JsonSerializer.Serialize(job.Remote))) is not null);

    public bool CompleteStorageSettlement(StorageSettlement job, StorageObserved observed) => InStore(() =>
    {
        if (!_storageEnabled) return false;
        using var tx = _connection.BeginTransaction();
        // CAS protects a newer upload receipt arriving while native conversion was running.
        var json = JsonSerializer.Serialize(job.Remote);
        var current = StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM storage_settlement WHERE job_id=$id AND remote_json=$j AND state='pending';",
            ("$id", job.JobId), ("$j", json));
        if (current is null) return false;
        WriteStorageObservation(tx, job.RootId, job.ObjectId, job.RemoteBinding, job.Remote, observed);
        StoreSchema.Execute(_connection, tx, "DELETE FROM storage_settlement WHERE job_id=$id;", ("$id", job.JobId));
        tx.Commit();
        return true;
    });
}
