using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore : ILocalGapStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Root, bool Discovery), string> _gapStatus = new();

    private readonly Dictionary<long, string> _gapSweepAfter = new();

    private void StageGapOrphans(long root, string epoch)
    {
        var ids = new List<string>();
        using (var cmd = StoreSchema.Command(_connection, null,
            "SELECT object_id FROM local_gap_item WHERE root_no=$r AND object_id>$after ORDER BY object_id LIMIT 128;"))
        {
            cmd.Parameters.AddWithValue("$r", root);
            cmd.Parameters.AddWithValue("$after", _gapSweepAfter.GetValueOrDefault(root, ""));
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        using var tx = _connection.BeginTransaction();
        foreach (var id in ids)
            StoreSchema.Execute(_connection, tx,
                "UPDATE local_gap_item SET epoch=$e,metadata_ready=1,metadata_stamp=NULL,hash=NULL,proof_json=NULL,result_seq=0 " +
                "WHERE root_no=$r AND object_id=$o AND NOT EXISTS(" +
                "SELECT 1 FROM file_state WHERE root_no=$r AND object_id=$o);",
                ("$r", root), ("$o", id), ("$e", epoch));
        tx.Commit();
        _gapSweepAfter[root] = ids.Count < 128 ? "" : ids[^1];
    }

    // Same fs_obj first-alias selection as FileStateReader.ByObject, without JSON date
    // conversion (mtime remains exact ticks). NULL means absence, not an empty observation.
    private static string GapObservationSql(string root, string objectId) =>
        "(SELECT json_array(d.path,f.name,f.kind,f.version_id,CASE WHEN f.hash IS NULL THEN NULL ELSE hex(f.hash) END,f.size,f.mtime,f.flags) " +
        "FROM file_state f INDEXED BY fs_obj JOIN dir d ON d.dir_no=f.dir_no " +
        $"WHERE f.root_no={root} AND f.object_id={objectId} LIMIT 1)";

    void ILocalGapStore.SetGapStatus(string rootId, bool discovery, string status) =>
        _gapStatus[(rootId, discovery)] = status;

    string ILocalGapStore.GapProgress(string rootId) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || ReadGap(root) is not { } epoch) return "";
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT COALESCE(SUM(epoch=$e AND hash IS NOT NULL),0),COUNT(*) FROM local_gap_item WHERE root_no=$r;");
        cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$e", epoch.Epoch);
        long hashed, total;
        using (var reader = cmd.ExecuteReader()) { reader.Read(); hashed = reader.GetInt64(0); total = reader.GetInt64(1); }
        var failures = new List<string>();
        using (var failed = StoreSchema.Command(_connection, null,
            "SELECT json_extract(expected_json,'$.Path'),failure_reason FROM local_gap_item WHERE root_no=$r AND hash IS NULL AND failure_count>=3 LIMIT 5;"))
        {
            failed.Parameters.AddWithValue("$r", root.Value);
            using var reader = failed.ExecuteReader();
            while (reader.Read()) failures.Add(reader.GetString(0) + ": " + reader.GetString(1));
        }
        var deferred = new List<string>();
        using (var cmdDeferred = StoreSchema.Command(_connection, null,
            "SELECT json_extract(expected_json,'$.Path'),defer_reason FROM local_gap_item WHERE root_no=$r AND hash IS NOT NULL AND defer_count>0 LIMIT 5;"))
        {
            cmdDeferred.Parameters.AddWithValue("$r", root.Value);
            using var reader = cmdDeferred.ExecuteReader();
            while (reader.Read()) deferred.Add(reader.GetString(0) + ": " + reader.GetString(1));
        }
        var discovery = _gapStatus.GetValueOrDefault((rootId, true), "metadata discovery pending");
        var hashing = _gapStatus.GetValueOrDefault((rootId, false), "content worker starts on sync");
        var reason = epoch.JournalCursor.Length == 0 ? "journal unavailable: verification needs a readable NTFS journal; ask the Windows administrator to restore/enable it, then Sync Now" :
            epoch.Reason;
        return $"{rootId}: {reason}; queued content hashed {hashed}/{total} (promoted files no longer counted); " +
            (epoch.DiscoveryDone && !discovery.Contains("failed", StringComparison.Ordinal) && !discovery.Contains("deferred", StringComparison.Ordinal) ? "metadata discovery complete" : discovery) + "; " + hashing + (failures.Count == 0 ? "" : "; item verification failed after 3 attempts (inspect the item, then Sync Now to retry): " + string.Join("; ", failures)) +
            (deferred.Count == 0 ? "" : "; proof validation deferred until next sync: " + string.Join("; ", deferred));
    });

    void ILocalGapStore.RetireGapItem(string rootId, LocalGapEpoch epoch, ItemState item) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || ReadGap(root)?.Epoch != epoch.Epoch) return;
        var current = _reads.ByObject(root.Value, item.ObjectId);
        if (current is not null && !SameGapItem(current, item)) return;
        // Retirement is staged too: a planner may still be reading protection for this root.
        StoreSchema.Execute(_connection, null,
            "UPDATE local_gap_item SET metadata_ready=1,metadata_stamp=" + GapObservationSql("$r", "$o") + ",expected_json=$j,hash=NULL,proof_json=NULL,result_seq=0 " +
            "WHERE root_no=$r AND object_id=$o AND epoch=$e;",
            ("$r", root.Value), ("$o", item.ObjectId), ("$e", epoch.Epoch), ("$j", JsonSerializer.Serialize(item)));
    });

    bool ILocalGapStore.IsIncompleteInitialInventory(string rootId) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return true;
        // Full inventory writes metadata in batches before CompleteGeneration persists its cursor.
        // A never-completed initial scan with no reusable hashes has no stale content proof to distrust.
        return StoreSchema.Scalar(_connection, null,
            "SELECT 1 WHERE EXISTS(SELECT 1 FROM generation WHERE root_no=$r AND complete=1) OR " +
            "EXISTS(SELECT 1 FROM file_state WHERE root_no=$r AND kind=0 AND hash IS NOT NULL);", ("$r", root.Value)) is null;
    });

    void ILocalGapStore.RetryGapFailures(string rootId)
    {
        var after = "";
        while (true)
        {
            var ids = InStore(() =>
            {
                var root = RootNo(rootId);
                var page = new List<string>();
                if (root is null) return page;
                using (var cmd = StoreSchema.Command(_connection, null,
                    "SELECT object_id FROM local_gap_item INDEXED BY gap_waiting WHERE root_no=$r AND object_id>$after AND hash IS NULL AND retry_allowed=0 ORDER BY object_id LIMIT 128;"))
                {
                    cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$after", after);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) page.Add(reader.GetString(0));
                }
                using var tx = _connection.BeginTransaction();
                foreach (var id in page) StoreSchema.Execute(_connection, tx,
                    "UPDATE local_gap_item SET retry_allowed=1 WHERE root_no=$r AND object_id=$o;", ("$r", root.Value), ("$o", id));
                tx.Commit();
                return page;
            });
            if (ids.Count < 128) return;
            after = ids[^1];
        }
    }

    void ILocalGapStore.FailGapItem(string rootId, LocalGapEpoch epoch, ItemState item, string reason) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return;
        StoreSchema.Execute(_connection, null,
            "UPDATE local_gap_item SET failure_count=MIN(failure_count+1,3),failure_reason=$why,retry_allowed=CASE WHEN failure_count>=2 THEN 0 ELSE 1 END WHERE root_no=$r AND object_id=$o AND epoch=$e;",
            ("$r", root.Value), ("$o", item.ObjectId), ("$e", epoch.Epoch), ("$why", reason));
    });

    void ILocalGapStore.DeferGapResult(string rootId, LocalGapResult result, string reason) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return;
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, "UPDATE local_gap SET next_result=next_result+1 WHERE root_no=$r;", ("$r", root.Value));
        // One attempt per run: the new sequence is beyond its captured watermark. No result is discarded.
        StoreSchema.Execute(_connection, tx,
            "UPDATE local_gap_item SET result_seq=(SELECT next_result FROM local_gap WHERE root_no=$r)," +
            "defer_count=MIN(defer_count+1,3),defer_reason=$why WHERE root_no=$r AND object_id=$o AND epoch=$e AND result_seq=$s;",
            ("$r", root.Value), ("$o", result.Expected.ObjectId), ("$e", result.Epoch), ("$s", result.Sequence), ("$why", reason));
        tx.Commit();
    });

    LocalGapEpoch? ILocalGapStore.GapEpoch(string rootId) => InStore(() => ReadGap(RootNo(rootId)));

    private LocalGapEpoch? ReadGap(long? root)
    {
        if (root is null) return null;
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT epoch,journal_cursor,boundary,trust_usn,worker_after,worker_done,reason FROM local_gap WHERE root_no=$r;");
        cmd.Parameters.AddWithValue("$r", root.Value);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetInt64(3) != 0, reader.GetString(4), reader.GetInt64(5) != 0, reader.GetString(6)) : null;
    }

    void ILocalGapStore.BeginGap(string rootId, string journalCursor, long boundary, bool trustFileUsn, string reason) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM file_state WHERE root_no=$r AND kind=0 LIMIT 1;", ("$r", root.Value)) is null) return;
        var prior = ReadGap(root);
        // An unfinished untrusted pass cannot become trusted just because the newest gap has a journal id.
        if (prior is not null && HasGapProtection(root))
        {
            if (!prior.TrustFileUsn || !LocalGapWorker.SameJournal(prior.JournalCursor, journalCursor)) trustFileUsn = false;
            if (trustFileUsn) boundary = Math.Min(boundary, prior.Boundary);
        }
        if (prior is not null && prior.JournalCursor == journalCursor && prior.Boundary == boundary &&
            prior.TrustFileUsn == trustFileUsn) return;
        // A later gap requires a new pass even if an earlier epoch is still unfinished.
        // Old entity obligations survive replacement; their staged hashes become ineligible.
        StoreSchema.Execute(_connection, null,
            "INSERT INTO local_gap(root_no,epoch,journal_cursor,boundary,trust_usn,reason) VALUES($r,$e,$c,$b,$t,$why) " +
            "ON CONFLICT(root_no) DO UPDATE SET epoch=excluded.epoch,journal_cursor=excluded.journal_cursor," +
            "boundary=excluded.boundary,trust_usn=excluded.trust_usn,reason=excluded.reason,after_object='',discovery_done=0,worker_after='',worker_done=0;",
            ("$r", root.Value), ("$e", Guid.NewGuid().ToString("N")), ("$c", journalCursor),
            ("$b", boundary), ("$t", trustFileUsn ? 1 : 0), ("$why", reason));
    });

    IReadOnlyList<ItemState> ILocalGapStore.GapDiscoveryPage(string rootId, LocalGapEpoch epoch, int limit) => InStore(() =>
    {
        var root = RootNo(rootId);
        var current = ReadGap(root);
        if (root is null || current is null || current.Epoch != epoch.Epoch || current.DiscoveryDone)
            return Array.Empty<ItemState>();
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT DISTINCT object_id FROM file_state INDEXED BY fs_obj WHERE root_no=$r AND object_id>$after AND kind=0 ORDER BY object_id LIMIT $n;");
        cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$after", current.AfterObject);
        cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 128));
        var ids = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.Select(id => _reads.ByObject(root.Value, id)!).Where(item => item is not null).ToArray();
    });

    private static bool SameGapItem(ItemState left, ItemState right) =>
        left.ObjectId == right.ObjectId && left.Path == right.Path && left.Kind == right.Kind &&
        left.VersionId == right.VersionId && left.Size == right.Size && left.Mtime == right.Mtime &&
        left.Hash == right.Hash && left.Flags == right.Flags;

    void ILocalGapStore.GapDiscovered(string rootId, LocalGapEpoch epoch, ItemState item, bool requiresHash) =>
        ((ILocalGapStore)this).GapDiscoveredBatch(rootId, epoch, [(item, requiresHash)]);

    void ILocalGapStore.GapDiscoveredBatch(string rootId, LocalGapEpoch epoch,
        IReadOnlyList<(ItemState Item, bool RequiresHash)> items) => InStore(() =>
    {
        if (items.Count > 128) throw new ArgumentOutOfRangeException(nameof(items));
        var root = RootNo(rootId);
        if (root is null || ReadGap(root)?.Epoch != epoch.Epoch) return;
        var observed = items.Select(entry => (entry.Item, entry.RequiresHash, Current: _reads.ByObject(root.Value, entry.Item.ObjectId))).ToArray();
        using var tx = _connection.BeginTransaction();
        foreach (var (item, requiresHash, current) in observed)
        {
            var metadataReady = current is null || (SameGapItem(current, item) &&
                (!requiresHash || LocalGapWorker.MetadataOnly(current)));
            // Every discovered entity retains a protection row, including unchanged USN and
            // metadata-only items. Only the next run may publish their discharge.
            StoreSchema.Execute(_connection, tx,
                "INSERT INTO local_gap_item(root_no,object_id,epoch,expected_json,metadata_ready,metadata_stamp) VALUES($r,$o,$e,$j,$m,CASE WHEN $m=1 THEN " + GapObservationSql("$r", "$o") + " ELSE NULL END) " +
                "ON CONFLICT(root_no,object_id) DO UPDATE SET epoch=excluded.epoch,expected_json=excluded.expected_json," +
                "metadata_ready=excluded.metadata_ready,metadata_stamp=excluded.metadata_stamp,hash=NULL,proof_json=NULL,result_seq=0,failure_count=0,failure_reason=NULL,retry_allowed=1,defer_count=0,defer_reason=NULL,discovery_reason=NULL;",
                ("$r", root.Value), ("$o", item.ObjectId), ("$e", epoch.Epoch),
                ("$j", JsonSerializer.Serialize(current ?? item)), ("$m", metadataReady ? 1 : 0));
            // Worker progress is independent of the protection frontier read by planners.
            StoreSchema.Execute(_connection, tx,
                "UPDATE local_gap SET worker_after=$o WHERE root_no=$r AND epoch=$e AND worker_after<$o;",
                ("$r", root.Value), ("$e", epoch.Epoch), ("$o", item.ObjectId));
        }
        tx.Commit();
    });

    void ILocalGapStore.SetGapDiscoveryReasons(string rootId, LocalGapEpoch epoch,
        IReadOnlyList<(string ObjectId, string Reason)> reasons) => InStore(() =>
    {
        if (reasons.Count > 128) throw new ArgumentOutOfRangeException(nameof(reasons));
        var root = RootNo(rootId);
        if (root is null || ReadGap(root)?.Epoch != epoch.Epoch) return;
        using var tx = _connection.BeginTransaction();
        foreach (var (objectId, reason) in reasons)
            StoreSchema.Execute(_connection, tx,
                // Hashing may already have staged this row in the independent lane. Keep its
                // discovery cause too; this update changes neither work eligibility nor proof.
                "UPDATE local_gap_item SET discovery_reason=$why WHERE root_no=$r AND object_id=$o AND epoch=$e;",
                ("$r", root.Value), ("$o", objectId), ("$e", epoch.Epoch), ("$why", reason));
        tx.Commit();
    });

    void ILocalGapStore.GapDiscoveryDone(string rootId, LocalGapEpoch epoch) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is not null) StoreSchema.Execute(_connection, null,
            "UPDATE local_gap SET worker_done=1 WHERE root_no=$r AND epoch=$e AND NOT EXISTS(" +
            "SELECT 1 FROM file_state INDEXED BY fs_obj WHERE root_no=$r AND kind=0 AND object_id>local_gap.worker_after);",
            ("$r", root.Value), ("$e", epoch.Epoch));
    });

    void ILocalGapStore.PromoteGapDiscovery(string rootId)
    {
        var start = InStore(() =>
        {
            var root = RootNo(rootId);
            var capturedEpoch = ReadGap(root);
            var horizon = root is null || capturedEpoch is null ? null : StoreSchema.Scalar(_connection, null,
                "SELECT object_id FROM local_gap_item INDEXED BY gap_metadata WHERE root_no=$r AND epoch=$e " +
                "AND metadata_ready=1 ORDER BY object_id DESC LIMIT 1;", ("$r", root.Value), ("$e", capturedEpoch.Epoch)) as string;
            return (Root: root, Epoch: capturedEpoch, Through: horizon);
        });
        if (start.Root is not { } rootNo || start.Epoch is not { } epoch) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long promoted = 0;
        var after = "";
        try
        {
            while (start.Through is { } through)
            {
                // Release the store gate and WAL writer between bounded transactions. A
                // captured horizon prevents a concurrent discovery lane extending this run.
                var page = InStore(() =>
                {
                    using var tx = _connection.BeginTransaction();
                    if (StoreSchema.Scalar(_connection, tx,
                        "SELECT 1 FROM local_gap WHERE root_no=$r AND epoch=$e;",
                        ("$r", rootNo), ("$e", epoch.Epoch)) is null) return (Last: (string?)null, Count: 0L);
                    var last = StoreSchema.Scalar(_connection, tx,
                        "SELECT MAX(object_id) FROM (SELECT object_id FROM local_gap_item INDEXED BY gap_metadata " +
                        "WHERE root_no=$r AND epoch=$e AND metadata_ready=1 AND object_id>$after AND object_id<=$through " +
                        "ORDER BY object_id LIMIT 512);", ("$r", rootNo), ("$e", epoch.Epoch),
                        ("$after", after), ("$through", through)) as string;
                    if (last is null) return (Last: (string?)null, Count: 0L);
                    const string range = " WHERE root_no=$r AND epoch=$e AND metadata_ready=1 AND object_id>$after AND object_id<=$last";
                    StoreSchema.Execute(_connection, tx,
                        "DELETE FROM local_gap_item INDEXED BY gap_metadata" + range +
                        " AND metadata_stamp IS " + GapObservationSql("local_gap_item.root_no", "local_gap_item.object_id") + ";",
                        ("$r", rootNo), ("$e", epoch.Epoch), ("$after", after), ("$last", last));
                    var count = Convert.ToInt64(StoreSchema.Scalar(_connection, tx, "SELECT changes();"));
                    // Changed observations retain protection and rejoin the normal retry lane.
                    StoreSchema.Execute(_connection, tx,
                        "UPDATE local_gap_item INDEXED BY gap_metadata SET metadata_ready=0,metadata_stamp=NULL,retry_allowed=1,failure_count=0" + range + ";",
                        ("$r", rootNo), ("$e", epoch.Epoch), ("$after", after), ("$last", last));
                    tx.Commit();
                    return (Last: (string?)last, Count: count);
                });
                if (page.Last is null) break;
                promoted += page.Count;
                after = page.Last;
            }
            InStore(() =>
            {
                // Publish only the captured worker frontier, after every page completes.
                // New staged rows retain explicit holds; replacement epochs are untouched.
                StoreSchema.Execute(_connection, null,
                    "UPDATE local_gap SET after_object=MAX(after_object,$after),discovery_done=MAX(discovery_done,$done) WHERE root_no=$r AND epoch=$e;",
                    ("$r", rootNo), ("$e", epoch.Epoch), ("$after", epoch.AfterObject), ("$done", epoch.DiscoveryDone ? 1 : 0));
            });
        }
        finally
        {
            RunLog.Trace($"local gap metadata promotion: root={rootId}; count={promoted}; elapsed-ms={watch.ElapsedMilliseconds}");
        }
    }

    IReadOnlyList<ItemState> ILocalGapStore.GapHashPage(string rootId, LocalGapEpoch epoch, int limit) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || ReadGap(root)?.Epoch != epoch.Epoch) return Array.Empty<ItemState>();
        StageGapOrphans(root.Value, epoch.Epoch);
        var ids = new List<string>();
        using (var cmd = StoreSchema.Command(_connection, null,
            "SELECT object_id FROM local_gap_item INDEXED BY gap_ready WHERE root_no=$r AND epoch=$e AND hash IS NULL AND retry_allowed=1 AND metadata_ready=0 " +
            "ORDER BY last_attempt,object_id LIMIT $n;"))
        {
            cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$e", epoch.Epoch);
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 128));
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        var result = new List<ItemState>();
        var current = ids.Select(id => (Id: id, Item: _reads.ByObject(root.Value, id))).ToArray();
        using var tx = _connection.BeginTransaction();
        foreach (var (id, item) in current)
        {
            if (item is null)
                StoreSchema.Execute(_connection, tx, "UPDATE local_gap_item SET metadata_ready=1,metadata_stamp=NULL WHERE root_no=$r AND object_id=$o AND epoch=$e;",
                    ("$r", root.Value), ("$o", id), ("$e", epoch.Epoch));
            else
            {
                // Rotation before IO prevents a blocked first page from starving later files.
                StoreSchema.Execute(_connection, tx,
                    "UPDATE local_gap_item SET last_attempt=$t,expected_json=$j,retry_allowed=CASE WHEN failure_count>=3 THEN 0 ELSE retry_allowed END WHERE root_no=$r AND object_id=$o;",
                    ("$r", root.Value), ("$o", id), ("$t", DateTime.UtcNow.Ticks), ("$j", JsonSerializer.Serialize(item)));
                result.Add(item);
            }
        }
        tx.Commit();
        return result.ToArray();
    });

    void ILocalGapStore.StageGapHash(string rootId, LocalGapEpoch epoch, ItemState item, string hash, LocalGapProof proof) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || ReadGap(root)?.Epoch != epoch.Epoch ||
            _reads.ByObject(root.Value, item.ObjectId) is not { } current || !SameGapItem(current, item)) return;
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, "UPDATE local_gap SET next_result=next_result+1 WHERE root_no=$r;", ("$r", root.Value));
        // Staging changes no LIVE row, flags or foreground obligations.
        StoreSchema.Execute(_connection, tx,
            "UPDATE local_gap_item SET hash=$h,proof_json=$p,failure_count=0,failure_reason=NULL,retry_allowed=1,defer_count=0,defer_reason=NULL,result_seq=(SELECT next_result FROM local_gap WHERE root_no=$r) " +
            "WHERE root_no=$r AND object_id=$o AND epoch=$e AND metadata_ready=0 AND expected_json=$j;",
            ("$r", root.Value), ("$o", item.ObjectId), ("$e", epoch.Epoch), ("$j", JsonSerializer.Serialize(item)),
            ("$h", hash), ("$p", JsonSerializer.Serialize(proof)));
        tx.Commit();
    });

    long ILocalGapStore.GapResultWatermark(string rootId) => InStore(() =>
    {
        var root = RootNo(rootId);
        return root is null ? 0 : Convert.ToInt64(StoreSchema.Scalar(_connection, null,
            "SELECT next_result FROM local_gap WHERE root_no=$r;", ("$r", root.Value)) ?? 0L);
    });

    IReadOnlyList<LocalGapResult> ILocalGapStore.GapResults(string rootId, long through, int limit, long after) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return Array.Empty<LocalGapResult>();
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT i.epoch,i.expected_json,i.hash,i.proof_json,i.result_seq FROM local_gap_item i INDEXED BY gap_results " +
            "JOIN local_gap g ON g.root_no=i.root_no AND g.epoch=i.epoch " +
            "WHERE i.root_no=$r AND i.hash IS NOT NULL AND i.result_seq<=$through AND i.result_seq>$after ORDER BY i.result_seq,i.object_id LIMIT $n;");
        cmd.Parameters.AddWithValue("$r", root.Value); cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 128));
        cmd.Parameters.AddWithValue("$through", through); cmd.Parameters.AddWithValue("$after", after);
        var result = new List<LocalGapResult>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetString(0), JsonSerializer.Deserialize<ItemState>(reader.GetString(1))!,
            reader.GetString(2), JsonSerializer.Deserialize<LocalGapProof>(reader.GetString(3))!, reader.GetInt64(4)));
        return result.ToArray();
    });

    bool ILocalGapStore.AcceptGapResult(string rootId, LocalGapResult result) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || ReadGap(root)?.Epoch != result.Epoch ||
            _reads.ByObject(root.Value, result.Expected.ObjectId) is not { } current || !SameGapItem(current, result.Expected)) return false;
        using var tx = _connection.BeginTransaction();
        var exists = StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM local_gap_item WHERE root_no=$r AND object_id=$o AND epoch=$e AND result_seq=$s AND hash=$h;",
            ("$r", root.Value), ("$o", current.ObjectId), ("$e", result.Epoch), ("$s", result.Sequence), ("$h", result.Hash));
        if (exists is null || StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM file_state WHERE root_no=$r AND object_id=$o AND (size!=$s OR mtime!=$m) LIMIT 1;",
            ("$r", root.Value), ("$o", current.ObjectId), ("$s", current.Size), ("$m", current.Mtime.UtcTicks)) is not null) return false;
        // Alias publication and durable planning handoff are atomic with retiring the gap obligation.
        StoreSchema.Execute(_connection, tx,
            "UPDATE file_state SET hash=$h,flags=flags & ~256 WHERE root_no=$r AND object_id=$o;",
            ("$r", root.Value), ("$o", current.ObjectId), ("$h", RowCodec.HashBlob(result.Hash)));
        StoreSchema.Execute(_connection, tx,
            "INSERT INTO local_rehash(root_no,object_id,last_attempt,reason) VALUES($r,$o,$t,'journal gap verified; planning pending') " +
            "ON CONFLICT(root_no,object_id) DO UPDATE SET last_attempt=excluded.last_attempt,reason=excluded.reason;",
            ("$r", root.Value), ("$o", current.ObjectId), ("$t", NextRehashOrder(root.Value, tx)));
        StoreSchema.Execute(_connection, tx, "DELETE FROM local_gap_item WHERE root_no=$r AND object_id=$o;",
            ("$r", root.Value), ("$o", current.ObjectId));
        tx.Commit();
        return true;
    });

    void ILocalGapStore.RejectGapResult(string rootId, LocalGapResult result) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is not null) StoreSchema.Execute(_connection, null,
            "UPDATE local_gap_item SET hash=NULL,proof_json=NULL,result_seq=0,failure_count=0,failure_reason=NULL,retry_allowed=0,defer_count=0,defer_reason=NULL WHERE root_no=$r AND object_id=$o AND epoch=$e AND result_seq=$s;",
            ("$r", root.Value), ("$o", result.Expected.ObjectId), ("$e", result.Epoch), ("$s", result.Sequence));
    });

    bool ILocalGapStore.HasGapProtection(string rootId) => InStore(() => HasGapProtection(RootNo(rootId)));

    private bool HasGapProtection(long? root) => root is not null && StoreSchema.Scalar(_connection, null,
        "SELECT 1 FROM local_gap WHERE root_no=$r AND (discovery_done=0 OR EXISTS(" +
        "SELECT 1 FROM local_gap_item i JOIN file_state f ON f.root_no=i.root_no AND f.object_id=i.object_id WHERE i.root_no=$r)) LIMIT 1;",
        ("$r", root.Value)) is not null;
}
