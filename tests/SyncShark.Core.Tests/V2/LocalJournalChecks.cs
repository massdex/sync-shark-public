using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    public static async Task RunAsync(string folder, bool scale = true)
    {
        Directory.CreateDirectory(folder);
        await RunChecks.PausedWalkCatchesUpBeforePlanningAsync(Path.Combine(folder, "paused-prepare"));
        await HardLinksAsync(Path.Combine(folder, "hardlinks"));
        await RehashDebtAsync(Path.Combine(folder, "rehash"));
        await RenameCasesAsync(Path.Combine(folder, "renames"));
        if (scale) await PhysicalFolderAsync(Path.Combine(folder, "physical"));
        await RunChecks.LocalPrepareCursorAsync(Path.Combine(folder, "prepare-store"));
        // Require a real usable journal: this focused check must not silently skip the production path.
        var root = Path.Combine(folder, "root");
        Directory.CreateDirectory(root);
        LocalChecks.Seed(root, "edit.txt", "one");
        LocalChecks.Seed(root, "delete.txt", "two");
        LocalChecks.Seed(root, "old/child.txt", "three");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var def = LocalChecks.RootOf(root, "root-local");
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        var gen = store.BeginGeneration(def.RootId, "full");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.CursorAfter.Length > 0 && snapshot.IncompleteSubtrees.Count == 0,
            "non-admin journal checkpoint available; no silent skip");
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(gen, snapshot.CursorAfter, true);

        // There is deliberately NO FileSystemWatcher. Reopen the inventory to model process downtime.
        LocalChecks.Seed(root, "edit.txt", "content edited while watcher absent");
        File.Delete(Path.Combine(root, "delete.txt"));
        Directory.Move(Path.Combine(root, "old"), Path.Combine(root, "renamed"));
        LocalChecks.Seed(root, "new.txt", "born while watcher absent");
        inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        var deltaGen = store.BeginGeneration(def.RootId, "delta");
        var delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, deltaGen, _ => true, CancellationToken.None);
        LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Count == 0,
            "unnamed create/rename resolve without discovery or a full scan");
        LocalChecks.Require(delta.Changes.Any(c => c.Kind == ChangeKind.Upsert && c.Path == "edit.txt" &&
            c.State!.Size == new FileInfo(Path.Combine(root, "edit.txt")).Length), "content edit is observed without directory timestamps");
        LocalChecks.Require(delta.Changes.Any(c => c.Kind == ChangeKind.Delete && c.Path == "delete.txt"),
            "deleted file is observed without watcher records");
        LocalChecks.Require(delta.Changes.Any(c => c.Kind == ChangeKind.Upsert && c.Path == "renamed/child.txt") &&
            delta.Changes.Any(c => c.Kind == ChangeKind.Delete && c.Path == "old"), "folder rename includes old and new subtree");
        LocalChecks.Require(delta.Changes.Any(c => c.Kind == ChangeKind.Upsert && c.Path == "new.txt"), "new root child is discovered");
        store.WriteDelta(delta);
        store.CompleteGeneration(deltaGen, delta.CursorAfter, true);

        // Forced uncertainty cannot become a guessed path or a deletion. Exercise the production resolver hook.
        var objectId = LocalItems.ParseObjectId(store.Live(def.RootId, ["new.txt"]).Single().ObjectId).FileId;
        var uncertain = new ResolveContext(1, 1, _ => null,
            100,
            (_, _, _) => null);
        var resolved = await LocalDeltaResolver.ResolveAsync(
            [new NtfsUsnChange(objectId, 1, 10, 0x100, FileAttributes.Normal, "")], uncertain, CancellationToken.None);
        LocalChecks.Require(resolved is null, "failed parent listing forces full reconciliation, never false absence");

        var checkpoint = new NtfsUsnCheckpoint(11, 100, 200, 0);
        LocalChecks.Require(checkpoint.RequiresFullReconciliation("000000000000000b:99"), "journal trim uses FirstUsn, not just LowestValidUsn");
        LocalChecks.Require(checkpoint.RequiresFullReconciliation("000000000000000c:150"), "journal replacement forces full reconciliation");
        var log = RunLog.Open(Path.Combine(folder, "fallback-log"), "journal", "fallback", false);
        using (log)
        {
            var invalid = await inventory.ChangesSinceAsync("0000000000000000:0", deltaGen + 1, _ => true, CancellationToken.None);
            LocalChecks.Require(invalid.CursorInvalid && invalid.Changes.Count == 0, "journal replacement publishes no changes and requests full scan");
            LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var retained) && retained!.FirstUsn > 0, "real retained journal range available for gap test");
            invalid = await inventory.ChangesSinceAsync($"{retained!.JournalId:x16}:{retained.FirstUsn - 1}", deltaGen + 1, _ => true, CancellationToken.None);
            LocalChecks.Require(invalid.CursorInvalid && invalid.Changes.Count == 0, "journal gap publishes no changes and requests full scan");
        }
        await log.Completion;
        var fallbackText = File.ReadAllText(log.Path!);
        LocalChecks.Require(fallbackText.Contains("journal replaced") && fallbackText.Contains("journal gap: cursor precedes retained range"), "operational nonverbose log explains replacement and gap fallbacks");

        LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var editStart), "hidden-edit checkpoint available");
        var file = Path.Combine(root, "edit.txt");
        var mtime = File.GetLastWriteTimeUtc(file);
        var bytes = File.ReadAllBytes(file);
        bytes[0] ^= 1;
        File.WriteAllBytes(file, bytes);
        File.SetLastWriteTimeUtc(file, mtime);
        var hidden = await inventory.ChangesSinceAsync(editStart!.Cursor, deltaGen + 1, _ => true, CancellationToken.None);
        LocalChecks.Require(!hidden.CursorInvalid && hidden.Changes.Single(c => c.Path == "edit.txt").State!.Hash ==
            DropboxContentHash.Compute(bytes), "USN data edit with restored size/mtime is rehashed, not mistaken for unchanged content");

        if (scale) await MillionRowsAsync(folder, root, store, inventory, def.RootId);
        var folderId = LocalItems.ParseObjectId(store.Live(def.RootId, ["renamed"]).Single().ObjectId).FileId;
        Directory.Move(Path.Combine(root, "renamed"), Path.Combine(root, "moved-again"));
        Directory.CreateDirectory(Path.Combine(root, "renamed"));
        var probe = new NtfsUsnChange(1, folderId, 10, 0x100, FileAttributes.Normal, "");
        var names = new LocalJournalNames(def, store, [probe], 100);
        LocalChecks.Require(names.Find(probe, "renamed", CancellationToken.None) is null,
            "replaced parent identity cannot authorize name lookup or absence");
        Console.WriteLine("v2 local journal checks passed.");
    }

    private static async Task MillionRowsAsync(string folder, string root, SqliteStateStore store,
        LocalInventory inventory, string rootId)
    {
        MillionRowProtection(folder, store, rootId);
        LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var start), "scale checkpoint available");
        LocalChecks.Seed(root, "edit.txt", "one file edited among a million retained rows");
        var before = GC.GetTotalAllocatedBytes(true);
        var gen = store.BeginGeneration(rootId, "delta");
        var delta = await inventory.ChangesSinceAsync(start!.Cursor, gen, _ => true, CancellationToken.None);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        LocalChecks.Require(!delta.CursorInvalid && delta.Changes.Count == 1 && delta.Changes[0].Path == "edit.txt",
            "one file change refreshes only its path at one million rows");
        LocalChecks.Require(allocated < 32L * 1024 * 1024, $"incremental allocation bounded ({allocated:N0} bytes)");
    }

    private static void MillionRowProtection(string folder, SqliteStateStore store, string rootId)
    {
        // Generate on SQLite's side: do not allocate a million ItemState objects in the test process.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(folder, "store", "v2", "state.db") }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<1000000)
                INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags)
                SELECT f.root_no,f.dir_no,'SCALE-'||x,'scale-'||x,f.kind,'synthetic-'||x,1,f.mtime,0
                FROM n CROSS JOIN (SELECT * FROM file_state WHERE name='edit.txt' LIMIT 1) f;
                """;
            command.ExecuteNonQuery();
            command.CommandText = "SELECT count(*) FROM file_state;";
            LocalChecks.Require((long)command.ExecuteScalar()! >= 1000000, "fixture contains one million SQLite rows");
        }
        var held = store.Live(rootId, ["edit.txt"]).Single();
        store.RequireRehash(rootId, held, "million-row lookup measurement");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var managedBefore = GC.GetTotalMemory(false);
        var privateBefore = process.PrivateMemorySize64;
        var lookupAllocation = GC.GetTotalAllocatedBytes(true);
        var lookupWatch = System.Diagnostics.Stopwatch.StartNew();
        for (var n = 0; n < 10_000; n++)
        {
            LocalChecks.Require(!store.RehashTouches(rootId, $"folder/child/deep/missing-{n}.txt", false), "unrelated ancestor lookup remains free");
            LocalChecks.Require(store.RehashTouches(rootId, "edit.txt/child/deep", false), "ancestor debt protects descendants");
        }
        lookupWatch.Stop(); process.Refresh();
        Console.WriteLine($"1M rows debt-present protection: 20000 lookups (4 absent + 3 held ancestors per pair), elapsed={lookupWatch.ElapsedMilliseconds}ms, allocated={GC.GetTotalAllocatedBytes(true)-lookupAllocation:N0}, managedDelta={GC.GetTotalMemory(false)-managedBefore:N0}, privateDelta={process.PrivateMemorySize64-privateBefore:N0}");
        LocalChecks.Require(store.CompleteRehash(rootId, held, new string('a', 64)), "release synthetic measurement hold");
        var pair = new PairDef("protection-scale", "Pair A", new(rootId, SyncProviders.Local, null, "/local", "local"),
            new("scale-cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
        store.SavePair(pair);
        var emptyGen = store.BeginGeneration(pair.B.RootId, "fixture");
        store.WriteSnapshot(new(pair.B.RootId, Side.B, emptyGen, "cursor", [], []));
        store.CompleteGeneration(emptyGen, "cursor", true);
        foreach (var mode in new[] { "no-holds", "production-no-holds", "partial-gap", "whole-root-gap" })
        {
            if (mode is "partial-gap" or "whole-root-gap")
            {
                ((ILocalGapStore)store).BeginGap(rootId, "cursor", 1, false, "scale measurement");
                using var connection = new SqliteConnection($"Data Source={SqliteStateStore.DatabasePath(Path.Combine(folder, "store"))};Pooling=False");
                connection.Open();
                StoreSchema.Execute(connection, null, "UPDATE local_gap SET after_object=$after,discovery_done=0;",
                    ("$after", mode == "partial-gap" ? "synthetic-999990" : ""));
            }
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var links = LinkExclusions.Of(store, pair, false);
            if (mode == "production-no-holds")
                links = links.WithRetryProtection(RetryProtection.Create(store, pair.PairId, active));
            var scope = new RunScope(store, _ => true, _ => true, links,
                activeRetries: mode == "production-no-holds" ? active : null,
                restoreProtection: RecoveryRestoreProtection.Create(store, pair.PairId), persistDeferred: true);
            scope.PendingChanges([held.ObjectId], []);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var modeAllocated = GC.GetTotalAllocatedBytes(true);
            var modeManaged = GC.GetTotalMemory(false);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var prepared = scope.LoadFull(pair);
            process.Refresh();
            Console.WriteLine($"1M Prepare {mode}: load-and-prepare-ms={watch.ElapsedMilliseconds}; visible={prepared.A.Count}; deferred={store.CountDeferredPlanning(pair.PairId)}; allocated={GC.GetTotalAllocatedBytes(true) - modeAllocated}; managed-delta={GC.GetTotalMemory(false) - modeManaged}; private={process.PrivateMemorySize64}; peak-working={process.PeakWorkingSet64}");
            GC.KeepAlive(prepared);
            LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) <= 1,
                "one million unchanged hidden entities do not become deferred work");
        }
        var createWatch = System.Diagnostics.Stopwatch.StartNew();
        store.DeferPlanning(pair.PairId, Enumerable.Range(0, 1_000_000)
            .Select(n => (pair.A.RootId, held with { ObjectId = $"volume-{n:D7}", Path = $"Volume/{n}" })));
        Console.WriteLine($"1M deferral creation: elapsed-ms={createWatch.ElapsedMilliseconds}");
        var drained = 0;
        var drainWatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var page in store.DeferredPlanningPages(pair.PairId))
        {
            drained += page.Count;
            store.AcknowledgeDeferredPlanning(pair.PairId, page, _ => true);
        }
        LocalChecks.Require(drained >= 1_000_000 && store.CountDeferredPlanning(pair.PairId) == 0,
            "one streamed drain acknowledges every entity at million-row scale");
        Console.WriteLine($"1M deferral drain: elapsed-ms={drainWatch.ElapsedMilliseconds}; count={drained}");
    }
}
