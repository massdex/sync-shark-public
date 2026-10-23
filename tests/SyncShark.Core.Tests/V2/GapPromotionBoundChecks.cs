using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Run-start metadata promotion drains its captured horizon in bounded transactions. Promoted
/// rows leave the staged index; interruption preserves the unvisited remainder. Completion is
/// published only after the drain, and another store caller can enter between pages. Every verified unchanged entity is also
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
        var cursor = new string('0', 15) + "1:200";
        const int staged = 10000;
        var pageRows = Fast.GapPromotionPageRows;
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
            var pages = 0;
            var remaining = (long)staged;
            var peakRows = 0L;
            store.GapPromotionPageCommittedForTest = () =>
            {
                var next = Staged(product);
                var visited = remaining - next;
                Require(visited > 0 && visited <= pageRows, "each committed page respects the configured row bound");
                peakRows = Math.Max(peakRows, visited);
                remaining = next;
                if (++pages != 1) return;
                Require(next == staged - pageRows && GapScalar(product, "SELECT discovery_done FROM local_gap;") == 0,
                    "the first page commits without publishing the unfinished frontier");
                var reader = Task.Run(() => store.Pair("p1"));
                Require(reader.Wait(TimeSpan.FromSeconds(10)) && reader.Result is not null,
                    "a concurrent store call gets the gate between promotion pages");
            };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            gaps.PromoteGapDiscovery(rootId);
            watch.Stop();
            store.GapPromotionPageCommittedForTest = null;
            var admitted = staged - Staged(product);
            Require(admitted == staged && pages == (staged + pageRows - 1) / pageRows && peakRows == pageRows,
                "one invocation drains 10000 entities in bounded pages");
            Console.WriteLine($"INFO metadata promotion: staged={staged}; admitted={admitted}; " +
                $"pages={pages}; peak-page-rows={peakRows}; elapsed-ms={watch.ElapsedMilliseconds}");
            Require(GapScalar(product, "SELECT discovery_done FROM local_gap;") == 1,
                "the invocation publishes the frontier only after draining its captured horizon");
            Require(DeferredForPair(product, rootId) == staged - 1,
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
                    .All(hit => !hit.Covered) &&
                store.ProtectionBatch(rootId, ItemFlags.NeedsRehash, [rows[changedIndex].Path])[0].Covered,
                "the completed drain releases verified rows but keeps the changed observation protected");
        }
        using (var store = SqliteStateStore.Open(product, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
        {
            Require(Staged(product) == 0, "the completed drain survives the store reopen");
            var gaps = (ILocalGapStore)store;
            gaps.PromoteGapDiscovery(rootId);
            Require(GapScalar(product, "SELECT discovery_done FROM local_gap;") == 1,
                "a reopened store retains the completed frontier");
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
        Console.WriteLine("PASS gap metadata promotion drains its horizon in bounded transactions");
        InterruptedPromotionChecks(root);
        StageGapOrphansPlanChecks(root);
    }

    private static void InterruptedPromotionChecks(string root)
    {
        var product = Path.Combine(root, "gap-promotion-restart-" + Guid.NewGuid().ToString("N"));
        var settings = Fast with { GapPromotionPageRows = 7 };
        const string rootId = "restart-root";
        var rows = Enumerable.Range(0, 31).Select(n => new ItemState("file-" + n.ToString("D3"),
            ItemKind.File, "object-" + n.ToString("D3"), null, new string('b', 64), 3,
            DateTimeOffset.UnixEpoch.AddDays(1), ItemFlags.None)).ToArray();
        using (var store = SqliteStateStore.Open(product, settings, new TestClock(), LocalItems.CaptureOptionalHash))
        {
            store.WriteSnapshot(new(rootId, Side.A, store.BeginGeneration(rootId, "full"), "cursor", rows, []));
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(rootId, "cursor", 100, true);
            var epoch = gaps.GapEpoch(rootId)!;
            gaps.GapDiscoveredBatch(rootId, epoch, rows.Select(item => (item, false)).ToArray());
            gaps.GapDiscoveryDone(rootId, epoch);
            store.GapPromotionPageCommittedForTest = () => throw new OperationCanceledException("test interruption");
            var interrupted = false;
            try { gaps.PromoteGapDiscovery(rootId); }
            catch (OperationCanceledException) { interrupted = true; }
            Require(interrupted && Staged(product) == rows.Length - settings.GapPromotionPageRows,
                "interruption retains exactly the unvisited committed-page remainder");
            Require(GapScalar(product, "SELECT discovery_done FROM local_gap;") == 0 &&
                store.ProtectionBatch(rootId, ItemFlags.NeedsRehash, [rows[^1].Path])[0].Covered,
                "interruption retains frontier and suffix protection");
        }
        using (var store = SqliteStateStore.Open(product, settings, new TestClock(), LocalItems.CaptureOptionalHash))
        {
            Require(Staged(product) == rows.Length - settings.GapPromotionPageRows,
                "unvisited rows survive reopen");
            ((ILocalGapStore)store).PromoteGapDiscovery(rootId);
            Require(Staged(product) == 0 && GapScalar(product, "SELECT discovery_done FROM local_gap;") == 1 &&
                store.ProtectionBatch(rootId, ItemFlags.NeedsRehash, [rows[^1].Path]).All(hit => !hit.Covered),
                "one resumed invocation drains and releases the durable remainder");
        }
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
