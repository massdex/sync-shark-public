using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    IReadOnlyList<StorageRegistration> IStateStore.StorageRegistrations() => StorageRegistrations();
    StorageSettlement? IStateStore.StoragePointerFor(string root, string item, string binding) => StoragePointerFor(root, item, binding);
    StorageSettlement? IStateStore.StorageSettlementFor(string root, string item, string binding) => StorageSettlementFor(root, item, binding);
    IReadOnlyList<StorageSettlement> IStateStore.StorageSettlementPage(long afterJobId, int limit,
        IReadOnlyCollection<string>? activeRoots) => StorageSettlementPage(afterJobId, limit, activeRoots);
    bool IStateStore.IsCurrentStorageSettlement(StorageSettlement job) => IsCurrentStorageSettlement(job);
    bool IStateStore.CompleteStorageSettlement(StorageSettlement job, StorageObserved observed) => CompleteStorageSettlement(job, observed);

    void IStateStore.CloseStorageNoticeBatch(string pairId, IReadOnlyList<StorageNotice> items) =>
        CloseStorageNoticeBatch(pairId, items);

    internal const string HasStorageWorkSql = """
        SELECT EXISTS(SELECT 1 FROM storage_policy WHERE mode='online-only')
          OR EXISTS(SELECT 1 FROM storage_settlement WHERE state='pending' AND due<=$now)
          OR EXISTS(SELECT 1 FROM storage_refresh WHERE due<=$now)
          OR EXISTS(SELECT 1 FROM storage_subtree_sweep)
          OR EXISTS(SELECT 1 FROM storage_hydration WHERE due<=$now)
          OR EXISTS(SELECT 1 FROM storage_path_intent WHERE next_attempt<=$now)
          OR EXISTS(SELECT 1 FROM storage_cleanup);
        """;
    public bool HasOnlineStoragePolicy(string rootId) => InStore(() => StorageEnabled() && StoreSchema.Scalar(_connection, null,
        "SELECT 1 FROM storage_policy WHERE root_id=$r AND mode='online-only' LIMIT 1;", ("$r", rootId)) is not null);

    public bool HasStorageWork() => InStore(() => StorageEnabled() && Convert.ToInt64(StoreSchema.Scalar(_connection, null,
        HasStorageWorkSql, ("$now", NowMs()))) != 0);

    public bool HasUnresolvedStorageIntent(RootDef root, string path) => InStore(() =>
        _storageEnabled && StoreSchema.Scalar(_connection, null, "SELECT 1 FROM storage_path_intent WHERE root_id=$r AND path_key=$k;",
            ("$r", root.RootId), ("$k", RowCodec.Key(path.Replace('\\', '/').Trim('/')))) is not null);

    // Bind only uniquely indexed paths. A persisted intent is removed once it belongs to an identity.
    private void BindStoragePathIntents(string? rootId = null)
    {
        if (!_storageEnabled) return;
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
        if (!_storageEnabled) return;
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
        if (!_storageEnabled) return;
        if (Convert.ToInt64(StoreSchema.Scalar(_connection, tx,
            "SELECT EXISTS(SELECT 1 FROM storage_policy WHERE root_id=$r AND object_id=$o) OR " +
            "EXISTS(SELECT 1 FROM storage_item WHERE root_id=$r AND object_id=$o) OR " +
            "EXISTS(SELECT 1 FROM storage_settlement WHERE root_id=$r AND object_id=$o) OR EXISTS(SELECT 1 FROM storage_hydration WHERE root_id=$r AND object_id=$o);", ("$r", rootId), ("$o", objectId))) == 0) return;
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
                _notices.CloseWhere(tx, "resolved",
                    "pair_no=$p AND reason_key>=$lo AND reason_key<$hi;",
                    ("$p", binding.Pair), ("$lo", prefix), ("$hi", prefix + "￿"));
            }
        foreach (var table in new[] { "storage_policy", "storage_item", "storage_settlement", "storage_refresh", "storage_hydration", "storage_subtree_sweep" })
            StoreSchema.Execute(_connection, tx, $"DELETE FROM {table} WHERE root_id=$r AND object_id=$o;",
                ("$r", rootId), ("$o", objectId));
    }

    private static IReadOnlyList<StorageSettlement> ReadStorageJobs(SqliteCommand command)
    {
        var result = new List<StorageSettlement>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            JsonSerializer.Deserialize<PilotRemoteEntry>(reader.GetString(4))!, reader.FieldCount > 5 ? reader.GetInt64(5) : 0,
                reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetString(6) : null));
        return result;
    }

    public StorageSettlement? StorageSettlementFor(string root, string item, string binding) => InStore(() =>
    {
        if (!_storageEnabled) return null;
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT job_id,root_id,object_id,remote_binding,remote_json,0,kind FROM storage_settlement WHERE root_id=$r AND object_id=$o AND remote_binding=$b;");
        cmd.Parameters.AddWithValue("$r", root); cmd.Parameters.AddWithValue("$o", item); cmd.Parameters.AddWithValue("$b", binding);
        return ReadStorageJobs(cmd).FirstOrDefault();
    });

    public bool RetireStorageSettlement(StorageSettlement job) => InStore(() =>
    {
        if (!_storageEnabled) return false;
        StoreSchema.Execute(_connection, null, "DELETE FROM storage_settlement WHERE job_id=$id AND remote_json=$j;",
            ("$id", job.JobId), ("$j", JsonSerializer.Serialize(job.Remote)));
        return StoreSchema.Changes(_connection, null) != 0;
    });

    internal bool StorageCauseIgnored(string pairId, string reasonKey) => InStore(() =>
        StoreSchema.Scalar(_connection, null, "SELECT 1 FROM notice WHERE pair_no=$p AND reason_key=$k AND choice='ignore' AND " + NoticeTable.ActionableSql("state") + " LIMIT 1;",
            ("$p", RequirePair(pairId)), ("$k", reasonKey)) is not null);

    // Persist one explicit Retry without running it. Only an existing queued row is marked, so a completed
    // entity is never recreated; the negative due marker survives pause and is consumed by the claim below.
    internal bool RequestStorageSettlement(StorageSettlement job) => InStore(() =>
    {
        if (!_storageEnabled) return false;
        StoreSchema.Execute(_connection, null,
            "UPDATE storage_settlement SET state='pending',due=$requested WHERE job_id=$id AND root_id=$r AND object_id=$o " +
            "AND remote_binding=$b AND remote_json=$j;",
            ("$requested", StorageDue.UserRequested), ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId),
            ("$b", job.RemoteBinding), ("$j", JsonSerializer.Serialize(job.Remote)));
        return StoreSchema.Changes(_connection, null) != 0;
    });

    internal bool IsStorageSettlementRequested(StorageSettlement job) => InStore(() => _storageEnabled &&
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM storage_settlement WHERE job_id=$id AND root_id=$r AND object_id=$o AND remote_binding=$b AND remote_json=$j AND due=$requested;",
            ("$id", job.JobId), ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding),
            ("$j", JsonSerializer.Serialize(job.Remote)), ("$requested", StorageDue.UserRequested)) is not null);

    // One atomic claim. The requested pass is exempt from root backoff; the normal pass is not. A claimed
    // row parks at a future lease so a second claim attempt fails. A concurrent Request can overwrite that
    // lease, so coalescing is guaranteed inside one store/entity gate; cross-process is best-effort only.
    internal StorageClaim TryClaimStorageSettlement(StorageSettlement job) => InStore(() =>
    {
        if (!_storageEnabled) return StorageClaim.None;
        var now = NowMs();
        StoreSchema.Execute(_connection, null,
            "UPDATE storage_settlement SET due=$lease WHERE job_id=$id AND root_id=$r AND object_id=$o AND remote_binding=$b " +
            "AND remote_json=$j AND state='pending' AND due=$requested;",
            ("$lease", now + StorageDue.ClaimLeaseMs), ("$requested", StorageDue.UserRequested), ("$id", job.JobId),
            ("$r", job.RootId), ("$o", job.ObjectId), ("$b", job.RemoteBinding), ("$j", JsonSerializer.Serialize(job.Remote)));
        if (StoreSchema.Changes(_connection, null) != 0) return StorageClaim.Requested;
        StoreSchema.Execute(_connection, null,
            "UPDATE storage_settlement SET due=$lease WHERE job_id=$id AND root_id=$r AND object_id=$o AND remote_binding=$b " +
            "AND remote_json=$j AND state='pending' AND due<=$now " +
            "AND NOT EXISTS(SELECT 1 FROM storage_root_backoff b WHERE b.root_id=$r AND b.due>$now);",
            ("$lease", now + StorageDue.ClaimLeaseMs), ("$now", now), ("$id", job.JobId), ("$r", job.RootId),
            ("$o", job.ObjectId), ("$b", job.RemoteBinding), ("$j", JsonSerializer.Serialize(job.Remote)));
        return StoreSchema.Changes(_connection, null) != 0 ? StorageClaim.Normal : StorageClaim.None;
    });

    public StorageSettlement? StoragePointerFor(string root, string item, string binding) => InStore(() =>
    {
        if (!_storageEnabled) return null;
        using var cmd = StoreSchema.Command(_connection, null, "SELECT COALESCE(q.job_id,0),s.root_id,s.object_id,s.remote_binding,s.remote_json,COALESCE(q.due,0) " +
            "FROM storage_item s LEFT JOIN storage_refresh q ON q.root_id=s.root_id AND q.object_id=s.object_id AND q.remote_binding=s.remote_binding " +
            "WHERE s.root_id=$r AND s.object_id=$o AND s.remote_binding=$b;");
        cmd.Parameters.AddWithValue("$r", root); cmd.Parameters.AddWithValue("$o", item); cmd.Parameters.AddWithValue("$b", binding);
        return ReadStorageJobs(cmd).FirstOrDefault();
    });

    public static string StoragePointerCursor(StorageSettlement job) => JsonSerializer.Serialize(new[] {
        job.RefreshDue.ToString(System.Globalization.CultureInfo.InvariantCulture), job.RootId, job.ObjectId, job.RemoteBinding });

    internal const string StoragePointerRequestedSql =
        "SELECT q.job_id,s.root_id,s.object_id,s.remote_binding,s.remote_json,q.due FROM " + StorageRootsSql +
        " CROSS JOIN storage_refresh q ON q.job_id IN (SELECT rq.job_id FROM storage_refresh rq INDEXED BY storage_refresh_requested " +
        "WHERE rq.root_id=roots.value AND rq.due=-1 ORDER BY rq.object_id,rq.remote_binding LIMIT $limit) " +
        "JOIN storage_item s ON s.root_id=q.root_id AND s.object_id=q.object_id AND s.remote_binding=q.remote_binding " +
        "ORDER BY q.root_id,q.object_id,q.remote_binding LIMIT $limit;";

    internal const string StoragePointerRangeSql =
        "SELECT q.job_id,s.root_id,s.object_id,s.remote_binding,s.remote_json,q.due FROM " + StorageRootsSql +
        " CROSS JOIN storage_refresh q ON q.job_id IN (SELECT rq.job_id FROM storage_refresh rq INDEXED BY storage_refresh_root " +
        "WHERE rq.root_id=roots.value AND rq.due<=$now AND (rq.due,rq.root_id,rq.object_id,rq.remote_binding)>($after,$r,$o,$b) " +
        "ORDER BY rq.due,rq.object_id,rq.remote_binding LIMIT $limit) " +
        "JOIN storage_item s ON s.root_id=q.root_id AND s.object_id=q.object_id AND s.remote_binding=q.remote_binding " +
        StorageUnblockedSql + " AND q.due<=$now AND (q.due,q.root_id,q.object_id,q.remote_binding)>($after,$r,$o,$b) " +
        "ORDER BY q.due,q.root_id,q.object_id,q.remote_binding LIMIT $limit;";

    internal IReadOnlyList<StorageSettlement> ChangedStoragePointers(string after = "", int limit = 128, IReadOnlyCollection<string>? activeRoots = null) => InStore(() =>
    {
        if (!_storageEnabled) return (IReadOnlyList<StorageSettlement>)[];
        var start = StorageDue.UserRequested.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var cursor = after.Length == 0 ? new[] { start, "", "", "" } : JsonSerializer.Deserialize<string[]>(after)!;
        if (cursor.Length == 3) cursor = [start, .. cursor];
        if (cursor.Length != 4) throw new ArgumentException("Invalid pointer cursor.", nameof(after));
        var pageSize = Math.Clamp(limit, 1, 128);
        using var cmd = StoreSchema.Command(_connection, null, StoragePointerRequestedSql);
        StorageRootParameters(cmd, activeRoots);
        cmd.Parameters.AddWithValue("$after", long.Parse(cursor[0], System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$r", cursor[1]); cmd.Parameters.AddWithValue("$o", cursor[2]); cmd.Parameters.AddWithValue("$b", cursor[3]);
        cmd.Parameters.AddWithValue("$limit", Math.Min(pageSize, 8));
        var requested = ReadStorageJobs(cmd);
        if (requested.Count == pageSize) return requested;
        cmd.CommandText = StoragePointerRangeSql;
        cmd.Parameters["$limit"].Value = pageSize;
        return (IReadOnlyList<StorageSettlement>)requested.Concat(ReadStorageJobs(cmd))
            .DistinctBy(job => job.JobId).Take(pageSize).ToArray();
    });

    internal void CompleteStoragePointerUpdate(StorageSettlement prior, PilotRemoteEntry remote) => InStore(() =>
    {
        if (!_storageEnabled) return;
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, "UPDATE storage_item SET remote_json=$new WHERE root_id=$r AND object_id=$o AND remote_binding=$b AND remote_json=$old;",
            ("$new", JsonSerializer.Serialize(remote)), ("$r", prior.RootId), ("$o", prior.ObjectId),
            ("$b", prior.RemoteBinding), ("$old", JsonSerializer.Serialize(prior.Remote)));
        // A stale worker must not consume a newer revision's queued work after losing the receipt CAS.
        if (StoreSchema.Changes(_connection, tx) != 0)
            StoreSchema.Execute(_connection, tx, "DELETE FROM storage_refresh WHERE root_id=$r AND object_id=$o AND remote_binding=$b AND job_id=$v;", ("$r", prior.RootId),
                ("$o", prior.ObjectId), ("$b", prior.RemoteBinding), ("$v", prior.JobId));
        tx.Commit();
    });

    public void RetireStoragePointer(StorageSettlement job, StorageObserved? observed = null) => InStore(() =>
    {
        if (!_storageEnabled) return;
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
        if (!_storageEnabled) return;
        StoreSchema.Execute(_connection, tx, """
            INSERT INTO storage_item(root_id,object_id,remote_binding,remote_json,availability,pinned,in_sync)
            VALUES($r,$o,$b,$j,$a,$p,$s) ON CONFLICT(root_id,object_id,remote_binding)
            DO UPDATE SET remote_json=excluded.remote_json,availability=excluded.availability,pinned=excluded.pinned,in_sync=excluded.in_sync;
            """, ("$r", root), ("$o", item), ("$b", binding), ("$j", JsonSerializer.Serialize(remote)),
            ("$a", observed.Availability), ("$p", observed.Pinned ? 1 : 0), ("$s", observed.InSync ? 1 : 0));
        // A run that published this exact indexed revision has already done the refresh work.
        // Keep a trigger queued for a newer concurrently indexed revision.
        StoreSchema.Execute(_connection, tx, "DELETE FROM storage_refresh WHERE root_id=$r AND object_id=$o AND remote_binding=$b " +
            "AND EXISTS(SELECT 1 FROM file_state f WHERE f.root_no=(SELECT root_no FROM root WHERE root_id=$b) " +
            "AND f.object_id=$id AND f.version_id=$rev);",
            ("$r", root), ("$o", item), ("$b", binding), ("$id", remote.Id), ("$rev", remote.Revision));
    }

    public IReadOnlyList<Notice> HydrationNoticesPage(string pairId, int limit = 16385) => InStore(() =>
    {
        using var cmd = StoreSchema.Command(_connection, null, $"""
            SELECT notice_id,rule,kind,path,detail,reason_key FROM notice INDEXED BY nt_pair_reason
            WHERE pair_no=$p AND reason_key>='storage:hydration:' AND reason_key<'storage:hydration;'
              AND {NoticeTable.ActionableSql("state")}  ORDER BY reason_key LIMIT $limit;
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
            _notices.CloseWhere(tx, "resolved",
                "pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND " + NoticeTable.ActionableSql("state") + ";",
                ("$p", pairNo), ("$lo", prefix), ("$hi", prefix + "￿"));
        }
        tx.Commit();
    });

    public void RecordStorageRegistration(string rootId, string path, string registrationId) => InStore(() =>
    {
        if (!_storageEnabled) return;
        StoreSchema.Execute(_connection, null, "INSERT INTO storage_registration(root_id,path,registration_id) VALUES($r,$p,$i) " +
            "ON CONFLICT(root_id) DO UPDATE SET path=excluded.path,registration_id=excluded.registration_id;",
            ("$r", rootId), ("$p", Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)), ("$i", registrationId));
    });

    public bool OwnsStorageRegistration(string rootId, string path, string registrationId) => InStore(() =>
        _storageEnabled && StoreSchema.Scalar(_connection, null, "SELECT path FROM storage_registration WHERE root_id=$r AND registration_id=$i;",
            ("$r", rootId), ("$i", registrationId)) is string saved &&
        string.Equals(saved, Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<StorageRegistration> StorageRegistrations() => InStore(() =>
    {
        if (!_storageEnabled) return (IReadOnlyList<StorageRegistration>)[];
        using var cmd = StoreSchema.Command(_connection, null, "SELECT root_id,path,registration_id FROM storage_registration;");
        using var reader = cmd.ExecuteReader();
        var result = new List<StorageRegistration>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return (IReadOnlyList<StorageRegistration>)result;
    });
}
