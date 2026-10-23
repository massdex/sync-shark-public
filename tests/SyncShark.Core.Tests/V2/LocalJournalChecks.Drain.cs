using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    // LocalInventory.VerifyIndexingPassAsync verifies only. Publication is the index owner's step
    // (LocalIndexingRuns.PassAsync selects the ready page and publishes it); these direct fixtures
    // exercise the same owner step explicitly instead of relying on foreground sync.
    private static void PublishReadyIndexWork(SqliteStateStore store, string rootId) =>
        store.PublishIndexPlanning(rootId, store.RehashCandidates(rootId, 512,
            store.RehashWatermark(rootId), needsVerification: false));

    // Bounded intake is 256 entities / 1024 paths per pass. Model several sync passes: count and
    // materialize each admitted page BEFORE acknowledging exactly the returned rows, because
    // acknowledgement either advances the entity's alias slice or retires a fully settled cycle.
    private static (int Entities, HashSet<string> Paths, bool AllAliases) DrainDeferredCoverage(
        SqliteStateStore store, string pairId, Func<string, bool> settled)
    {
        // Count entities by identity, not by summing slices: a large alias set can take several pages.
        var entities = new HashSet<(long Root, string ObjectId)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allAliases = true;
        // A concrete tick cutoff: rotation moves admitted versions to cutoff+1.., so MaxValue would overflow.
        // The next cutoff is past the rotated versions, which need not wait for the wall clock to advance.
        var cutoff = DateTime.UtcNow.Ticks;
        for (var pass = 0; pass < 32; pass++)
        {
            var page = store.AdmitDeferredPlanning(pairId, cutoff);
            if (page.Count == 0) break;
            foreach (var row in page)
            {
                entities.Add((row.Root, row.ObjectId));
                allAliases &= row.AllAliases;
                foreach (var path in row.Paths) paths.Add(path);
            }
            store.AcknowledgeDeferredPlanning(pairId, page, settled);
            cutoff = page[^1].Version + 1;
        }
        return (entities.Count, paths, allAliases);
    }

    private static async Task<(bool Full, IReadOnlyList<string> Paths)> ScanSideForCheck(
        SqliteStateStore store, LocalInventory inventory, string root, string? cursor = null)
    {
        var def = LocalChecks.RootOf(root, "root-local");
        var pair = new PairDef("drain-check", "drain", def,
            new RootDef("unused", SyncProviders.Dropbox, null, "/", "unused"), [], [], true);
        store.SavePair(pair);
        var provider = new FakeEndpointProvider().AddInventory(def, inventory);
        var run = new SyncRun(store, new Executor(store, StorageChecks.Fast, ExecutorChecks.Admission()),
            provider, StorageChecks.Fast, TimeProvider.System);
        var method = typeof(SyncRun).GetMethod("ScanSideAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(run, [pair, Side.A,
            new ProgressThrottle(null, TimeProvider.System, TimeSpan.Zero), CancellationToken.None,
            cursor is null ? null : new Dictionary<string, string> { [def.RootId] = cursor }, false])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return ((bool)result.GetType().GetProperty("Full")!.GetValue(result)!,
            (IReadOnlyList<string>)result.GetType().GetProperty("ChangePaths")!.GetValue(result)!);
    }

    private static async Task DrainAsync(string folder)
    {
        var root = Path.Combine(folder, "root");
        for (var n = 0; n < 260; n++) LocalChecks.Seed(root, $"files/{n:D3}.txt", "aaa");
        LocalChecks.Seed(root, "excluded.txt", "excluded");
        LocalChecks.Seed(root, "blocked.txt", "blocked");
        LocalChecks.Require(CreateHardLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "files/000.txt"), IntPtr.Zero), "drain alias fixture");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.VerificationElapsedForTest = () => TimeSpan.Zero;
        // Publication needs a pair consumer; the durable planning queue is per pair, not global.
        // Save it before the snapshot so its first insert cannot clear the root's committed cursor.
        var check = new PairDef("drain-check", "drain", LocalChecks.RootOf(root, "root-local"),
            new RootDef("unused", SyncProviders.Dropbox, null, "/", "unused"), [], [], true);
        store.SavePair(check);
        await SaveFull(inventory, store);
        var initial = store.Live("root-local");
        store.RequireRehashBatch("root-local", initial.Where(i => i.Kind == ItemKind.File), "original debt reason");
        using (var held = new FileStream(Path.Combine(root, "blocked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // The bounded indexing pass verifies; the index owner then publishes the ready page.
            await inventory.VerifyIndexingPassAsync(p => p != "excluded.txt", timeout.Token);
            PublishReadyIndexWork(store, "root-local");
            var verified = store.Live("root-local").Where(item => item.Kind == ItemKind.File &&
                item.Hash is not null && (item.Flags & ItemFlags.NeedsRehash) == 0)
                .Select(item => item.ObjectId).Distinct(StringComparer.Ordinal).ToArray();
            var published = store.CountDeferredPlanning(check.PairId); // captured before any intake consumes it
            // Foreground sync publication is a no-op: it must neither publish nor touch aliases.
            await inventory.VerifyPendingAsync(p => p != "excluded.txt",
                _ => throw new InvalidOperationException("foreground sync must not touch aliases"),
                timeout.Token);
            LocalChecks.Require(published == verified.Length &&
                store.CountDeferredPlanning(check.PairId) == published,
                "the indexing pass queues every completed entity; foreground publication is a no-op");
            // 260 entities exceed one bounded intake, so several sync passes admit and acknowledge.
            var firstCoverage = DrainDeferredCoverage(store, check.PairId, _ => true);
            LocalChecks.Require(firstCoverage.Entities == verified.Length &&
                verified.SelectMany(id => store.ObjectPaths("root-local", id, 0, 128)).All(firstCoverage.Paths.Contains) &&
                firstCoverage.Paths.Contains("alias.txt") && firstCoverage.AllAliases,
                "repeated bounded admissions cover every completed entity and alias in the durable planning queue");
            LocalChecks.Require(store.CountDeferredPlanning(check.PairId) == 0,
                "the bounded admissions retire the whole published queue when every alias is settled");
            LocalChecks.Require(store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == 2, "blocked and excluded debts terminate and remain pending");
            LocalChecks.Require(store.RehashReasons("root-local", 300).Any(r => r.Contains("excluded.txt: original debt reason")), "excluded debt retains its diagnostic");
        }
        // All debt is outside the empty original journal delta. Exercise the actual run boundary.
        store.RequireRehashBatch("root-local", initial.Where(i => i.Kind == ItemKind.File), "second pass");
        LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var checkpoint), "drain empty checkpoint");
        await inventory.VerifyIndexingPassAsync(default);
        PublishReadyIndexWork(store, "root-local");
        var scan = await ScanSideForCheck(store, inventory, root, checkpoint!.Cursor);
        var settled = store.Live("root-local").Where(item => item.Kind == ItemKind.File && item.Hash is not null &&
            (item.Flags & ItemFlags.NeedsRehash) == 0).Select(item => item.ObjectId).Distinct(StringComparer.Ordinal).ToArray();
        var queued = store.CountDeferredPlanning(check.PairId); // captured before any intake consumes it
        LocalChecks.Require(!scan.Full && queued == settled.Length,
            "after-write drain keeps incremental SideScan and queues every verified entity");
        LocalChecks.Require(store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == 0 && queued > 0,
            "all retriable content holds cleared while durable plan obligations remain");
        // 262 entities exceed one bounded intake; repeated sync passes cover every entity and alias.
        var secondCoverage = DrainDeferredCoverage(store, check.PairId, _ => true);
        LocalChecks.Require(secondCoverage.Entities == settled.Length &&
            settled.SelectMany(id => store.ObjectPaths("root-local", id, 0, 128)).All(secondCoverage.Paths.Contains) &&
            secondCoverage.Paths.Contains("alias.txt") && secondCoverage.AllAliases,
            "repeated bounded admissions cover every after-write verified entity and alias");
        LocalChecks.Require(store.CountDeferredPlanning(check.PairId) == 0,
            "the after-write bounded admissions retire the whole queue when every alias is settled");
        store.RequireRehashBatch("root-local", initial.Where(i => i.Kind == ItemKind.File), "fallback pass");
        var protectedBeforeFallback = store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count;
        var io = inventory.GapIo;
        inventory.GapIo = new VerificationTestIo((_, _, _) => throw new InvalidOperationException("full fallback must not hash"));
        scan = await ScanSideForCheck(store, inventory, root, "invalid-cursor");
        LocalChecks.Require(scan.Full && protectedBeforeFallback > 0 &&
            inventory.LastFallbackReason == "saved cursor cannot be parsed" &&
            store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == protectedBeforeFallback,
            "invalid cursor triggers real full fallback without discharging any unproven content");
        inventory.GapIo = io;
        await inventory.VerifyIndexingPassAsync(default);
        LocalChecks.Require(store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == 0,
            "only subsequent independent content proofs discharge fallback debt");

        store.AcknowledgeRehash("root-local", store.RehashWatermark("root-local"), _ => true);
        LocalChecks.Require(store.RehashCandidates("root-local", 1).Count == 0, "settled aliases acknowledge durable plan obligations");
        var first = store.Live("root-local", ["files/001.txt"]).Single();
        store.RequireRehash("root-local", first, "visible edit");
        var changed = first with { Size = first.Size + 1, Flags = first.Flags | ItemFlags.NeedsRehash | (ItemFlags)128, Hash = new string('a', 64) };
        store.WriteDelta(new Delta("root-local", Side.A, store.BeginGeneration("root-local", "delta"), "", "",
            [new Change(ChangeKind.Upsert, changed.ObjectId, changed.Path, changed, null)], false, []));
        var visible = store.Live("root-local", [first.Path]).Single();
        LocalChecks.Require((visible.Flags & ItemFlags.NeedsRehash) == 0 && (visible.Flags & (ItemFlags)128) != 0 && visible.Hash is null && store.RehashCandidates("root-local", 1).Single().ObjectId == first.ObjectId,
            "matching changed metadata clears debt and stale incoming flag/hash without clearing other flags");
        var alias = store.Live("root-local", ["files/000.txt"]).Single();
        store.RequireRehash("root-local", alias, "alias visible edit");
        var all = store.Live("root-local").Select(i => i.ObjectId == alias.ObjectId ? i with { Size = i.Size + 2 } : i).ToArray();
        store.WriteSnapshot(new Snapshot("root-local", Side.A, store.BeginGeneration("root-local", "full"), "", all, []));
        LocalChecks.Require(store.Live("root-local", ["files/000.txt", "alias.txt"]).All(i => (i.Flags & ItemFlags.NeedsRehash) == 0 && i.Hash is null),
            "snapshot alias metadata edit leaves no sibling flag without entity debt");
        await CrashDrainAsync(Path.Combine(folder, "restart"));
        AcknowledgeChecks(store);
        Console.WriteLine("PASS bounded drain, full fallback, durable planning intake, blocked/excluded termination, metadata release");
    }

    private static async Task CrashDrainAsync(string folder)
    {
        var root = Path.Combine(folder, "root"); LocalChecks.Seed(root, "first.txt", "same");
        LocalChecks.Require(CreateHardLink(Path.Combine(root, "second.txt"), Path.Combine(root, "first.txt"), IntPtr.Zero), "restart aliases");
        var data = Path.Combine(folder, "store");
        using (var store = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.VerificationElapsedForTest = () => TimeSpan.Zero;
            store.SavePair(new PairDef("drain-check", "drain", LocalChecks.RootOf(root, "root-local"),
                new RootDef("unused", SyncProviders.Dropbox, null, "/", "unused"), [], [], true));
            await SaveFull(inventory, store);
            store.RequireRehash("root-local", store.Live("root-local", ["first.txt"]).Single(), "crash obligation");
            // The bounded indexing pass verifies; the index owner then publishes the ready page.
            await inventory.VerifyIndexingPassAsync(default);
            PublishReadyIndexWork(store, "root-local");
            await inventory.VerifyPendingAsync(_ => true,
                _ => throw new InvalidOperationException("foreground sync must not touch aliases"), default);
            // Read-only projection: rotate:false materializes aliases without advancing the queue, so
            // the MaxValue cutoff cannot overflow a rotation.
            var admitted = store.AdmitDeferredPlanning("drain-check", long.MaxValue, rotate: false);
            LocalChecks.Require(store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == 0 &&
                store.RehashPendingCount("root-local") == 0 && store.CountDeferredPlanning("drain-check") == 1 &&
                admitted.Count == 1 && admitted[0].AllAliases &&
                admitted[0].Paths.Contains("first.txt", StringComparer.OrdinalIgnoreCase) &&
                admitted[0].Paths.Contains("second.txt", StringComparer.OrdinalIgnoreCase),
                "verified rows are handed to the durable planning queue with every alias before process exit");
            // Simulate process exit immediately after drain, before planning/any execution acknowledgment.
        }
        using (var store = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.VerificationElapsedForTest = () => TimeSpan.Zero;
            LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var empty), "restart empty journal");
            var scan = await ScanSideForCheck(store, inventory, root, empty!.Cursor);
            // One sync pass admits the oldest page (rotating its version) and settles only one alias.
            var firstPass = store.AdmitDeferredPlanning("drain-check", DateTime.UtcNow.Ticks);
            LocalChecks.Require(!scan.Full && firstPass.Count == 1 && firstPass[0].AllAliases &&
                firstPass[0].Paths.Contains("first.txt", StringComparer.OrdinalIgnoreCase) &&
                firstPass[0].Paths.Contains("second.txt", StringComparer.OrdinalIgnoreCase),
                "restart after drain before planning re-admits the entity with every alias");
            store.AcknowledgeDeferredPlanning("drain-check", firstPass, p => p == "first.txt");
            LocalChecks.Require(store.CountDeferredPlanning("drain-check") == 1,
                "one unplanned or excluded alias retains deferred planning work");
            // The next pass re-admits the current page past the version the previous admission rotated to.
            var secondPass = store.AdmitDeferredPlanning("drain-check", firstPass[0].Version + 1);
            LocalChecks.Require(secondPass.Count == 1 && secondPass[0].AllAliases,
                "the retained entity is re-admitted on the next bounded pass");
            store.AcknowledgeDeferredPlanning("drain-check", secondPass, _ => true);
            LocalChecks.Require(store.CountDeferredPlanning("drain-check") == 0,
                "all settled aliases release deferred planning work");
        }
    }

    private static void AcknowledgeChecks(SqliteStateStore store)
    {
        var rows = Enumerable.Range(0, 260).Select(n => new ItemState($"p/{n:D3}", ItemKind.File,
            $"ack-{n:D3}", null, null, 1, DateTimeOffset.UnixEpoch, ItemFlags.None)).ToArray();
        store.WriteSnapshot(new Snapshot("ack", Side.A, store.BeginGeneration("ack", "full"), "", rows, []));
        store.RequireRehashBatch("ack", rows, "ack test");
        foreach (var row in rows) LocalChecks.Require(store.CompleteRehash("ack", row, new string('a', 64)), "verified ack fixture");
        var boundary = store.RehashWatermark("ack");
        store.AcknowledgeRehash("ack", boundary, p => string.CompareOrdinal(p, "p/128") >= 0);
        LocalChecks.Require(store.RehashCandidates("ack", 300).Count == 128, "retained first128 ack rows do not starve later settled page");
        boundary = store.RehashWatermark("ack");
        store.RequireRehash("ack", rows[0], "fresh obligation after verification");
        LocalChecks.Require(store.CompleteRehash("ack", rows[0], new string('b', 64)), "fresh obligation verified");
        store.AcknowledgeRehash("ack", boundary, _ => true);
        LocalChecks.Require(store.RehashCandidates("ack", 300).Single().ObjectId == rows[0].ObjectId, "old acknowledgment cannot erase newer verified obligation");
        var last = store.RehashWatermark("ack");
        store.AcknowledgeRehash("ack", last, _ => true);
        store.RequireRehash("ack", rows[1], "after empty queue");
        LocalChecks.Require(store.RehashWatermark("ack") > last, "retry token is not reused after queue deletion");
        LocalChecks.Require(store.CompleteRehash("ack", rows[1], new string('a', 64)), "new lifetime obligation verified");
        store.AcknowledgeRehash("ack", last, _ => true);
        LocalChecks.Require(store.RehashWatermark("ack") > last, "stale boundary never acknowledges after queue emptied");
    }

    private static async Task ProtectionScaleAsync(string folder)
    {
        var root = Path.Combine(folder, "root"); LocalChecks.Seed(root, "edit.txt", "one");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.VerificationElapsedForTest = () => TimeSpan.Zero;
        // This measurement needs SQLite state, not a usable USN journal. Keep all
        // fixtures under the caller's folder so it can run on a non-journal volume.
        var snapshot = await inventory.FullAsync(store.BeginGeneration("root-local", "full"), _ => true, null!, default);
        LocalChecks.Require(snapshot.IncompleteSubtrees.Count == 0, "complete protection fixture");
        store.WriteSnapshot(snapshot);
        MillionRowProtection(folder, store, "root-local");
    }

    private static async Task VanishAsync(string folder)
    {
        try
        {
        var root = Path.Combine(folder, "root"); LocalChecks.Seed(root, "survivor.txt", "same");
        var incoming = Path.Combine(folder, "incoming"); Directory.CreateDirectory(incoming);
        LocalChecks.Require(CreateHardLink(Path.Combine(incoming, "alias.txt"), Path.Combine(root, "survivor.txt"), IntPtr.Zero), "vanish alias fixture");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.VerificationElapsedForTest = () => TimeSpan.Zero;
        var snapshot = await SaveFull(inventory, store);
        Directory.Move(incoming, Path.Combine(root, "incoming"));
        inventory.AfterRefreshScan = () => File.Move(Path.Combine(root, "incoming/alias.txt"), Path.Combine(folder, "vanished.txt"));
        var log = RunLog.Open(Path.Combine(folder, "log"), "vanish", "check", false);
        Delta delta;
        using (log) delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
        await log.Completion;
        LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Contains("incoming/alias.txt") &&
            delta.IncompleteSubtrees.Contains("survivor.txt") && delta.Changes.Count == 0 &&
            delta.IncompleteReasons.Values.Any(reason => reason.Contains("metadata open failed for incoming/alias.txt: Windows error 2")) && File.ReadAllText(log.Path!).Contains("metadata open failed for incoming/alias.txt: Windows error 2"),
            "post-refresh disappearance rejects stale alias upsert with exact missing-file reason");
        LocalChecks.Require(store.Live("root-local", ["survivor.txt"]).Count == 1, "vanish race preserves original alias row");
        Console.WriteLine("PASS deterministic post-refresh vanish race");
        }
        finally { RemoveFixture(folder); }
    }
}
