namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task PausedWalkCatchesUpBeforePlanningAsync(string folder)
    {
        var root = Path.Combine(folder, "root");
        Directory.CreateDirectory(root);
        LocalChecks.Seed(root, "old.txt", "before pause");
        LocalChecks.Seed(root, "d/moved.txt", "moved during pause");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var local = LocalChecks.RootOf(root, "ra");
        var pair = fx.Pair with { A = local };
        store.SavePair(pair);
        LocalInventory Inventory() => new(local, Side.A, store, new HashSet<string>(), Settings, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage);
        var inventory = Inventory();
        var before = await inventory.FullAsync(store.BeginGeneration("ra", "full"), _ => true, null!, CancellationToken.None);
        var old = before.Items.Single(item => item.Path == "old.txt") with { Hash = "known-old-content" };
        inventory.DirectoryCompletedForTest = path => { if (path == root) store.SetPairPaused(pair.PairId, true); };
        using (PairPause.Begin(store, pair.PairId))
        {
            try
            {
                await inventory.FullAsync(store.BeginGeneration("ra", "full"), _ => true, null!, CancellationToken.None);
                throw new InvalidOperationException("Expected pause with a durable root page");
            }
            catch (PairPausedException) { }
        }
        Require(store.LocalWalkPage("ra", 0) is not null, "real journal pause saved the root directory");
        File.Delete(Path.Combine(root, "old.txt"));
        File.Move(Path.Combine(root, "d/moved.txt"), Path.Combine(root, "arrived.txt"));
        LocalChecks.Seed(root, "new.txt", "created during pause");
        store.SetPairPaused(pair.PairId, false);
        inventory = Inventory();
        var walked = new List<string>();
        inventory.DirectoryCompletedForTest = walked.Add;
        fx.Provider.AddInventory(local, inventory);
        var preview = await fx.Run().PreviewFirstSyncAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        var paths = store.Live("ra").Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
        Require(paths.Contains("arrived.txt") && paths.Contains("new.txt") && !paths.Contains("old.txt"),
            "the SAME preview catches move/create/delete during pause before planning");
        Require(walked.Count > 0 && walked[0] == Path.Combine(root, "d") && walked.Count(p => p == Path.Combine(root, "d")) == 1, "catch-up does not need another full walk");
        Require(preview.InventoryCursors.ContainsKey("ra"), "preview retains the catch-up cursor");

        // Isolate the previously synced identity: catch-up supplies complete retained inventory,
        // not explicit deletion evidence. Normal baseline rules must still propagate its absence.
        var peer = old with { ObjectId = "peer-old" };
        var noDeletes = new Dictionary<string, DateTimeOffset?>();
        var rows = Planner.Join([new Baseline(pair.PairId, "old-key", old, peer, 1)],
            store.Live("ra").Where(item => item.Path == "old.txt").ToArray(), [peer], noDeletes, noDeletes,
            new Completeness(true, true, new HashSet<string>(), new HashSet<string>()), false);
        var deletionPlan = Planner.Decide(pair.PairId, 10, rows,
            new PlanOptions(TimeSpan.FromSeconds(2), false,
                new LabelRule("{name} [{side} version {time}]{ext}", "d MMM HH.mm", TimeSpan.Zero, "Local", "Peer"), 10));
        Require(deletionPlan.Actions.Count == 1 && deletionPlan.Actions[0] is
            { Kind: ActionKind.Delete, Target: Side.B, Path: "old.txt", Rule: 6, ObjectKey: "old-key" },
            "catch-up absence plans exactly the normal rule-6 peer deletion, with no extra actions");
        Require(deletionPlan.Notices.Count == 0, "catch-up deletion needs no extra notices");
    }

    internal static async Task LocalPrepareCursorAsync(string folder)
    {
        string retained;
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
        {
            var fx = Build(suppliedStore: store);
            fx.A.Seed("matched.txt", "same");
            fx.B.Seed("matched.txt", "same");
            var initial = await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(initial.InventoryCursors.ContainsKey(fx.Pair.A.RootId),
                "first-sync prepare retains the local journal checkpoint, not only Dropbox's cursor");
            Require(store.Cursor(fx.Pair.A.RootId) is null, "prepare does not prematurely commit the successful local root cursor");
            // The pending checkpoint round-trips independently of the inventory/run instances.
            var cursors = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                System.Text.Json.JsonSerializer.Serialize(initial.InventoryCursors))!;
            var fullBefore = fx.A.FullCalls;
            var resumed = await fx.Run().PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId, null, null, [],
                null, new RecordingProgress(), CancellationToken.None, cursors);
            Require(fx.A.FullCalls == fullBefore && fx.A.DeltaCalls > 0,
                "first-sync resume reads a local delta instead of a full inventory");
            // The real run's invalid-cursor path must call FullAsync, not just set an advisory flag.
            cursors[fx.Pair.A.RootId] = "invalid-journal-gap";
            var recovered = await fx.Run().PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId, null, null, [],
                null, new RecordingProgress(), CancellationToken.None, cursors);
            Require(fx.A.FullCalls == fullBefore + 1 && recovered.Plan.InventoryCursors.ContainsKey(fx.Pair.A.RootId),
                "journal gap performs a full local scan and retains its replacement checkpoint");
            var applied = await fx.Run().ApplyAsync(fx.Pair.PairId, recovered.Plan, new RecordingProgress(), CancellationToken.None);
            retained = recovered.Plan.InventoryCursors[fx.Pair.A.RootId];
            Require(applied.Failed == 0 && store.Cursor(fx.Pair.A.RootId) == retained,
                "successful first sync promotes the local position to its root row");
        }
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        Require(reopened.Cursor("ra") == retained, "local root checkpoint survives SQLite reopen");
    }
}
