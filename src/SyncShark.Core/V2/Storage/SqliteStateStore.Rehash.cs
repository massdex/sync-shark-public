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

    public long RehashPendingCount(string rootId) => InStore(() =>
        Convert.ToInt64(StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM local_rehash WHERE root_no=(SELECT root_no FROM root WHERE root_id=$id);", ("$id", rootId))));

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
            "SELECT d.path,f.name FROM file_state f INDEXED BY fs_obj JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND f.object_id=$o ORDER BY f.dir_no,f.name_key,f.name LIMIT $n OFFSET $skip;");
        cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$o", objectId);
        cmd.Parameters.AddWithValue("$n", limit); cmd.Parameters.AddWithValue("$skip", offset);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(RowCodec.Join(reader.GetString(0), reader.GetString(1)));
        return result.ToArray();
    });

    public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit) => RehashCandidates(rootId, limit, RehashWatermark(rootId));

    public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit, long through) => RehashCandidates(rootId, limit, through, null);

    private static readonly string VerificationNeededSql = VerificationSql.NeededSql("f");
    private static readonly string VerificationEligibleSql = VerificationSql.EligibleSql("f");

    private const string NotGapOwnedSql = "NOT EXISTS(SELECT 1 FROM local_gap_item g WHERE g.root_no=q.root_no AND g.object_id=q.object_id)";

    public bool HasPendingRehashVerification(string rootId) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM local_rehash q WHERE q.root_no=(SELECT root_no FROM root WHERE root_id=$id) AND " + NotGapOwnedSql + " AND " +
            "EXISTS(SELECT 1 FROM file_state f INDEXED BY fs_obj WHERE f.root_no=q.root_no AND f.object_id=q.object_id AND " +
            VerificationNeededSql + " AND " + VerificationEligibleSql + ") LIMIT 1;", ("$id", rootId)) is not null);

    public void RequireRehash(string rootId, ItemState item, string reason) => RequireRehashBatch(rootId, [item], reason);

    public void RequireRehashBatch(string rootId, IEnumerable<ItemState> items, string reason)
    {
        foreach (var batch in items.DistinctBy(item => item.ObjectId).Chunk(128))
            InIndexingStore(() =>
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
            "UPDATE local_gap_item SET hash=NULL,proof_json=NULL,result_seq=0,metadata_ready=0 WHERE root_no=$r AND object_id=$o;",
            ("$r", root), ("$o", item.ObjectId));
        StoreSchema.Execute(_connection, tx,
            "INSERT INTO local_rehash(root_no,object_id,last_attempt,reason,evidence_revision) VALUES($r,$o,$order,$why,$order) " +
            "ON CONFLICT(root_no,object_id) DO UPDATE SET last_attempt=excluded.last_attempt,reason=excluded.reason,evidence_revision=excluded.evidence_revision;",
            ("$r", root), ("$o", item.ObjectId), ("$why", reason), ("$order", NextRehashOrder(root, tx)));
        StoreSchema.Execute(_connection, tx,
            "UPDATE file_state INDEXED BY fs_obj SET flags=flags | 256,hash=NULL WHERE root_no=$r AND object_id=$o;",
            ("$r", root), ("$o", item.ObjectId));
    }

    internal string RehashRootSql => _storageEnabled
        ? "SELECT 1 FROM file_state INDEXED BY fs_nonpointer WHERE root_no=$r AND (flags & ~1024) != 0 AND (flags & 256)!=0 LIMIT 1;"
        : "SELECT 1 FROM file_state INDEXED BY fs_flagged WHERE root_no=$r AND flags != 0 AND (flags & 256)!=0 LIMIT 1;";

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
                RehashRootSql,
                ("$r", root.Value)) is not null;
        var key = RowCodec.Key(path);
        return StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM file_state f WHERE f.root_no=$r AND (f.flags & 256)!=0 AND " +
            "f.dir_no IN (SELECT dir_no FROM dir WHERE path_key=$k OR (path_key >= $lo AND path_key < $hi)) LIMIT 1;",
            ("$r", root.Value), ("$k", key), ("$lo", key.Length == 0 ? "" : key + "/"),
            ("$hi", key.Length == 0 ? "ï¿¿" : key + "0")) is not null;
    });

    public IReadOnlyList<string> RehashReasons(string rootId, int limit) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || limit <= 0) return Array.Empty<string>();
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT d.path,f.name,q.reason FROM local_rehash q CROSS JOIN file_state f INDEXED BY fs_obj JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE q.root_no=$r AND f.root_no=q.root_no AND f.object_id=q.object_id AND (f.flags & 256)!=0 LIMIT $limit;");
        cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(RowCodec.Join(reader.GetString(0), reader.GetString(1)) + ": " + reader.GetString(2));
        return result.ToArray();
    });

    internal const string AcknowledgeRehashIdentitySql =
        "SELECT d.path,f.name,f.flags,f.hash FROM file_state f INDEXED BY fs_obj JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$r AND f.object_id=$o;";

    public void AcknowledgeRehash(string rootId, long through, Func<string, bool> settled, CancellationToken ct = default, IReadOnlyCollection<string>? objectIds = null)
    {
        using var admission = FairStoreGate.WithCancellation(ct);
        var after = "";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = ReadRehashAcknowledgementPage(rootId, through, after, objectIds);
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
                        RehashAcknowledgementAliasesSql))
                    {
                        cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$o", id);
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            any = true;
                            if (!VerificationPolicy.CanAcknowledge(reader.IsDBNull(3) ? null : "verified", (ItemFlags)reader.GetInt64(2)) ||
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

    public long RehashRevision(string rootId, string objectId) => InStore(() =>
        Convert.ToInt64(StoreSchema.Scalar(_connection, null,
            "SELECT COALESCE((SELECT evidence_revision FROM local_rehash WHERE root_no=(SELECT root_no FROM root WHERE root_id=$r) AND object_id=$o),0);",
            ("$r", rootId), ("$o", objectId))));

    public bool CompleteIndexingRehash(string rootId, ItemState expected, string hash, long revision) => InStore(() =>
    {
        if (revision == 0) return false;
        if (RehashRevision(rootId, expected.ObjectId) == revision) return CompleteRehash(rootId, expected, hash);
        var root = RootNo(rootId);
        if (root is null) return false;
        // CAS mismatch never writes. A retired/rotated queue may already have published the same proof.
        var rows = _reads.AtKey(root.Value, expected.Path, null);
        return rows.Count == 1 && VerificationPolicy.AlreadyVerified(rows[0].Item, expected, hash);
    });

    public bool RetireIndexingRehash(string rootId, ItemState item, long revision) => InIndexingStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || revision == 0 || RehashRevision(rootId, item.ObjectId) != revision) return false;
        using var tx = _connection.BeginTransaction();
        var current = _reads.ByObject(root.Value, item.ObjectId, tx);
        if (current is null || !SameGapItem(current with { Hash = item.Hash, Flags = item.Flags }, item)) return false;
        // No content evidence is written. A later inclusion/observation must verify afresh.
        StoreSchema.Execute(_connection, tx,
            "DELETE FROM local_rehash WHERE root_no=$r AND object_id=$o AND evidence_revision=$v;",
            ("$r", root.Value), ("$o", item.ObjectId), ("$v", revision));
        CloseIndexItemFailure(tx, rootId, item.Path);
        tx.Commit();
        return true;
    });

    public bool CompleteRehash(string rootId, ItemState expected, string hash) => InIndexingStore(() =>
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
            "SELECT 1 FROM file_state INDEXED BY fs_obj WHERE root_no=$r AND object_id=$o AND (size!=$s OR mtime!=$m) LIMIT 1;",
            ("$r", root.Value), ("$o", expected.ObjectId), ("$s", expected.Size),
            ("$m", expected.Mtime.UtcTicks)) is not null) return false;
        StoreSchema.Execute(_connection, tx,
            "UPDATE file_state INDEXED BY fs_obj SET flags=flags & ~256,hash=$h WHERE root_no=$r AND object_id=$o;",
            ("$r", root.Value), ("$o", expected.ObjectId), ("$h", RowCodec.HashBlob(hash)));
        // This exclusive foreground verification also settles the entity's gap work atomically.
        StoreSchema.Execute(_connection, tx, "DELETE FROM local_gap_item WHERE root_no=$r AND object_id=$o;",
            ("$r", root.Value), ("$o", expected.ObjectId));
        // Accepted proof ends this failure episode; the planning obligation still survives.
        StoreSchema.Execute(_connection, tx,
            "UPDATE local_rehash SET failure_count=0,retry_after=0,last_attempt=$order WHERE root_no=$r AND object_id=$o;",
            ("$r", root.Value), ("$o", expected.ObjectId), ("$order", NextRehashOrder(root.Value, tx)));
        // The queue is also the durable planning obligation. Only settled execution acknowledges it.
        CloseIndexItemFailure(tx, rootId, expected.Path);
        tx.Commit();
        return true;
    });
}
