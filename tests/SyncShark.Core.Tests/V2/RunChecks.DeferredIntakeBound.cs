using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // The storage owner's hard run bounds (SqliteStateStore.AdmitDeferredPlanning).
    private const int DeferredIntakeEntities = 2048;
    private const int DeferredIntakePaths = 8192;

    /// <summary>
    /// Bounded deferred intake (RQ-032, RQ-150). One run reads one oldest-first indexed page of
    /// queued verification work, admits at most 2048 entities and 8192 alias paths in all, and
    /// rotates the admitted versions behind the unadmitted queue in the same transaction so a
    /// permanently held prefix cannot starve later entries across runs or restarts. The returned
    /// rotated version is what the existing exact acknowledgement retires; a newer hold survives.
    /// A read-only preview selects the same page but never rotates. Per-entity alias lists above
    /// the run budget stay unacknowledgeable (existing AllAliases safeguard; residual C36).
    /// </summary>
    internal static async Task DeferredIntakeBoundAsync()
    {
        DeferredIntakeStoreChecks();
        DeferredIntakeMillion();
        DeferredIntakeFairnessChecks();
        DeferredIntakeAliasCapChecks();
        await DeferredIntakePreviewAsync();
        await DeferredIntakeProductionRunAsync();
        Console.WriteLine("PASS DeferredIntakeBound");
    }

    private static void DeferredIntakeStoreChecks()
    {
        var folder = FixtureFolder("deferred-intake-store");
        try
        {
            var pair = IntakePair();
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            store.SavePair(pair);
            const int backlog = 10_000;
            store.DeferPlanning(pair.PairId, IntakeBacklog(pair, 0, backlog));
            Require(store.CountDeferredPlanning(pair.PairId) == backlog,
                "the intake fixture queued ten thousand real deferred entities");

            var before = DeferredVersions(folder);
            Require(before.Count == backlog, "every queued entity is a real persisted row");
            var cutoff = before.Values.Max() + 1;
            var watch = Stopwatch.StartNew();
            var page = store.AdmitDeferredPlanning(pair.PairId, cutoff);
            watch.Stop();
            var oldest = before.OrderBy(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(DeferredIntakeEntities).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            Require(page.Count == DeferredIntakeEntities,
                "one run admits exactly one bounded entity page over a ten-thousand-row backlog");
            Require(page.All(row => oldest.Contains(row.ObjectId)),
                "intake takes the oldest queued entities first, served by the version index");
            Require(page.Sum(row => row.Paths.Count) <= DeferredIntakePaths,
                "one run materializes at most 8192 alias paths in all");
            Require(page.All(row => row.Version > cutoff && row.Version > before[row.ObjectId]),
                "admitted versions rotate behind the unadmitted queue in the intake transaction");
            var persisted = DeferredVersions(folder);
            Require(page.All(row => persisted[row.ObjectId] == row.Version),
                "the returned rotated version is the durable version the exact acknowledgement matches");
            Require(persisted.Count == backlog, "rotation never removes queued work");
            Console.WriteLine($"deferred intake: entities={page.Count} paths={page.Sum(row => row.Paths.Count)} " +
                $"backlog={persisted.Count} elapsed={watch.ElapsedMilliseconds}ms");

            // Read-only intake (the plan-preview path) never rotates a durable version.
            var preview = store.AdmitDeferredPlanning(pair.PairId, persisted.Values.Max() + 1, rotate: false);
            Require(preview.Count == DeferredIntakeEntities && preview.All(row =>
                    persisted[row.ObjectId] == row.Version),
                "read-only intake selects a bounded page without touching durable versions");
            Require(DeferredVersions(folder).OrderBy(entry => entry.Key).SequenceEqual(persisted.OrderBy(entry => entry.Key)),
                "a read-only intake leaves every queued version unchanged");

            // Acknowledgement retires exactly the rotated page, not a re-deferred newer hold.
            var retire = store.AdmitDeferredPlanning(pair.PairId, DeferredVersions(folder).Values.Max() + 1);
            var reHeld = retire[0];
            store.DeferPlanning(pair.PairId, new[] { (pair.B.RootId, IntakeItem(reHeld.ObjectId)) });
            var count = store.CountDeferredPlanning(pair.PairId);
            store.AcknowledgeDeferredPlanning(pair.PairId, retire, _ => true);
            Require(store.CountDeferredPlanning(pair.PairId) == count - retire.Count + 1,
                "acknowledgement retires the exact rotated page and keeps one newer hold");
        }
        finally { DeleteFixture(folder); }
    }

    private static void DeferredIntakeFairnessChecks()
    {
        var folder = FixtureFolder("deferred-intake-fair");
        try
        {
            var pair = IntakePair();
            const int backlog = 4800;
            HashSet<string> firstPage;
            using (var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                store.SavePair(pair);
                store.DeferPlanning(pair.PairId, IntakeBacklog(pair, 0, backlog));
                firstPage = store.AdmitDeferredPlanning(pair.PairId,
                    DeferredVersions(folder).Values.Max() + 1).Select(row => row.ObjectId)
                    .ToHashSet(StringComparer.Ordinal);
                Require(firstPage.Count == DeferredIntakeEntities,
                    "the held first page fills the bounded intake exactly");
            }

            using (var reopened = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                var versions = DeferredVersions(folder);
                var second = reopened.AdmitDeferredPlanning(pair.PairId, versions.Values.Max() + 1);
                Require(second.Count == DeferredIntakeEntities,
                    "a restarted run admits the next bounded page while the first page is still held");
                Require(second.All(row => !firstPage.Contains(row.ObjectId) && versions[row.ObjectId] < row.Version),
                    "a permanently held admitted prefix cannot steal the next run's page after a restart");
                Require(firstPage.All(id => DeferredVersions(folder).ContainsKey(id)),
                    "held work is retained, never deleted by rotation");

                // Every queued entity is eventually admitted: keep taking oldest pages to the end.
                var admittedEver = new HashSet<string>(firstPage, StringComparer.Ordinal);
                admittedEver.UnionWith(second.Select(row => row.ObjectId));
                var pending = DeferredVersions(folder).Keys.Except(admittedEver, StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);
                var rounds = 0;
                while (pending.Count > 0 && rounds++ < 8)
                {
                    var next = reopened.AdmitDeferredPlanning(pair.PairId,
                        DeferredVersions(folder).Values.Max() + 1);
                    var unseenInPage = next.Count(row => !admittedEver.Contains(row.ObjectId));
                    Require(unseenInPage == Math.Min(DeferredIntakeEntities, pending.Count),
                        "each successive run admits its oldest unseen entities first, never only the held prefix");
                    admittedEver.UnionWith(next.Select(row => row.ObjectId));
                    pending = DeferredVersions(folder).Keys.Except(admittedEver, StringComparer.Ordinal)
                        .ToHashSet(StringComparer.Ordinal);
                }
                Require(pending.Count == 0,
                    "every deferred entity is eventually admitted even while an earlier page is permanently held");
            }
        }
        finally { DeleteFixture(folder); }
    }

    private static void DeferredIntakeAliasCapChecks()
    {
        var folder = FixtureFolder("deferred-intake-alias");
        try
        {
            var pair = IntakePair();
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            store.SavePair(pair);
            var aliases = Enumerable.Range(0, 8193).Select(n => new ItemState($"Alias/{n:D5}", ItemKind.File,
                "aliased-object", null, new string('a', 64), 1, DateTimeOffset.UnixEpoch, ItemFlags.None)).ToArray();
            SeedRoot(store, pair.B, Side.B, aliases);
            var anchor = new ItemState("Anchor", ItemKind.File, "anchor-object", null, new string('b', 64),
                1, DateTimeOffset.UnixEpoch, ItemFlags.None);
            SeedRoot(store, pair.A, Side.A, [anchor]);
            // 9f22aed0 reserves one slot for the queued spelling before reading current aliases.
            // Use a distinct former spelling so this exact-cap fixture contains 8192 unique paths.
            store.DeferPlanning(pair.PairId, new[] { (pair.B.RootId, aliases[0] with { Path = "Former/alias" }) });
            var page = store.AdmitDeferredPlanning(pair.PairId, DeferredVersions(folder).Values.Max() + 1);
            Require(page.Count == 1 && page[0].Paths.Count == DeferredIntakePaths && !page[0].AllAliases,
                "an entity with more aliases than the run budget is admitted bounded and flagged partial");
            store.AcknowledgeDeferredPlanning(pair.PairId, page, _ => true);
            Require(store.CountDeferredPlanning(pair.PairId) == 1,
                "the existing AllAliases safeguard keeps a partially materialized entity unacknowledged (C36)");

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
            connection.Open();
            using var explain = StoreSchema.Command(connection, null,
                "EXPLAIN QUERY PLAN " + SqliteStateStore.DeferredPlanningIntakeSql);
            explain.Parameters.AddWithValue("$p", 1);
            explain.Parameters.AddWithValue("$before", long.MaxValue);
            explain.Parameters.AddWithValue("$limit", DeferredIntakeEntities);
            using var reader = explain.ExecuteReader();
            var details = new List<string>();
            while (reader.Read()) details.Add(reader.GetString(3));
            Require(details.Any(detail => detail.Contains("planning_deferred_pair_version", StringComparison.Ordinal)) &&
                details.All(detail => !detail.Contains("TEMP B-TREE", StringComparison.Ordinal)),
                "the intake scan is one bounded seek on the pair/version/root/id index, never a sort of the pair");
            var columns = new List<string>();
            using (var indexInfo = StoreSchema.Command(connection, null,
                "SELECT name FROM pragma_index_info('planning_deferred_pair_version') ORDER BY seqno;"))
            using (var indexReader = indexInfo.ExecuteReader())
                while (indexReader.Read()) columns.Add(indexReader.GetString(0));
            Require(columns.SequenceEqual(new[] { "pair_no", "version", "root_no", "object_id" }),
                "the intake index has the exact pair/version/root/id column order that orders the page");
        }
        finally { DeleteFixture(folder); }
    }

    private static async Task DeferredIntakePreviewAsync()
    {
        var folder = FixtureFolder("deferred-intake-preview");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Anchor", "same");
            fx.B.Seed("Anchor", "same");
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System);
            await FirstSyncAsync(run, fx);
            // A real persisted backlog. No protection is active, so preview planning cannot
            // legitimately re-defer anything: any version change would be intake rotation.
            store.DeferPlanning(fx.Pair.PairId, Enumerable.Range(0, 2400).Select(n =>
                (fx.Pair.B.RootId, new ItemState($"Queued/File-{n:D4}", ItemKind.File, $"queued-{n:D4}", null,
                    new string('a', 64), 1, DateTimeOffset.UnixEpoch, ItemFlags.None))));
            var before = DeferredVersions(folder);
            var fullA = fx.A.FullCalls;
            var fullB = fx.B.FullCalls;
            await run.PreviewIncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(DeferredVersions(folder).OrderBy(entry => entry.Key).SequenceEqual(before.OrderBy(entry => entry.Key)),
                "a production read-only preview never rotates the bounded deferred page");
            Require(fx.A.FullCalls == fullA && fx.B.FullCalls == fullB,
                "a deferred-only preview stays on the delta path, never an unrelated full scan");
        }
        finally { DeleteFixture(folder); }
    }

    private static async Task DeferredIntakeProductionRunAsync()
    {
        var folder = FixtureFolder("deferred-intake-run");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            const int backlog = 2400;
            fx.A.Seed("Anchor", "same");
            fx.B.Seed("Anchor", "same");
            for (var n = 0; n < backlog; n++)
            {
                fx.A.Seed($"Folder/File-{n:D4}.txt", "original");
                fx.B.Seed($"Folder/File-{n:D4}.txt", "original");
            }
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System);
            await FirstSyncAsync(run, fx);

            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(fx.Pair.A.RootId, "cursor", 1, false, "synthetic intake gap");
            for (var n = 0; n < backlog; n++) fx.B.Seed($"Folder/File-{n:D4}.txt", "changed during gap");
            // Bulk inventory work, not provider deltas: deltas also queue their local proof
            // dependencies as urgent. Keep this fixture testing the original bulk intake bound.
            await run.RequestRescanAsync([fx.Pair.B.RootId], CancellationToken.None);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.UrgentPending(fx.Pair.PairId) == 0, "bulk rescan work carries no user-delta provenance");
            Require(store.CountDeferredPlanning(fx.Pair.PairId) == backlog,
                "the production run queued one more-than-one-page backlog of real deferred entities");

            var cutoff = DeferredVersions(folder).Values.Max() + 1;
            var fullA = fx.A.FullCalls;
            var fullB = fx.B.FullCalls;
            var watch = Stopwatch.StartNew();
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            watch.Stop();
            var after = DeferredVersions(folder);
            var admitted = after.Values.Count(version => version > cutoff);
            Require(admitted == DeferredIntakeEntities,
                "the production run admits exactly one bounded deferred page, not the whole backlog");
            Require(after.Count == backlog,
                "the unadmitted remainder stays queued for the next run (no work is dropped)");
            Require(fx.A.FullCalls == fullA && fx.B.FullCalls == fullB,
                "deferred-only planning input stays on the delta path, never an unrelated full inventory");
            Console.WriteLine($"deferred intake run: batch={admitted} remainder={after.Count - admitted} " +
                $"elapsed={watch.ElapsedMilliseconds}ms full-scans=0");
        }
        finally { DeleteFixture(folder); }
    }

    private static PairDef IntakePair() => new("pair", "Pair A",
        new("local", SyncProviders.Local, null, "/local", "local"),
        new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);

    private static IEnumerable<(string Root, ItemState Item)> IntakeBacklog(PairDef pair, int start, int count) =>
        Enumerable.Range(start, count).Select(n => (pair.B.RootId, IntakeItem($"object-{n:D5}")));

    private static ItemState IntakeItem(string objectId) =>
        new($"Pending/{objectId}", ItemKind.File, objectId, null, new string('a', 64),
            1, DateTimeOffset.UnixEpoch, ItemFlags.None);

    private static void SeedRoot(SqliteStateStore store, RootDef root, Side side, IReadOnlyList<ItemState> items)
    {
        var gen = store.BeginGeneration(root.RootId, "fixture");
        store.WriteSnapshot(new(root.RootId, side, gen, "cursor", items, []));
        store.CompleteGeneration(gen, "cursor", true);
    }

    private static Dictionary<string, long> DeferredVersions(string folder)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
        connection.Open();
        using var command = StoreSchema.Command(connection, null, "SELECT object_id,version FROM planning_deferred;");
        using var reader = command.ExecuteReader();
        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read()) versions[reader.GetString(0)] = reader.GetInt64(1);
        return versions;
    }
}
