using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal const string HasStorageWorkSql = """
        SELECT EXISTS(SELECT 1 FROM storage_policy WHERE mode='online-only')
          OR EXISTS(SELECT 1 FROM storage_settlement WHERE state='pending')
          OR EXISTS(SELECT 1 FROM storage_refresh WHERE due<=$now)
          OR EXISTS(SELECT 1 FROM storage_policy_sweep)
          OR EXISTS(SELECT 1 FROM storage_path_intent WHERE next_attempt<=$now)
          OR EXISTS(SELECT 1 FROM storage_cleanup);
        """;
    public bool HasOnlineStoragePolicy(string rootId) => InStore(() => StoreSchema.Scalar(_connection, null,
        "SELECT 1 FROM storage_policy WHERE root_id=$r AND mode='online-only' LIMIT 1;", ("$r", rootId)) is not null);

    public bool HasStorageWork() => InStore(() => Convert.ToInt64(StoreSchema.Scalar(_connection, null,
        HasStorageWorkSql, ("$now", NowMs()))) != 0);

    public bool HasUnresolvedStorageIntent(RootDef root, string path) => InStore(() =>
        StoreSchema.Scalar(_connection, null, "SELECT 1 FROM storage_path_intent WHERE root_id=$r AND path_key=$k;",
            ("$r", root.RootId), ("$k", RowCodec.Key(path.Replace('\\', '/').Trim('/')))) is not null);

    // Bind only uniquely indexed paths. A persisted intent is removed once it belongs to an identity.
    private void BindStoragePathIntents(string? rootId = null)
    {
        var pending = new List<(string Root, string Path, string Mode)>();
        using (var cmd = StoreSchema.Command(_connection, null, "SELECT root_id,path,mode FROM storage_path_intent WHERE next_attempt<=$now " +
            (rootId is null ? "" : "AND root_id=$r ") + "ORDER BY next_attempt,root_id,path_key LIMIT 128;"))
        {
            cmd.Parameters.AddWithValue("$now", NowMs());
            if (rootId is not null) cmd.Parameters.AddWithValue("$r", rootId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) pending.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        foreach (var intent in pending)
        {
            StoreSchema.Execute(_connection, null, "UPDATE storage_path_intent SET next_attempt=$next WHERE root_id=$r AND path_key=$k;",
                ("$next", NowMs() + 15000), ("$r", intent.Root), ("$k", RowCodec.Key(intent.Path)));
            var root = RootNo(intent.Root);
            if (root is null) continue;
            var rows = _reads.AtKey(root.Value, intent.Path, null);
            if (rows.Count != 1) continue;
            using var tx = _connection.BeginTransaction();
            if (StoreSchema.Scalar(_connection, tx, "SELECT mode FROM storage_path_intent WHERE root_id=$r AND path_key=$k;",
                ("$r", intent.Root), ("$k", RowCodec.Key(intent.Path))) as string != intent.Mode) continue;
            WriteStoragePolicy(tx, intent.Root, rows[0].Item.ObjectId, intent.Mode);
            StoreSchema.Execute(_connection, tx, "DELETE FROM storage_path_intent WHERE root_id=$r AND path_key=$k;",
                ("$r", intent.Root), ("$k", RowCodec.Key(intent.Path)));
            tx.Commit();
        }
    }

    // Only the delete-trigger workset is examined, never the whole storage inventory. Called after
    // publication, not between delete/insert steps of a rename, so identity-owned intent survives moves.
    private void CleanupStorageItems()
    {
        var items = new List<(string Root, string Object)>();
        using (var cmd = StoreSchema.Command(_connection, null, "SELECT root_id,object_id FROM storage_cleanup LIMIT 512;"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) items.Add((reader.GetString(0), reader.GetString(1)));
        if (items.Count == 0) return;
        using var tx = _connection.BeginTransaction();
        foreach (var item in items)
        {
            var present = StoreSchema.Scalar(_connection, tx,
                "SELECT 1 FROM root r JOIN file_state f INDEXED BY fs_obj ON f.root_no=r.root_no WHERE r.root_id=$r AND f.object_id=$o LIMIT 1;",
                ("$r", item.Root), ("$o", item.Object)) is not null;
            if (!present) RemoveStorageEntity(tx, item.Root, item.Object);
            StoreSchema.Execute(_connection, tx, "DELETE FROM storage_cleanup WHERE root_id=$r AND object_id=$o;",
                ("$r", item.Root), ("$o", item.Object));
        }
        tx.Commit();
    }

    private void RemoveStorageEntity(SqliteTransaction tx, string rootId, string objectId)
    {
        if (Convert.ToInt64(StoreSchema.Scalar(_connection, tx,
            "SELECT EXISTS(SELECT 1 FROM storage_policy WHERE root_id=$r AND object_id=$o) OR " +
            "EXISTS(SELECT 1 FROM storage_item WHERE root_id=$r AND object_id=$o) OR " +
            "EXISTS(SELECT 1 FROM storage_settlement WHERE root_id=$r AND object_id=$o);", ("$r", rootId), ("$o", objectId))) == 0) return;
        var bindings = new List<(long Pair, string Remote)>();
        using (var cmd = StoreSchema.Command(_connection, tx, """
            SELECT pair_no,CASE WHEN json_extract(def_json,'$.A.RootId')=$r THEN json_extract(def_json,'$.B.RootId')
              ELSE json_extract(def_json,'$.A.RootId') END FROM pair
            WHERE json_extract(def_json,'$.A.RootId')=$r OR json_extract(def_json,'$.B.RootId')=$r;
            """))
        {
            cmd.Parameters.AddWithValue("$r", rootId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) bindings.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        foreach (var binding in bindings)
            foreach (var operation in new[] { "settlement", "remote-update", "hydration" })
            {
                var prefix = StorageNotices.Prefix(new(rootId, objectId, binding.Remote, operation));
                StoreSchema.Execute(_connection, tx,
                    "UPDATE notice SET state='closed',choice='resolved' WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi;",
                    ("$p", binding.Pair), ("$lo", prefix), ("$hi", prefix + "￿"));
            }
        foreach (var table in new[] { "storage_policy", "storage_item", "storage_settlement", "storage_refresh" })
            StoreSchema.Execute(_connection, tx, $"DELETE FROM {table} WHERE root_id=$r AND object_id=$o;",
                ("$r", rootId), ("$o", objectId));
    }

    /// <summary>Visits at most limit baseline keys; durable cursor resumes after restart. Returns keys visited.</summary>
    public int EnqueueStoragePolicyPage(int limit = 128) => InStore(() =>
    {
        CleanupStorageItems();
        BindStoragePathIntents();
        string rootId; long afterPair; string afterKey; long revision;
        using (var cmd = StoreSchema.Command(_connection, null, "SELECT root_id,after_pair,after_key,revision FROM storage_policy_sweep ORDER BY root_id LIMIT 1;"))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return 0;
            rootId = reader.GetString(0); afterPair = reader.GetInt64(1); afterKey = reader.GetString(2); revision = reader.GetInt64(3);
        }
        var pairRows = new List<(long No, PairDef Pair)>();
        using (var cmd = StoreSchema.Command(_connection, null, "SELECT pair_no,def_json FROM pair WHERE pair_no>=$after ORDER BY pair_no;"))
        {
            cmd.Parameters.AddWithValue("$after", afterPair);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var pair = JsonSerializer.Deserialize<PairDef>(reader.GetString(1), RowCodec.Json)!;
                if ((pair.A.RootId == rootId && pair.A.Provider == SyncProviders.Local && pair.B.Provider == SyncProviders.Dropbox) ||
                    (pair.B.RootId == rootId && pair.B.Provider == SyncProviders.Local && pair.A.Provider == SyncProviders.Dropbox)) pairRows.Add((reader.GetInt64(0), pair));
            }
        }
        foreach (var entry in pairRows)
        {
            var keys = new List<string>();
            using (var cmd = StoreSchema.Command(_connection, null,
                "SELECT object_key FROM sync_point WHERE pair_no=$p AND object_key>$after ORDER BY object_key LIMIT $limit;"))
            {
                cmd.Parameters.AddWithValue("$p", entry.No);
                cmd.Parameters.AddWithValue("$after", entry.No == afterPair ? afterKey : "");
                cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 512));
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) keys.Add(reader.GetString(0));
            }
            if (keys.Count == 0) continue;
            var localRoot = entry.Pair.A.RootId == rootId ? entry.Pair.A : entry.Pair.B;
            var remoteRoot = entry.Pair.A.RootId == rootId ? entry.Pair.B : entry.Pair.A;
            foreach (var baseline in _points.Read(entry.Pair.PairId, entry.No, keys))
            {
                var localBasis = entry.Pair.A.RootId == rootId ? baseline.A : baseline.B;
                var remoteBasis = entry.Pair.A.RootId == rootId ? baseline.B : baseline.A;
                var local = ByObject(rootId, localBasis.ObjectId);
                var remote = ByObject(remoteRoot.RootId, remoteBasis.ObjectId);
                if (local is not { Kind: ItemKind.File, Hash: not null, Flags: ItemFlags.None } ||
                    remote is not { Kind: ItemKind.File, Hash: not null, VersionId: not null } ||
                    local.Hash != localBasis.Hash || local.Size != localBasis.Size || local.Mtime != localBasis.Mtime || local.Path != localBasis.Path ||
                    remote.Hash != remoteBasis.Hash || remote.Size != remoteBasis.Size || remote.VersionId != remoteBasis.VersionId || remote.Path != remoteBasis.Path ||
                    local.Size != remote.Size || !string.Equals(local.Hash, remote.Hash, StringComparison.OrdinalIgnoreCase) ||
                    ResolveStoragePolicy(localRoot, local.Path) != "online-only") continue;
                QueueStorageSettlement(rootId, local.ObjectId, remoteRoot.RootId,
                    new PilotRemoteEntry(local.Path, PilotEntryKind.File, remote.ObjectId, remote.VersionId, remote.Size, remote.Hash, local.Mtime));
            }
            StoreSchema.Execute(_connection, null, "UPDATE storage_policy_sweep SET after_pair=$p,after_key=$k WHERE root_id=$r AND revision=$v;",
                ("$p", entry.No), ("$k", keys[^1]), ("$r", rootId), ("$v", revision));
            return keys.Count;
        }
        StoreSchema.Execute(_connection, null, "DELETE FROM storage_policy_sweep WHERE root_id=$r AND revision=$v;", ("$r", rootId), ("$v", revision));
        return 0;
    });

    private static IReadOnlyList<StorageSettlement> ReadStorageJobs(SqliteCommand command)
    {
        var result = new List<StorageSettlement>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            JsonSerializer.Deserialize<PilotRemoteEntry>(reader.GetString(4))!, reader.FieldCount > 5 ? reader.GetInt64(5) : 0));
        return result;
    }

    public StorageSettlement? StorageSettlementFor(string root, string item, string binding) => InStore(() =>
    {
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT job_id,root_id,object_id,remote_binding,remote_json FROM storage_settlement WHERE root_id=$r AND object_id=$o AND remote_binding=$b;");
        cmd.Parameters.AddWithValue("$r", root); cmd.Parameters.AddWithValue("$o", item); cmd.Parameters.AddWithValue("$b", binding);
        return ReadStorageJobs(cmd).FirstOrDefault();
    });

    public bool RetireStorageSettlement(StorageSettlement job) => InStore(() =>
    {
        StoreSchema.Execute(_connection, null, "DELETE FROM storage_settlement WHERE job_id=$id AND remote_json=$j;",
            ("$id", job.JobId), ("$j", JsonSerializer.Serialize(job.Remote)));
        return StoreSchema.Changes(_connection, null) != 0;
    });

    internal bool StorageCauseIgnored(string pairId, string reasonKey) => InStore(() =>
        StoreSchema.Scalar(_connection, null, "SELECT 1 FROM notice WHERE pair_no=$p AND reason_key=$k AND choice='ignore' LIMIT 1;",
            ("$p", RequirePair(pairId)), ("$k", reasonKey)) is not null);

    internal bool StorageOperationIgnored(string pairId, StorageNotice item) => InStore(() =>
        StoreSchema.Scalar(_connection, null, "SELECT 1 FROM notice WHERE pair_no=$p AND reason_key >= $lo AND reason_key < $hi AND choice='ignore' LIMIT 1;",
            ("$p", RequirePair(pairId)), ("$lo", StorageNotices.Prefix(item)), ("$hi", StorageNotices.Prefix(item) + "￿")) is not null);

    internal void RetryStorageSettlement(string root, string item, string binding) => InStore(() =>
        StoreSchema.Execute(_connection, null, "UPDATE storage_settlement SET state='pending' WHERE root_id=$r AND object_id=$o AND remote_binding=$b;",
            ("$r", root), ("$o", item), ("$b", binding)));

    internal void IgnoreStorageSettlement(string root, string item, string binding, string? scope = null) => InStore(() =>
    {
        var job = StorageSettlementFor(root, item, binding);
        if (job is null || scope is null || StorageNotices.ReceiptScope(job.Remote) != scope) return;
        StoreSchema.Execute(_connection, null, "UPDATE storage_settlement SET state='ignored' WHERE job_id=$id AND remote_json=$j;",
            ("$id", job.JobId), ("$j", JsonSerializer.Serialize(job.Remote)));
    });

    public StorageSettlement? StoragePointerFor(string root, string item, string binding) => InStore(() =>
    {
        using var cmd = StoreSchema.Command(_connection, null, "SELECT COALESCE(q.job_id,0),s.root_id,s.object_id,s.remote_binding,s.remote_json,COALESCE(q.due,0) " +
            "FROM storage_item s LEFT JOIN storage_refresh q ON q.root_id=s.root_id AND q.object_id=s.object_id AND q.remote_binding=s.remote_binding " +
            "WHERE s.root_id=$r AND s.object_id=$o AND s.remote_binding=$b;");
        cmd.Parameters.AddWithValue("$r", root); cmd.Parameters.AddWithValue("$o", item); cmd.Parameters.AddWithValue("$b", binding);
        return ReadStorageJobs(cmd).FirstOrDefault();
    });

    public static string StoragePointerCursor(StorageSettlement job) => JsonSerializer.Serialize(new[] {
        job.RefreshDue.ToString(System.Globalization.CultureInfo.InvariantCulture), job.RootId, job.ObjectId, job.RemoteBinding });

    internal IReadOnlyList<StorageSettlement> ChangedStoragePointers(string after = "", int limit = 128) => InStore(() =>
    {
        var cursor = after.Length == 0 ? new[] { "-1", "", "", "" } : JsonSerializer.Deserialize<string[]>(after)!;
        if (cursor.Length == 3) cursor = ["-1", .. cursor];
        if (cursor.Length != 4) throw new ArgumentException("Invalid pointer cursor.", nameof(after));
        using var cmd = StoreSchema.Command(_connection, null, """
            SELECT q.job_id,s.root_id,s.object_id,s.remote_binding,s.remote_json,q.due FROM storage_refresh q INDEXED BY storage_refresh_due
            JOIN storage_item s ON s.root_id=q.root_id AND s.object_id=q.object_id AND s.remote_binding=q.remote_binding
            WHERE q.due<=$now AND (q.due,q.root_id,q.object_id,q.remote_binding)>($after,$r,$o,$b)
            ORDER BY q.due,q.root_id,q.object_id,q.remote_binding LIMIT $limit;
            """);
        cmd.Parameters.AddWithValue("$now", NowMs()); cmd.Parameters.AddWithValue("$after", long.Parse(cursor[0], System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$r", cursor[1]); cmd.Parameters.AddWithValue("$o", cursor[2]); cmd.Parameters.AddWithValue("$b", cursor[3]);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 128));
        return ReadStorageJobs(cmd);
    });

    internal void CompleteStoragePointerUpdate(StorageSettlement prior, PilotRemoteEntry remote) => InStore(() =>
    {
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, "UPDATE storage_item SET remote_json=$new WHERE root_id=$r AND object_id=$o AND remote_binding=$b AND remote_json=$old;",
            ("$new", JsonSerializer.Serialize(remote)), ("$r", prior.RootId), ("$o", prior.ObjectId),
            ("$b", prior.RemoteBinding), ("$old", JsonSerializer.Serialize(prior.Remote)));
        StoreSchema.Execute(_connection, tx, "UPDATE storage_refresh SET due=$due WHERE root_id=$r AND object_id=$o AND remote_binding=$b AND job_id=$v;",
            ("$due", NowMs() + (long)TimeSpan.FromDays(1).TotalMilliseconds), ("$r", prior.RootId),
            ("$o", prior.ObjectId), ("$b", prior.RemoteBinding), ("$v", prior.JobId));
        tx.Commit();
    });

    public void RetireStoragePointer(StorageSettlement job, StorageObserved? observed = null) => InStore(() =>
    {
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, "DELETE FROM storage_refresh WHERE root_id=$r AND object_id=$o AND remote_binding=$b AND job_id=$v;",
            ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding), ("$v", job.JobId));
        if (observed is not null)
            StoreSchema.Execute(_connection, tx, "UPDATE storage_item SET availability=$a,pinned=$p,in_sync=$s WHERE root_id=$r AND object_id=$o AND remote_binding=$b AND remote_json=$j;",
                ("$a", observed.Availability), ("$p", observed.Pinned ? 1 : 0), ("$s", observed.InSync ? 1 : 0),
                ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding), ("$j", JsonSerializer.Serialize(job.Remote)));
        tx.Commit();
    });

    private void WriteStorageObservation(SqliteTransaction tx, string root, string item, string binding,
        PilotRemoteEntry remote, StorageObserved observed)
    {
        StoreSchema.Execute(_connection, tx, """
            INSERT INTO storage_item(root_id,object_id,remote_binding,remote_json,availability,pinned,in_sync)
            VALUES($r,$o,$b,$j,$a,$p,$s) ON CONFLICT(root_id,object_id,remote_binding)
            DO UPDATE SET remote_json=excluded.remote_json,availability=excluded.availability,pinned=excluded.pinned,in_sync=excluded.in_sync;
            """, ("$r", root), ("$o", item), ("$b", binding), ("$j", JsonSerializer.Serialize(remote)),
            ("$a", observed.Availability), ("$p", observed.Pinned ? 1 : 0), ("$s", observed.InSync ? 1 : 0));
        StoreSchema.Execute(_connection, tx, """
            INSERT INTO storage_refresh(root_id,object_id,remote_binding,due) VALUES($r,$o,$b,$due)
            ON CONFLICT(root_id,object_id,remote_binding) DO UPDATE SET due=excluded.due,job_id=excluded.job_id;
            """, ("$r", root), ("$o", item), ("$b", binding), ("$due", NowMs() + (long)TimeSpan.FromDays(1).TotalMilliseconds));
    }

    public IReadOnlyList<Notice> HydrationNoticesPage(string pairId, int limit = 16385) => InStore(() =>
    {
        using var cmd = StoreSchema.Command(_connection, null, """
            SELECT notice_id,rule,kind,path,detail,reason_key FROM notice INDEXED BY nt_pair_reason
            WHERE pair_no=$p AND reason_key>='storage:hydration:' AND reason_key<'storage:hydration;'
              AND state='open' ORDER BY reason_key LIMIT $limit;
            """);
        cmd.Parameters.AddWithValue("$p", RequirePair(pairId));
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 16385));
        var result = new List<Notice>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(NoticeDetailCodec.Decode(new Notice(pairId, reader.GetInt32(1),
            Enum.Parse<NoticeKind>(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(0))));
        return (IReadOnlyList<Notice>)result;
    });

    public void CloseStorageNoticeBatch(string pairId, IReadOnlyList<StorageNotice> items) => InStore(() =>
    {
        if (items.Count > 512) throw new ArgumentOutOfRangeException(nameof(items), "Use bounded pages of 512 items.");
        var pairNo = RequirePair(pairId);
        using var tx = _connection.BeginTransaction();
        foreach (var item in items)
        {
            var prefix = StorageNotices.Prefix(item with { Scope = null });
            StoreSchema.Execute(_connection, tx,
                "UPDATE notice SET state='closed',choice='resolved' WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND state='open';",
                ("$p", pairNo), ("$lo", prefix), ("$hi", prefix + "￿"));
        }
        tx.Commit();
    });

    public void RecordStorageRegistration(string rootId, string path, string registrationId) => InStore(() =>
        StoreSchema.Execute(_connection, null, "INSERT INTO storage_registration(root_id,path,registration_id) VALUES($r,$p,$i) " +
            "ON CONFLICT(root_id) DO UPDATE SET path=excluded.path,registration_id=excluded.registration_id;",
            ("$r", rootId), ("$p", Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)), ("$i", registrationId)));

    public bool OwnsStorageRegistration(string rootId, string path, string registrationId) => InStore(() =>
        StoreSchema.Scalar(_connection, null, "SELECT path FROM storage_registration WHERE root_id=$r AND registration_id=$i;",
            ("$r", rootId), ("$i", registrationId)) is string saved &&
        string.Equals(saved, Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<StorageRegistration> StorageRegistrations() => InStore(() =>
    {
        using var cmd = StoreSchema.Command(_connection, null, "SELECT root_id,path,registration_id FROM storage_registration;");
        using var reader = cmd.ExecuteReader();
        var result = new List<StorageRegistration>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return (IReadOnlyList<StorageRegistration>)result;
    });
}

public sealed record StorageRegistration(string RootId, string Path, string RegistrationId);
