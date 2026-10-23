using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Queue audit V4: run-start metadata promotion is bounded per pass. Promoted rows leave the
/// staged index, so the unvisited remainder is durable and the next run (or the same store after
/// reopen) resumes it. The published discovery frontier waits for a complete drain, so the
/// unvisited suffix keeps its protection in the meantime. Every verified unchanged entity is also
/// marked planning-dirty for the pairs using the root (existing planning_deferred), while a changed
/// observation stays protected, is never marked dirty, and creates no content-hash debt.
/// </summary>
internal static class GapPromotionBoundChecks
{
    /// <summary>Disposable test databases: no per-commit fsync, so the counted bound stays fast.</summary>
    private static readonly V2Settings Fast = V2Settings.Default with { StoreDurableCommits = false };

    /// <summary>Fixture entry point: <c>--v2-gap-promotion-bound-checks &lt;root&gt;</c>.</summary>
    public static void Run(string root)
    {
        // Never remove a caller-supplied directory: this fixture owns one unique child only.
        var product = Path.Combine(root, "gap-promotion-bound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(product);
        const string rootId = "promotion-bound-root";
        const string cursor = "0000000000000001:200";
        const int staged = 2600;
        const int perPass = SqliteStateStore.GapPromotionPagesPerPass * SqliteStateStore.GapPromotionPageSize;
        const int changedIndex = 10;
        var rows = Enumerable.Range(1, staged).Select(n => new ItemState("folder/" + n.ToString("D5"),
            ItemKind.File, "object-" + n.ToString("D5"), null, new string('a', 64), 3,
            DateTimeOffset.UnixEpoch.AddDays(1), ItemFlags.None)).ToArray();
        using (var store = SqliteStateStore.Open(product, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
        {
            // The handoff is pair-scoped: a saved pair is what makes the root consumable.
            store.SavePair(new PairDef("p1", "Pair A",
                new RootDef(rootId, SyncProviders.Local, null, "/local", "id:local:1"),
                new RootDef("peer-root", SyncProviders.Dropbox, null, "/cloud", "id:dbx:1"), [], [], true));
            store.WriteSnapshot(new(rootId, Side.A, store.BeginGeneration(rootId, "full"), cursor, rows, []));
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(rootId, cursor, 100, true);
            var epoch = gaps.GapEpoch(rootId)!;
            foreach (var batch in rows.Chunk(128))
                gaps.GapDiscoveredBatch(rootId, epoch, batch.Select(item => (item, false)).ToArray());
            gaps.GapDiscoveryDone(rootId, epoch);
            Require(gaps.GapEpoch(rootId)!.DiscoveryDone && Staged(product) == staged,
                "the fixture stages every one of the " + staged + " unchanged metadata entities");
            // One observation changes after discovery: its proof no longer discharges.
            Exec(product, "UPDATE file_state SET mtime=mtime+1 WHERE root_no=(SELECT root_no FROM root WHERE root_id=$r) " +
                "AND object_id=$o;", ("$r", rootId), ("$o", rows[changedIndex].ObjectId));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            gaps.PromoteGapDiscovery(rootId);
            watch.Stop();
            var admitted = staged - Staged(product);
            Require(admitted == perPass,
                $"one promotion pass admits exactly {perPass} of {staged} staged entities (measured {admitted})");
            Console.WriteLine($"INFO metadata promotion pass: staged={staged}; admitted={admitted}; " +
                $"remaining={Staged(product)}; elapsed-ms={watch.ElapsedMilliseconds}");
            Require(GapScalar(product, "SELECT discovery_done FROM local_gap;") == 0,
                "a capped pass leaves the published discovery frontier unpublished");
            Require(GapText(product, "SELECT MIN(object_id) FROM local_gap_item WHERE metadata_ready=1;") ==
                    rows[perPass].ObjectId,
                "the durable remainder resumes at the oldest unvisited staged entity");
            Require(DeferredForPair(product, rootId) == perPass - 1,
                "every matching entity is marked planning-dirty once for the pair using the root");
            Require(DeferredForObject(product, rootId, rows[changedIndex].ObjectId) == 0 &&
                GapScalar(product, "SELECT COUNT(*) FROM local_gap_item WHERE object_id=$o AND metadata_ready=0;",
                    ("$o", rows[changedIndex].ObjectId)) == 1,
                "the changed observation is retained and is never marked planning-dirty");
            Require(GapScalar(product, "SELECT COUNT(*) FROM local_rehash;") == 0,
                "the planning handoff creates no content-hash debt");
            var kept = store.ByObject(rootId, rows[0].ObjectId)!;
            Require(kept.Hash == rows[0].Hash && kept.Flags == rows[0].Flags && kept.Size == rows[0].Size &&
                kept.Mtime.UtcTicks == rows[0].Mtime.UtcTicks,
                "the planning handoff preserves the verified entity's hash, flags and metadata");
            Require(store.ProtectionBatch(rootId, ItemFlags.NeedsRehash, [rows[0].Path, rows[^1].Path])
                    .All(hit => hit.Covered) &&
                store.ProtectionBatch(rootId, ItemFlags.NeedsRehash, [rows[changedIndex].Path])[0].Covered,
                "a capped pass keeps the unvisited suffix and the changed observation protected");
        }
        using (var store = SqliteStateStore.Open(product, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
        {
            // The rows retired by the capped pass are gone; the remainder was committed before the
            // store closed, so this reopen resumes the same epoch without a new cursor.
            Require(Staged(product) == staged - perPass, "the staged remainder survives the store reopen");
            var gaps = (ILocalGapStore)store;
            while (Staged(product) > 0)
            {
                var before = Staged(product);
                gaps.PromoteGapDiscovery(rootId);
                Require(before - Staged(product) is > 0 and <= perPass,
                    "each reopened promotion remains bounded by downstream intake capacity");
            }
            gaps.PromoteGapDiscovery(rootId);
            Require(GapScalar(product, "SELECT discovery_done FROM local_gap;") == 1,
                "a reopened store drains the durable remainder and only then publishes completion");
            Require(DeferredForPair(product, rootId) == staged - 1 &&
                GapScalar(product, "SELECT COUNT(*) FROM local_rehash;") == 0,
                "both passes leave one planning-dirty row per verified entity and no content-hash debt");
            Require(store.ProtectionBatch(rootId, ItemFlags.NeedsRehash, [rows[0].Path, rows[^1].Path])
                    .All(hit => !hit.Covered),
                "a complete drain releases the metadata suffix protection");
            Require(store.ProtectionBatch(rootId, ItemFlags.NeedsRehash, [rows[changedIndex].Path])[0].Covered,
                "the changed observation stays protected in the normal retry lane");
            gaps.PromoteGapDiscovery(rootId);
            Require(Staged(product) == 0 && GapScalar(product, "SELECT discovery_done FROM local_gap;") == 1,
                "a repeat pass after a complete drain changes nothing");
        }
        Console.WriteLine("PASS bounded gap metadata promotion resumes its durable remainder next run");
        StageGapOrphansPlanChecks(root);
    }

    // Queue audit STORE+INDEX investigation: StageGapOrphans' correlated NOT EXISTS on
    // file_state(root_no,object_id) must seek fs_obj, never scan the table. This is the exact
    // production statement text from SqliteStateStore.Gap.cs; the disposable store database holds
    // enough rows that a scan would be visible in the plan.
    private static void StageGapOrphansPlanChecks(string root)
    {
        var product = Path.Combine(root, "stage-gap-orphans-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(product);
        using (SqliteStateStore.Open(product, Fast, new TestClock(), LocalItems.CaptureOptionalHash)) { }
        using var db = new SqliteConnection($"Data Source={DbPath(product)};Pooling=False");
        db.Open();
        const int rows = 20000;
        using (var tx = db.BeginTransaction())
        {
            using (var file = db.CreateCommand())
            {
                file.Transaction = tx;
                file.CommandText = "INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id," +
                    "version_id,hash,size,mtime,flags) VALUES(1,1,$key,$name,0,$id,NULL,NULL,1,1,0);";
                var key = file.Parameters.Add("$key", SqliteType.Text);
                var name = file.Parameters.Add("$name", SqliteType.Text);
                var id = file.Parameters.Add("$id", SqliteType.Text);
                for (var n = 0; n < rows; n++)
                {
                    key.Value = $"K{n:D6}"; name.Value = $"f{n:D6}"; id.Value = $"object-{n:D6}";
                    file.ExecuteNonQuery();
                }
            }
            using (var gap = db.CreateCommand())
            {
                gap.Transaction = tx;
                gap.CommandText = "INSERT INTO local_gap_item(root_no,object_id,epoch,expected_json,metadata_ready) " +
                    "VALUES(1,$id,'epoch','{}',1);";
                var id = gap.Parameters.Add("$id", SqliteType.Text);
                // Orphans: every second staged entity has no file_state row.
                for (var n = 0; n < rows; n += 2) { id.Value = $"object-{n:D6}"; gap.ExecuteNonQuery(); }
            }
            tx.Commit();
        }
        const string statement =
            "UPDATE local_gap_item SET epoch=$e,metadata_ready=1,metadata_stamp=NULL,hash=NULL,proof_json=NULL,result_seq=0 " +
            "WHERE root_no=$r AND object_id=$o AND NOT EXISTS(SELECT 1 FROM file_state WHERE root_no=$r AND object_id=$o);";
        var plan = new List<string>();
        using (var explain = db.CreateCommand())
        {
            explain.CommandText = "EXPLAIN QUERY PLAN " + statement;
            explain.Parameters.AddWithValue("$e", "epoch-2");
            explain.Parameters.AddWithValue("$r", 1);
            explain.Parameters.AddWithValue("$o", "object-000000");
            using var reader = explain.ExecuteReader();
            while (reader.Read()) plan.Add(reader.GetString(3));
        }
        Require(plan.Any(line => line.Contains("SEARCH file_state", StringComparison.Ordinal) &&
                    line.Contains("fs_obj", StringComparison.Ordinal)) &&
                plan.All(line => !line.Contains("SCAN file_state", StringComparison.Ordinal)),
            "stage-gap-orphans existence check seeks fs_obj instead of scanning file_state");
        Console.WriteLine("INFO stage-gap-orphans plan over " + rows + " rows: " + string.Join(" | ", plan));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var tx = db.BeginTransaction())
        using (var run = db.CreateCommand())
        {
            run.Transaction = tx;
            run.CommandText = statement;
            var epoch = run.Parameters.Add("$e", SqliteType.Text);
            var rootNo = run.Parameters.Add("$r", SqliteType.Integer);
            var objectId = run.Parameters.Add("$o", SqliteType.Text);
            epoch.Value = "epoch-2"; rootNo.Value = 1;
            for (var n = 0; n < 128; n++) { objectId.Value = $"object-{n * 2:D6}"; run.ExecuteNonQuery(); }
            tx.Commit();
        }
        watch.Stop();
        Console.WriteLine($"INFO stage-gap-orphans 128 updates: {watch.ElapsedMilliseconds} ms");
    }

    private static long Staged(string dataRoot) =>
        GapScalar(dataRoot, "SELECT COUNT(*) FROM local_gap_item WHERE metadata_ready=1;");

    // Pair- and root-scoped, exactly as MarkPlanningDirty writes it.
    private static long DeferredForPair(string dataRoot, string rootId) =>
        GapScalar(dataRoot, "SELECT COUNT(*) FROM planning_deferred d JOIN pair p ON p.pair_no=d.pair_no " +
            "JOIN root r ON r.root_no=d.root_no WHERE p.pair_id='p1' AND r.root_id=$r;", ("$r", rootId));

    private static long DeferredForObject(string dataRoot, string rootId, string objectId) =>
        GapScalar(dataRoot, "SELECT COUNT(*) FROM planning_deferred d JOIN pair p ON p.pair_no=d.pair_no " +
            "JOIN root r ON r.root_no=d.root_no WHERE p.pair_id='p1' AND r.root_id=$r AND d.object_id=$o;",
            ("$r", rootId), ("$o", objectId));

    private static long GapScalar(string dataRoot, string sql, params (string, object?)[] parameters)
    {
        using var db = new SqliteConnection($"Data Source={DbPath(dataRoot)};Pooling=False");
        db.Open();
        return Convert.ToInt64(StoreSchema.Scalar(db, null, sql, parameters));
    }

    private static string? GapText(string dataRoot, string sql, params (string, object?)[] parameters)
    {
        using var db = new SqliteConnection($"Data Source={DbPath(dataRoot)};Pooling=False");
        db.Open();
        return StoreSchema.Scalar(db, null, sql, parameters) as string;
    }

    private static void Exec(string dataRoot, string sql, params (string, object?)[] parameters)
    {
        using var db = new SqliteConnection($"Data Source={DbPath(dataRoot)};Pooling=False");
        db.Open();
        StoreSchema.Execute(db, null, sql, parameters);
    }

    private static string DbPath(string dataRoot) => Path.Combine(dataRoot, "v2", "state.db");

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
