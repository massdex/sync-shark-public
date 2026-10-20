namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private long _rehashOrder;

    // Ack boundaries are process-local. Preserve a high-water mark even when the queue is emptied.
    private long NextRehashOrder(long root, Microsoft.Data.Sqlite.SqliteTransaction tx, long floor = 0)
    {
        var persisted = Convert.ToInt64(StoreSchema.Scalar(_connection, tx,
            "SELECT COALESCE(MAX(last_attempt),0) FROM local_rehash WHERE root_no=$r;", ("$r", root)));
        _rehashOrder = Math.Max(Math.Max(_rehashOrder, persisted), Math.Max(floor, DateTime.UtcNow.Ticks)) + 1;
        return _rehashOrder;
    }

    public long RehashWatermark(string rootId) => InStore(() =>
    {
        var root = RootNo(rootId);
        return root is null ? 0L : Convert.ToInt64(StoreSchema.Scalar(_connection, null,
            "SELECT COALESCE(MAX(last_attempt),0) FROM local_rehash WHERE root_no=$r;", ("$r", root.Value)));
    });

    public IReadOnlyList<string> ObjectPaths(string rootId, string objectId, int offset, int limit) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || limit <= 0) return Array.Empty<string>();
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT d.path,f.name FROM file_state f JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND f.object_id=$o ORDER BY f.dir_no,f.name_key,f.name LIMIT $n OFFSET $skip;");
        cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$o", objectId);
        cmd.Parameters.AddWithValue("$n", limit); cmd.Parameters.AddWithValue("$skip", offset);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(RowCodec.Join(reader.GetString(0), reader.GetString(1)));
        return result.ToArray();
    });

    public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit) => RehashCandidates(rootId, limit, RehashWatermark(rootId));

    public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit, long through) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || limit <= 0) return Array.Empty<ItemState>();
        var ids = new List<string>();
        using (var cmd = StoreSchema.Command(_connection, null,
            "SELECT q.object_id FROM local_rehash q INDEXED BY rehash_retry WHERE q.root_no=$r AND q.last_attempt<=$through AND " +
            "EXISTS(SELECT 1 FROM file_state f WHERE f.root_no=q.root_no AND f.object_id=q.object_id) " +
            "ORDER BY q.last_attempt,q.object_id LIMIT $limit;"))
        {
            cmd.Parameters.AddWithValue("$r", root.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$through", through);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        using (var tx = _connection.BeginTransaction())
        {
            foreach (var id in ids)
                StoreSchema.Execute(_connection, tx,
                    "UPDATE local_rehash SET last_attempt=$order WHERE root_no=$r AND object_id=$o;",
                    ("$r", root.Value), ("$o", id), ("$order", NextRehashOrder(root.Value, tx, through)));
            tx.Commit();
        }
        return ids.Select(id => _reads.ByObject(root.Value, id)!).ToArray();
    });

    public void RequireRehash(string rootId, ItemState item, string reason) => RequireRehashBatch(rootId, [item], reason);

    public void RequireRehashBatch(string rootId, IEnumerable<ItemState> items, string reason)
    {
        foreach (var batch in items.DistinctBy(item => item.ObjectId).Chunk(128))
            InStore(() =>
            {
                var root = RootNo(rootId);
                if (root is null) return;
                using var tx = _connection.BeginTransaction();
                foreach (var item in batch) MarkRehash(root.Value, item, reason, tx);
                tx.Commit();
            });
    }

    private void MarkRehash(long root, ItemState item, string reason, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        // Fresh authoritative writes supersede even metadata-identical staged gap results.
        StoreSchema.Execute(_connection, tx,
            "UPDATE local_gap_item SET hash=NULL,proof_json=NULL,result_seq=0 WHERE root_no=$r AND object_id=$o;",
            ("$r", root), ("$o", item.ObjectId));
        StoreSchema.Execute(_connection, tx,
            "INSERT INTO local_rehash(root_no,object_id,last_attempt,reason) VALUES($r,$o,$order,$why) " +
            "ON CONFLICT(root_no,object_id) DO UPDATE SET last_attempt=excluded.last_attempt,reason=excluded.reason;",
            ("$r", root), ("$o", item.ObjectId), ("$why", reason), ("$order", NextRehashOrder(root, tx)));
        StoreSchema.Execute(_connection, tx,
            "UPDATE file_state SET flags=flags | 256,hash=NULL WHERE root_no=$r AND object_id=$o;",
            ("$r", root), ("$o", item.ObjectId));
    }

    public bool RehashTouches(string rootId, string path, bool descendants) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return false;
        for (var current = path; current.Length > 0;)
        {
            if (_reads.AtKey(root.Value, current, null).Any(row => (row.Item.Flags & ItemFlags.NeedsRehash) != 0)) return true;
            if (descendants) break;
            var slash = current.LastIndexOf('/');
            current = slash < 0 ? "" : current[..slash];
        }
        if (!descendants) return false;
        if (path.Length == 0)
            return StoreSchema.Scalar(_connection, null,
                "SELECT 1 FROM file_state INDEXED BY fs_flagged WHERE root_no=$r AND (flags & ~512) != 0 AND (flags & 256)!=0 LIMIT 1;",
                ("$r", root.Value)) is not null;
        var key = RowCodec.Key(path);
        return StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM file_state f WHERE f.root_no=$r AND (f.flags & 256)!=0 AND " +
            "f.dir_no IN (SELECT dir_no FROM dir WHERE path_key=$k OR (path_key >= $lo AND path_key < $hi)) LIMIT 1;",
            ("$r", root.Value), ("$k", key), ("$lo", key.Length == 0 ? "" : key + "/"),
            ("$hi", key.Length == 0 ? "￿" : key + "0")) is not null;
    });

    public IReadOnlyList<string> RehashReasons(string rootId, int limit) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || limit <= 0) return Array.Empty<string>();
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT d.path,f.name,q.reason FROM local_rehash q CROSS JOIN file_state f JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE q.root_no=$r AND f.root_no=q.root_no AND f.object_id=q.object_id AND (f.flags & 256)!=0 LIMIT $limit;");
        cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(RowCodec.Join(reader.GetString(0), reader.GetString(1)) + ": " + reader.GetString(2));
        return result.ToArray();
    });

    public void AcknowledgeRehash(string rootId, long through, Func<string, bool> settled, CancellationToken ct = default)
    {
        var after = "";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = InStore(() =>
            {
                var root = RootNo(rootId);
                if (root is null) return Array.Empty<string>();
                using var cmd = StoreSchema.Command(_connection, null,
                    "SELECT object_id FROM local_rehash WHERE root_no=$r AND object_id>$after AND last_attempt<=$through ORDER BY object_id LIMIT 128;");
                cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$after", after);
                cmd.Parameters.AddWithValue("$through", through);
                var ids = new List<string>(); using var reader = cmd.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetString(0));
                return ids.ToArray();
            });
            if (page.Length == 0) return;
            InStore(() =>
            {
                var root = RootNo(rootId);
                if (root is null) return;
                using var tx = _connection.BeginTransaction();
                foreach (var id in page)
                {
                    ct.ThrowIfCancellationRequested();
                    var any = false; var all = true;
                    using (var cmd = StoreSchema.Command(_connection, tx,
                        "SELECT d.path,f.name,f.flags,f.hash FROM file_state f JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$r AND f.object_id=$o;"))
                    {
                        cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$o", id);
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            any = true;
                            const ItemFlags blocked = ItemFlags.NeedsRehash | ItemFlags.Locked | ItemFlags.Unreadable |
                                ItemFlags.Ignored | ItemFlags.Link | ItemFlags.Placeholder | (ItemFlags)128;
                            if (((ItemFlags)reader.GetInt64(2) & blocked) != 0 || reader.IsDBNull(3) ||
                                !settled(RowCodec.Join(reader.GetString(0), reader.GetString(1)))) { all = false; break; }
                        }
                    }
                    if (any && all) StoreSchema.Execute(_connection, tx,
                        "DELETE FROM local_rehash WHERE root_no=$r AND object_id=$o AND last_attempt<=$through;",
                        ("$r", root.Value), ("$o", id), ("$through", through));
                }
                tx.Commit();
            });
            after = page[^1]; // Retained/unplanned first pages never starve later settled identities.
        }
    }

    public bool CompleteRehash(string rootId, ItemState expected, string hash) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return false;
        using var tx = _connection.BeginTransaction();
        var exact = _reads.AtKey(root.Value, expected.Path, tx).Any(row =>
            row.Item.Path == expected.Path && row.Item.ObjectId == expected.ObjectId &&
            row.Item.Size == expected.Size && row.Item.Mtime == expected.Mtime);
        if (!exact) return false;
        // A validated handle proves one physical entity. Do not unhold aliases with different metadata.
        if (StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM file_state WHERE root_no=$r AND object_id=$o AND (size!=$s OR mtime!=$m) LIMIT 1;",
            ("$r", root.Value), ("$o", expected.ObjectId), ("$s", expected.Size),
            ("$m", expected.Mtime.UtcTicks)) is not null) return false;
        StoreSchema.Execute(_connection, tx,
            "UPDATE file_state SET flags=flags & ~256,hash=$h WHERE root_no=$r AND object_id=$o;",
            ("$r", root.Value), ("$o", expected.ObjectId), ("$h", RowCodec.HashBlob(hash)));
        // This exclusive foreground verification also settles the entity's gap work atomically.
        StoreSchema.Execute(_connection, tx, "DELETE FROM local_gap_item WHERE root_no=$r AND object_id=$o;",
            ("$r", root.Value), ("$o", expected.ObjectId));
        // The queue is also the durable planning obligation. Only settled execution acknowledges it.
        tx.Commit();
        return true;
    });
}
