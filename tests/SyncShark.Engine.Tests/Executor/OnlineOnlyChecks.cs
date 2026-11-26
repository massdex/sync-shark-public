using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>An online-only PC file and whatever Dropbox holds at its path are left alone: no move, removal or transfer.</summary>
public static class OnlineOnlyChecks
{
    private static Entry Stub(string path, string id) => ItemChecks.File(Side.Local, path, 9, id) with { Skip = nameof(TextKey.SkipOnlineOnly) };
    private static Task None(SyncedEntry? _, Scope __, Scope? ___, CancellationToken ____) => Task.CompletedTask;

    // A stub that replaces a bound PC folder is not that folder's deletion: the Dropbox folder and its children stay.
    public static async Task StubReplacingFolderAsync()
    {
        var pair = ItemChecks.Pair(); var paths = new PathScope(pair, [pair], DriveType.Fixed);
        using var store = new InMemoryStateStore(); var pc = new FakeSide(Side.Local); var db = new FakeSide(Side.Dropbox); var queue = new WorkQueue();
        var folder = ItemChecks.File(Side.Dropbox, "X", 0, "db-x", true); var child = ItemChecks.File(Side.Dropbox, "X/c", 1, "db-c");
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(ItemChecks.File(Side.Local, "X", 0, "pc-x", true), folder), null, null, default);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(ItemChecks.File(Side.Local, "X/c", 1, "pc-c"), child), null, null, default);
        pc.Seed(Stub("X", "pc-stub"), []); db.Seed(folder, []); db.Seed(child, [1]);
        await ItemSync.ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, new Change(Side.Local, ChangeKind.Deleted, "X", null, "pc-x", null, false, true, 5, JournalId: 1),
            pc, db, store, ItemChecks.Population(pair, paths, pc, db, store), ItemChecks.Hop(pair, paths, queue, pc, db, store), default, queue.Held);
        Check.Require(db.Mutations.Count == 0 && db.Items.ContainsKey("X/c") && pc.Items["X"].Entry.Skip == nameof(TextKey.SkipOnlineOnly),
            "A stub replacing a PC folder deleted the Dropbox folder");
    }

    // Repair does not read Dropbox children under a PC stub as absent on the PC.
    public static Task RepairUnderStubAsync() => global::Massdex.SyncShark.Engine.Tests.Run.RepairChecks.WithRepairStore(async (store, pair, ct) =>
    {
        var pc = new FakeSide(Side.Local); var db = new FakeSide(Side.Dropbox);
        var folder = ItemChecks.File(Side.Dropbox, "X", 0, "db-x", true); var child = ItemChecks.File(Side.Dropbox, "X/c", 1, "db-c");
        await store.FinishRepairAsync(pair.PairId, (0, await store.OweRepairAsync(pair.PairId, Side.Dropbox, "requested", ct)), "{}", ct);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(ItemChecks.File(Side.Local, "X", 0, "pc-x", true), folder), null, null, ct);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(ItemChecks.File(Side.Local, "X/c", 1, "pc-c"), child), null, null, ct);
        pc.Seed(Stub("X", "pc-stub"), []); db.Seed(folder, []); db.Seed(child, [1]);
        var paths = new PathScope(pair, [pair], DriveType.Fixed);
        var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, db, store).WaitAsync(ct);
        await Repair.RunAsync(pair, ItemChecks.Settings, _ => { }, paths, queue, pc, db, store, null, null, ct).WaitAsync(ct);
        Check.Require(db.Mutations.Count == 0 && db.Items.ContainsKey("X/c") && pc.Mutations.Count == 0, "Repair removed Dropbox content under a PC stub");
    });

    // A mirrored folder rename moves only free branches; the stub keeps its PC name.
    public static async Task FolderMoveKeepsStubAsync()
    {
        var pair = ItemChecks.Pair(); var paths = new PathScope(pair, [pair], DriveType.Fixed);
        using var store = new InMemoryStateStore(); var pc = new FakeSide(Side.Local); var db = new FakeSide(Side.Dropbox);
        var la = ItemChecks.File(Side.Local, "A", 0, "la", true); var da = ItemChecks.File(Side.Dropbox, "A", 0, "da", true);
        var lf = ItemChecks.File(Side.Local, "A/f", 1, "lf"); var df = ItemChecks.File(Side.Dropbox, "A/f", 1, "df");
        var saved = SyncedEntries.From(la, da);
        await store.CommitAsync(pair.PairId, null, saved, null, null, default); await store.CommitAsync(pair.PairId, null, SyncedEntries.From(lf, df), null, null, default);
        pc.Seed(la, []); pc.Seed(lf, [1]); pc.Seed(Stub("A/s", "ls"), []);
        var moved = da with { Path = "B" }; db.Seed(moved, []); db.Seed(df with { Path = "B/f" }, [1]); db.Seed(ItemChecks.File(Side.Dropbox, "B/s", 2, "ds"), [2]);
        try { await RowExecution.ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, null, [new(Side.Dropbox, moved, saved)], pc, db, store, (_, _, _, _) => Task.CompletedTask, None, default); }
        catch (ItemFailedException) { } // the kept stub leaves the old folder in place, reported as one failure
        Check.Require(pc.Items.TryGetValue("A/s", out var stub) && stub.Entry.Skip == nameof(TextKey.SkipOnlineOnly) && !pc.Items.ContainsKey("B/s") && pc.Items.ContainsKey("B/f"),
            "A folder rename carried an online-only file or did not move its free branch");
    }

    // A split rename never downloads onto an online-only PC file at the mapped Dropbox name.
    public static async Task SplitSkipsStubTargetAsync()
    {
        var pair = ItemChecks.Pair(); var paths = new PathScope(pair, [pair], DriveType.Fixed);
        using var store = new InMemoryStateStore(); var pc = new FakeSide(Side.Local); var db = new FakeSide(Side.Dropbox);
        var left = ItemChecks.File(Side.Local, "file", 1, "left"); var right = ItemChecks.File(Side.Dropbox, "file", 1, "right"); var saved = SyncedEntries.From(left, right);
        var a = left with { Path = "pc-name" }; var b = right with { Path = "db-name" }; pc.Seed(a, [1]); db.Seed(b, [1]); pc.Seed(Stub("db-name", "stub"), []);
        await store.CommitAsync(pair.PairId, null, saved, null, null, default);
        try { await RowExecution.ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, null, [new(Side.Local, a, saved), new(Side.Dropbox, b, saved)], pc, db, store, (_, _, _, _) => Task.CompletedTask, None, default); }
        catch (Exception e) when (e is ItemFailedException or ChangedUnderneathException) { }
        Check.Require(db.Requests[nameof(ISide.OpenReadAsync)] == 0 && pc.Items["db-name"].Entry.Skip == nameof(TextKey.SkipOnlineOnly),
            "A split rename read Dropbox bytes for an online-only PC target");
    }
}
