using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
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
        await SaveFull(inventory, store);
        var initial = store.Live("root-local");
        store.RequireRehashBatch("root-local", initial.Where(i => i.Kind == ItemKind.File), "original debt reason");
        using (var held = new FileStream(Path.Combine(root, "blocked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await inventory.VerifyPendingAsync(p => p != "excluded.txt", p => touched.Add(p), timeout.Token);
            LocalChecks.Require(touched.Count == 261 && touched.Contains("alias.txt"), "one bounded pass drains more than128 identities and reports every completed alias");
            LocalChecks.Require(store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == 2, "blocked and excluded debts terminate and remain pending");
            LocalChecks.Require(store.RehashReasons("root-local", 300).Any(r => r.Contains("excluded.txt: original debt reason")), "excluded debt retains its diagnostic");
        }
        // All debt is outside the empty original journal delta. Exercise the actual run boundary.
        store.RequireRehashBatch("root-local", initial.Where(i => i.Kind == ItemKind.File), "second pass");
        LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var checkpoint), "drain empty checkpoint");
        var scan = await ScanSideForCheck(store, inventory, root, checkpoint!.Cursor);
        LocalChecks.Require(!scan.Full && scan.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 263 && scan.Paths.Contains("alias.txt"),
            "after-write drain keeps incremental SideScan and touches all previously untargeted paths including aliases");
        LocalChecks.Require(store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == 0 && store.RehashWatermark("root-local") > 0, "all retriable content holds cleared while durable plan obligations remain");
        store.RequireRehashBatch("root-local", initial.Where(i => i.Kind == ItemKind.File), "fallback pass");
        scan = await ScanSideForCheck(store, inventory, root, "invalid-cursor");
        LocalChecks.Require(scan.Full && store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == 0, "full fallback drains after persisted snapshot in same run");

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
        Console.WriteLine("PASS bounded drain, full fallback, incremental touched aliases, blocked/excluded termination, metadata release");
    }

    private static async Task CrashDrainAsync(string folder)
    {
        var root = Path.Combine(folder, "root"); LocalChecks.Seed(root, "first.txt", "same");
        LocalChecks.Require(CreateHardLink(Path.Combine(root, "second.txt"), Path.Combine(root, "first.txt"), IntPtr.Zero), "restart aliases");
        var data = Path.Combine(folder, "store");
        using (var store = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
            await SaveFull(inventory, store);
            store.RequireRehash("root-local", store.Live("root-local", ["first.txt"]).Single(), "crash obligation");
            await inventory.VerifyPendingAsync(_ => true, _ => { }, default);
            LocalChecks.Require(store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == 0, "verified rows are usable by current planning");
            // Simulate process exit immediately after drain, before planning/any execution acknowledgment.
        }
        using (var store = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
            LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var empty), "restart empty journal");
            var scan = await ScanSideForCheck(store, inventory, root, empty!.Cursor);
            LocalChecks.Require(!scan.Full && scan.Paths.Contains("first.txt") && scan.Paths.Contains("second.txt"),
                "restart after drain before planning re-touches every alias on empty journal");
            var boundary = store.RehashWatermark("root-local");
            store.AcknowledgeRehash("root-local", boundary, p => p == "first.txt");
            LocalChecks.Require(store.RehashWatermark("root-local") > 0, "one unplanned or excluded alias retains obligation");
            store.AcknowledgeRehash("root-local", boundary, _ => true);
            LocalChecks.Require(store.RehashWatermark("root-local") == 0, "all settled aliases release obligation");
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
