namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationSelectionRows()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "selection-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var stable = fx.A.Seed("stable.txt", "stable");
        var changed = fx.A.Seed("changed.txt", "changed");
        var added = fx.A.Seed("new.txt", "new");
        var orphan = fx.A.Seed("orphan.txt", "orphan");
        var moved = fx.A.Seed("Moved", "", ItemKind.Folder);
        var casing = fx.A.Seed("Case.txt", "case");
        var noticed = fx.A.Seed("noticed.txt", "noticed");
        var retry = fx.A.Seed("retry.txt", "retry");
        var pending = fx.A.Seed("pending.txt", "pending");
        var missing = stable with { Path = "missing.txt", ObjectId = "missing-object" };
        var oldChanged = changed with { Size = changed.Size + 1 };
        ItemState[] oldA = [stable, oldChanged, orphan, moved, casing, noticed, retry, pending, missing];
        var baselineRows = new[] { stable, oldChanged, moved with { Path = "Previous" }, casing, missing };
        var b = baselineRows.Select(item => item with { ObjectId = "other-" + item.ObjectId,
            Path = item.Path == casing.Path ? "case.txt" : item.Path }).ToArray();
        SelectionSnapshot(store, fx.Pair.A, Side.A, oldA);
        SelectionSnapshot(store, fx.Pair.B, Side.B, b);
        for (var i = 0; i < baselineRows.Length; i++) SelectionBaseline(store, fx.Pair, baselineRows[i], b[i]);
        SelectionSnapshot(store, fx.Pair.A, Side.A, oldA);
        store.AddNotices([new(fx.Pair.PairId, 0, NoticeKind.Info, noticed.Path, "Needs attention", "selection-fixture")]);
        store.SaveSyncRetry(fx.Pair.PairId, new(retry.Path, "sync", 1, "retry pending", DateTimeOffset.MaxValue));
        store.RecordIntent(fx.Pair.PairId, new("pending-effect", 4, ActionKind.CopyNew, Side.A,
            pending.Path, null, pending, pending, false, null));
        var counts = await fx.Run().SelectAsync(fx.Pair, Side.A,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), "selection",
            new ProgressThrottle(new RecordingProgress(), TimeProvider.System, TimeSpan.Zero), default);
        var work = store.ReconcilePage("selection:work", Side.A, "", "", 256).Select(item => item.Path).ToHashSet();
        Require(work.SetEquals([changed.Path, added.Path, orphan.Path, moved.Path, casing.Path, missing.Path]),
            "selection includes changed, new, missing and amendment rows without unchanged accounted items");
        Require(counts.Enumerated == 9 && counts.Changed == 4 && counts.New == 1 && counts.Missing == 1 && counts.Unaffected == 4,
            "selection counts partition observed items and separately count missing rows");
        Require(counts.Orphan == 1 && counts.Moved == 1 && counts.Case == 1,
            "S-orphan, S-moved folders and S-case each have an explicit selection reason");
        Require(store.Live(fx.Pair.A.RootId, [changed.Path]).Single() == oldChanged && store.Cursor(fx.Pair.A.RootId) == "selection-cursor",
            "selection changes neither live state nor the durable cursor");
        Require(store.ReconcilePage("selection:seen", Side.A, "", "", 256).Count == 0,
            "selection clears its seen session after missing detection");
        var scoped = await fx.Run().SelectAsync(fx.Pair, Side.A,
            new(null, new ReconciliationScope([orphan.Path], []), ReconciliationTrigger.JournalReplaced), "scoped",
            new ProgressThrottle(new RecordingProgress(), TimeProvider.System, TimeSpan.Zero), default);
        Require(scoped.Enumerated == 1 && scoped.Orphan == 1 && scoped.Missing == 0 &&
            store.ReconcilePage("scoped:work", Side.A, "", "", 256).Single().Path == orphan.Path,
            "an exact selection scope does not select unrelated missing or changed paths");
    }

    private static async Task ReconciliationDropboxSelectionRows()
    {
        using var wire = new DropboxWire();
        wire.SeedFile("rev.txt", "revision");
        wire.SeedFile("hash.txt", "content");
        wire.SeedFile("stable.txt", "stable");
        using var http = new HttpClient(wire);
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "selection-dropbox-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, DropboxWire.RootId);
        var root = fx.Pair.B with { Provider = SyncProviders.Dropbox, RootPath = "/Pilot", RootObjectId = DropboxWire.RootId };
        var pair = fx.Pair with { B = root };
        store.SavePair(pair);
        var inventory = new DropboxInventory(root, Side.B, client, store, pair.PairId);
        fx.Provider.AddInventory(root, inventory);
        var snapshot = await inventory.FullAsync(0, _ => true, SilentProgress, default);
        var old = snapshot.Items.Select(item => item.Path == "rev.txt" ? item with { VersionId = "previous-revision" } :
            item.Path == "hash.txt" ? item with { Hash = new string('a', 64) } : item).ToArray();
        foreach (var item in old) SelectionBaseline(store, pair, item with { ObjectId = "other-" + item.ObjectId }, item);
        SelectionSnapshot(store, root, Side.B, old);
        var counts = await fx.Run().SelectAsync(pair, Side.B,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.DropboxReset), "dropbox-selection",
            new ProgressThrottle(new RecordingProgress(), TimeProvider.System, TimeSpan.Zero), default);
        Require(store.ReconcilePage("dropbox-selection:work", Side.B, "", "", 256).Select(item => item.Path)
            .ToHashSet().SetEquals(["rev.txt", "hash.txt"]), "Dropbox selection uses revision or content hash, not metadata alone");
        Require(counts.Changed == 2 && counts.New == 0 && counts.Missing == 0,
            "Dropbox selection reports each changed provider row once");
    }

    private static void SelectionSnapshot(SqliteStateStore store, RootDef root, Side side, ItemState[] items)
    {
        var gen = store.BeginGeneration(root.RootId, "full");
        store.WriteSnapshot(new(root.RootId, side, gen, "selection-cursor", items, []));
        store.CompleteGeneration(gen, "selection-cursor", true);
    }

    private static void SelectionBaseline(SqliteStateStore store, PairDef pair, ItemState a, ItemState b)
    {
        var action = new SyncAction("selection-" + a.ObjectId, 3, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, a.ObjectId);
        store.RecordIntent(pair.PairId, action);
        store.CommitResult(pair.PairId, 1, action, new(action.ActionId, Outcome.Done, a, b, null, null));
    }
}
