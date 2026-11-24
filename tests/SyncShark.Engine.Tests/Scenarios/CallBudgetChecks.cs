using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Executor;
using Massdex.SyncShark.Engine.Tests.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>A settled unchanged Repair must not re-read or transfer individual unchanged files at either regression size.</summary>
public static class CallBudgetChecks
{
    public static async Task UnchangedAsync(int size)
    {
        var pair = ItemChecks.Pair(); var pc = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
        using var store = new InMemoryStateStore();
        await store.SavePairAsync(pair, default); await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
        for (int i=0;i<size;i++)
        {
            string path = $"file-{i}"; var local = ItemChecks.File(Side.Local,path,1,$"local-{i}"); var remote = ItemChecks.File(Side.Dropbox,path,1,$"cloud-{i}");
            pc.Seed(local,[1]); cloud.Seed(remote,[1]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(local,remote),null,null,default);
        }
        await store.OweRepairAsync(pair.PairId,Side.Local,"requested",default);
        long before = store.Requests[nameof(IStore.CommitAsync)];
        var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,default);
        Check.Require((await store.ReadRepairAsync(pair.PairId,Side.Local,default)).Reason==null,"Repair did not settle the requested full comparison");
        foreach (var side in new[]{pc,cloud})
        {
            Check.Require(side.Requests[nameof(ISide.ReadAllAsync)]>0,"Repair skipped a side’s acquisition");
            Check.Require(side.Items.Count == size && side.Items.Values.All(v => v.Bytes.SequenceEqual(new byte[]{1})), "Unchanged file modified");
            foreach (var op in new[]{nameof(ISide.OpenReadAsync),nameof(ISide.ContentHashAsync),nameof(ISide.ReadOneAsync),nameof(ISide.PublishAsync),nameof(ISide.DeleteAsync),nameof(ISide.MoveAsync)})
                Check.Require(side.Requests[op] == 0, $"{side.Side} {op} per unchanged item");
        }
        Check.Require(store.Requests[nameof(IStore.CommitAsync)] == before && store.Records.Count == size && store.OpenNotices.Count == 0, "Unchanged settlement wrote state");
    }

    // ---- Budget helpers ----------------------------------------------------------------------------------
    // The involved set is fixed and the unchanged population varies with `size`; every assertion below is a
    // constant independent of `size`, so the two registry sizes (5,110 and 20,440) are the two-size comparison.
    private static PathScope Paths(PairConfig pair) => new(pair,[pair],DriveType.Fixed);

    /// <summary>A pair at steady state: a finished Repair plus `unchanged` settled identical files on both sides.</summary>
    private static async Task<(InMemoryStateStore Store, FakeSide Pc, FakeSide Cloud, long Commits)> SteadyAsync(PairConfig pair, int unchanged)
    {
        var store = new InMemoryStateStore(); var pc = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair, default);
        await store.FinishRepairAsync(pair.PairId, (0, await store.OweRepairAsync(pair.PairId, Side.Dropbox, "requested", default)), "{}", default);
        for (int i = 0; i < unchanged; i++)
        {
            string path = $"unchanged-{i}";
            var a = ItemChecks.File(Side.Local, path, 1, $"ul-{i}");
            var b = ItemChecks.File(Side.Dropbox, path, 1, $"ur-{i}");
            pc.Seed(a, [1]); cloud.Seed(b, [1]);
            await store.CommitAsync(pair.PairId, null, SyncedEntries.From(a, b), null, null, default);
        }
        return (store, pc, cloud, store.Requests[nameof(IStore.CommitAsync)]);
    }

    private static void Boundary(FakeSide side) =>
        side.IdleBoundary = new ChangePage([], "boundary", new JournalPosition(1, 1)) { CaughtUp = true };

    /// <summary>One change through the real observe/decide/apply path with a no-op old-binding compare and empty held input.</summary>
    private static Task ApplyFile(PairConfig pair, PathScope paths, Change change, FakeSide pc, FakeSide cloud, InMemoryStateStore store,
        Func<(JournalPosition? Local, string? Dropbox), bool>? covers = null) =>
        ItemSync.ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, change, pc, cloud, store,
            static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default,
            static _ => Array.Empty<Change>(), coversThrough: covers);

    private static long Transfer(FakeSide side) =>
        new[]{nameof(ISide.OpenReadAsync),nameof(ISide.ContentHashAsync),nameof(ISide.StageAsync),
              nameof(ISide.PublishAsync),nameof(ISide.DeleteAsync),nameof(ISide.MoveAsync)}
            .Sum(op => side.Requests[op]);

    /// <summary>Bytes actually moved: content reads, stages and publications (TR in section 7), not decision reads.</summary>
    private static long ContentTransfer(FakeSide side) =>
        new[]{nameof(ISide.OpenReadAsync),nameof(ISide.StageAsync),nameof(ISide.PublishAsync)}
            .Sum(op => side.Requests[op]);

    private static void Exact(long actual, long expected, string what) =>
        Check.Require(actual == expected, $"Budget: {what} expected {expected} got {actual}");

    private static void AtMost(long actual, long bound, string what) =>
        Check.Require(actual <= bound, $"Budget: {what} expected <= {bound} got {actual}");

    // ---- Changed-run paths -------------------------------------------------------------------------------

    public static async Task UnchangedAutosyncAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, commits) = await SteadyAsync(pair, size);
        var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, cloud, store);
        var idle = new ChangePage([], "cursor", new JournalPosition(1, 1)) { CaughtUp = true };
        await AutoSync.ApplyPageAsync(pair, ItemChecks.Settings, _ => { }, paths, queue, Side.Dropbox, idle, pc, cloud, store, default);
        Exact(Transfer(pc) + Transfer(cloud), 0, "unchanged autosync per-item provider work");
        Exact(store.Requests[nameof(IStore.CommitAsync)] - commits, 0, "unchanged autosync record writes");
    }

    public static async Task ChangedUploadDirectAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, commits) = await SteadyAsync(pair, size);
        const string path = "item";
        var local = ItemChecks.File(Side.Local, path, 2, "pc-item");
        pc.Seed(local, [2]);
        await ApplyFile(pair, paths, new Change(Side.Local, ChangeKind.Changed, path, null, local.Id, local, true, false), pc, cloud, store);
        AtMost(pc.Requests[nameof(ISide.OpenReadAsync)], 1, "upload content read");
        AtMost(pc.Requests[nameof(ISide.ContentHashAsync)], 1, "upload hash read");
        AtMost(cloud.Requests[nameof(ISide.StageAsync)], 1, "upload stage");
        AtMost(cloud.Requests[nameof(ISide.PublishAsync)], 1, "upload publish");
        Exact(cloud.Requests[nameof(ISide.DeleteAsync)] + cloud.Requests[nameof(ISide.MoveAsync)], 0, "upload extra mutations");
        Check.Require(cloud.Items.TryGetValue(path, out var up) && up.Bytes.SequenceEqual(new byte[] { 2 }), "Direct upload did not publish the new bytes");
        AtMost(store.Requests[nameof(IStore.CommitAsync)] - commits, 1, "upload record write");
    }

    // STOPPED: the session/direct split and the per-upload write-request count live in the real Dropbox writer
    // (Dropbox/DropboxWriter.cs); FakeSide models one Stage + one Publish and cannot count session content writes.
    // Needs a double change (a session-aware fake), so it is left for the double owner.
    public static Task ChangedUploadSessionAsync(int size) => throw new NotImplementedException();

    public static async Task UploadFolderReplacementAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string path = "item";
        var folder = ItemChecks.File(Side.Dropbox, path, 0, "cd-folder", folder: true);
        var oldLocal = ItemChecks.File(Side.Local, path, 0, "pl-folder", folder: true);
        cloud.Seed(folder, []); pc.Seed(oldLocal, []);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(oldLocal, folder), null, null, default);
        Boundary(pc); Boundary(cloud);
        var replacement = ItemChecks.File(Side.Local, path, 3, "pl-folder");
        pc.Items[path] = (replacement, [3]);
        long before = store.Requests[nameof(IStore.CommitAsync)];
        await ApplyFile(pair, paths, new Change(Side.Local, ChangeKind.Changed, path, null, replacement.Id, replacement, true, false),
            pc, cloud, store, covers: _ => true);
        AtMost(pc.Requests[nameof(ISide.OpenReadAsync)], 1, "replacement content read");
        AtMost(cloud.Requests[nameof(ISide.DeleteAsync)], 1, "replacement folder removal");
        AtMost(cloud.Requests[nameof(ISide.PublishAsync)], 1, "replacement publish");
        Check.Require(cloud.Items.TryGetValue(path, out var final) && !final.Entry.IsFolder, "Replacement did not leave a file");
        AtMost(store.Requests[nameof(IStore.CommitAsync)] - before, 2, "replacement record writes");
    }

    public static async Task ChangedDownloadAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string path = "item";
        var oldLocal = ItemChecks.File(Side.Local, path, 1, "pl-item");
        var oldCloud = ItemChecks.File(Side.Dropbox, path, 1, "cd-item");
        pc.Seed(oldLocal, [1]); cloud.Seed(oldCloud, [1]);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(oldLocal, oldCloud), null, null, default);
        Boundary(pc); Boundary(cloud);
        var changed = oldCloud with { ContentHash = Hash(2), Modified = oldCloud.Modified.AddSeconds(1) };
        cloud.Items[path] = (changed, [2]);
        long before = store.Requests[nameof(IStore.CommitAsync)];
        await ApplyFile(pair, paths, new Change(Side.Dropbox, ChangeKind.Changed, path, null, changed.Id, changed, false, false),
            pc, cloud, store, covers: _ => true);
        AtMost(cloud.Requests[nameof(ISide.OpenReadAsync)], 1, "download content read");
        AtMost(pc.Requests[nameof(ISide.StageAsync)], 1, "download stage");
        AtMost(pc.Requests[nameof(ISide.PublishAsync)], 1, "download publish");
        Exact(pc.Requests[nameof(ISide.MoveAsync)], 0, "download move");
        AtMost(store.Requests[nameof(IStore.CommitAsync)] - before, 1, "download record write");
    }

    public static async Task SameSizeHashDecisionAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string path = "item";
        var oldLocal = ItemChecks.File(Side.Local, path, 1, "pl-item");
        var oldCloud = ItemChecks.File(Side.Dropbox, path, 1, "cd-item");
        pc.Seed(oldLocal, [1]); cloud.Seed(oldCloud, [1]);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(oldLocal, oldCloud), null, null, default);
        // Same size, same id, a later stamp: only a content hash can decide it changed.
        var edited = oldLocal with { Modified = oldLocal.Modified.AddSeconds(1) };
        pc.Items[path] = (edited, [3]);
        await ApplyFile(pair, paths, new Change(Side.Local, ChangeKind.Changed, path, null, edited.Id, edited, true, false), pc, cloud, store);
        AtMost(pc.Requests[nameof(ISide.ContentHashAsync)], 2, "same-size decision hash reads");
        AtMost(pc.Requests[nameof(ISide.OpenReadAsync)], 1, "same-size changed content read");
        AtMost(cloud.Requests[nameof(ISide.PublishAsync)], 1, "same-size changed publish");
        Exact(pc.Requests[nameof(ISide.MoveAsync)], 0, "same-size move");
    }

    public static async Task MoveWithoutTransferAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string from = "item"; const string to = "renamed";
        var oldLocal = ItemChecks.File(Side.Local, from, 1, "pl-item");
        var oldCloud = ItemChecks.File(Side.Dropbox, from, 1, "cd-item");
        pc.Seed(oldLocal, [1]); cloud.Seed(oldCloud, [1]);
        var binding = SyncedEntries.From(oldLocal, oldCloud);
        await store.CommitAsync(pair.PairId, null, binding, null, null, default);
        // The cloud identity moved; the PC identity stayed.
        cloud.Items.Remove(from);
        var moved = oldCloud with { Path = to };
        cloud.Seed(moved, [1]);
        long before = store.Requests[nameof(IStore.CommitAsync)];
        await ItemSync.ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, null, [new RenameCandidate(Side.Dropbox, moved, binding)],
            pc, cloud, store, static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default,
            static _ => Array.Empty<Change>());
        Exact(ContentTransfer(pc) + ContentTransfer(cloud), 0, "move content reads/transfers");
        AtMost(cloud.Requests[nameof(ISide.MoveAsync)], 1, "move request");
        AtMost(store.Requests[nameof(IStore.CommitAsync)] - before, 1, "move record write");
    }

    // ---- Echoes ------------------------------------------------------------------------------------------

    public static async Task CloudEntryEchoAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string path = "item";
        var local = ItemChecks.File(Side.Local, path, 1, "pl-item");
        var saved = ItemChecks.File(Side.Dropbox, path, 1, "cd-item");
        pc.Seed(local, [1]); cloud.Seed(saved, [1]);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(local, saved), null, null, default);
        var echo = saved with { Revision = "r2" };
        cloud.Items[path] = (echo, [1]);
        long before = store.Requests[nameof(IStore.CommitAsync)];
        await ApplyFile(pair, paths, new Change(Side.Dropbox, ChangeKind.Changed, path, null, echo.Id, echo, false, false), pc, cloud, store);
        Exact(Transfer(pc) + Transfer(cloud), 0, "cloud echo per-item provider work");
        Exact(store.Requests[nameof(IStore.CommitAsync)] - before, 0, "cloud echo record writes");
    }

    public static async Task CloudTombstoneEchoAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, commits) = await SteadyAsync(pair, size);
        const string path = "never-bound";
        await ApplyFile(pair, paths, new Change(Side.Dropbox, ChangeKind.Deleted, path, null, null, null, false, false), pc, cloud, store);
        Exact(Transfer(pc) + Transfer(cloud), 0, "tombstone echo per-item provider work");
        Exact(store.Requests[nameof(IStore.CommitAsync)] - commits, 0, "tombstone echo record writes");
    }

    public static async Task PcPublicationEchoAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string path = "item";
        var local = ItemChecks.File(Side.Local, path, 1, "pl-item");
        var saved = ItemChecks.File(Side.Dropbox, path, 1, "cd-item");
        pc.Seed(local, [1]); cloud.Seed(saved, [1]);
        // One record carries the local publication boundary my own write produced.
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(local, saved), new JournalPosition(7, 10), null, default);
        long before = store.Requests[nameof(IStore.CommitAsync)];
        await ApplyFile(pair, paths, new Change(Side.Local, ChangeKind.Changed, path, null, local.Id, local, true, false, 5UL, JournalId: 7UL), pc, cloud, store);
        Exact(Transfer(pc) + Transfer(cloud), 0, "pc publication echo per-item provider work");
        Exact(store.Requests[nameof(IStore.CommitAsync)] - before, 0, "pc publication echo record writes");
    }

    public static async Task PcUnprovedContentChangeAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string path = "item";
        var local = ItemChecks.File(Side.Local, path, 1, "pl-item");
        var saved = ItemChecks.File(Side.Dropbox, path, 1, "cd-item");
        pc.Seed(local, [1]); cloud.Seed(saved, [1]);
        // No publication boundary: an unproved-stamp entry must read its content once more to decide.
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(local, saved), null, null, default);
        await ApplyFile(pair, paths, new Change(Side.Local, ChangeKind.Changed, path, null, local.Id, local, true, false), pc, cloud, store);
        AtMost(pc.Requests[nameof(ISide.ContentHashAsync)], 1, "unproved content hash read");
        AtMost(pc.Requests[nameof(ISide.OpenReadAsync)], 1, "unproved content read");
    }

    // First run: a file whose size and modification second match on both sides costs one record write per item and
    // zero content transfers (PLAN 7 "First run" row). First-run context comes from the Repair acquisition, which
    // harness-f1 now implements on the double (BeginScan/ScanTouched), so no store workaround is needed.
    public static async Task FirstSyncStampMatchAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var pc = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
        using var store = new InMemoryStateStore();
        await store.SavePairAsync(pair, default); // no completed Repair: first run
        for (int i = 0; i < size; i++)
        {
            string p = $"file-{i}";
            var a = ItemChecks.File(Side.Local, p, 1, $"local-{i}");
            var b = ItemChecks.File(Side.Dropbox, p, 1, $"cloud-{i}");
            pc.Seed(a, [1]); cloud.Seed(b, [1]);
        }
        var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, cloud, store);
        long before = store.Requests[nameof(IStore.CommitAsync)];
        await Repair.RunAsync(pair, ItemChecks.Settings, _ => { }, paths, queue, pc, cloud, store, null, null, default);
        Exact(store.Requests[nameof(IStore.CommitAsync)] - before, size, "first-run matched record writes");
        foreach (var side in new[] { pc, cloud })
        {
            Check.Require(side.Requests[nameof(ISide.ReadAllAsync)] > 0, "First run skipped a side");
            foreach (var op in new[] { nameof(ISide.OpenReadAsync), nameof(ISide.StageAsync), nameof(ISide.PublishAsync) })
                Exact(side.Requests[op], 0, $"{side.Side} {op} per matched first-run item");
            Check.Require(side.Items.Count == size, "First run altered a matched item");
        }
        Check.Require(store.Records.Count == size, "First run did not bind every matched item");
    }

    // STOPPED: the first-run PC-only probe needs a name that is eligible in the Dropbox deleted-name history and the
    // exact get_metadata -> list_revisions -> get_metadata-by-id chain. The double models the spent mask and the
    // deleted-name enumeration but not the three per-path provider reads of the real deletion-evidence adapter
    // (Dropbox/DeletedHistory.cs), whose contract is already proven by Dropbox/DeletedHistoryChecks. Needs a
    // history-backed first-run fixture.
    public static Task FirstSyncDeletedNameProbeAsync(int size) => throw new NotImplementedException();

    // A full Repair after a gap must not read or transfer unchanged files, in any operation. The provider-call budget
    // is a per-item ISide count, so the parity-proven in-memory double is used (R93); SQL-statement counts, which must
    // use the real Store, are the separate PerPhaseSqlAndRowCounts COUNTER case.
    public static async Task RepairAfterGapAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        // A gap the reader did not cover forces a full two-sided comparison rather than a replay.
        await store.OweRepairAsync(pair.PairId, Side.Local, "requested", default);
        long before = store.Requests[nameof(IStore.CommitAsync)];
        var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, cloud, store);
        await Repair.RunAsync(pair, ItemChecks.Settings, _ => { }, paths, queue, pc, cloud, store, null, null, default);
        Check.Require((await store.ReadRepairAsync(pair.PairId, Side.Local, default)).Reason == null, "Repair did not settle the requested comparison");
        foreach (var side in new[] { pc, cloud })
        {
            Check.Require(side.Requests[nameof(ISide.ReadAllAsync)] > 0, "Repair skipped a side’s acquisition");
            foreach (var op in new[] { nameof(ISide.OpenReadAsync), nameof(ISide.ContentHashAsync), nameof(ISide.PublishAsync), nameof(ISide.DeleteAsync), nameof(ISide.MoveAsync) })
                Exact(side.Requests[op], 0, $"{side.Side} {op} per unchanged item");
        }
        Exact(store.Requests[nameof(IStore.CommitAsync)] - before, 0, "gap Repair record writes for unchanged items");
        Check.Require(store.Records.Count == size, "Gap Repair lost or added a record");
    }

    public static async Task WholeFolderDeleteAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string root = "folder";
        var folder = ItemChecks.File(Side.Dropbox, root, 0, "cd-folder", folder: true);
        var child = ItemChecks.File(Side.Dropbox, "folder/a", 1, "cd-a");
        cloud.Seed(folder, []); cloud.Seed(child, [1]);
        Boundary(pc); Boundary(cloud);
        var result = await new FolderDelete().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, folder, null, Side.Dropbox,
            ActionKind.DeleteDropbox, pc, cloud, store, static _ => Array.Empty<Change>(),
            static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default, coversThrough: _ => true);
        Exact(cloud.Requests[nameof(ISide.DeleteAsync)], 1, "whole folder delete requests");
        Exact(Transfer(pc), 0, "whole folder delete pc work");
        Check.Require(!cloud.Items.ContainsKey(root) && !cloud.Items.ContainsKey("folder/a"), "Whole folder delete left entries");
        Check.Require(result.Outcome is RemovalOutcome.Removed or RemovalOutcome.Unknown, "Whole folder delete unsettled");
    }

    public static async Task PartialFolderDeleteAsync(int size)
    {
        var pair = ItemChecks.Pair() with { Exclusions = [new(Side.Local, "folder/keep", true)] };
        var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string root = "folder";
        var folder = ItemChecks.File(Side.Dropbox, root, 0, "cd-folder", folder: true);
        foreach (var name in new[] { "folder/a", "folder/b", "folder/keep" }) cloud.Seed(ItemChecks.File(Side.Dropbox, name, 1, "cd-" + name), [1]);
        cloud.Seed(folder, []);
        Boundary(pc); Boundary(cloud);
        await new FolderDelete().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, folder, null, Side.Dropbox,
            ActionKind.DeleteDropbox, pc, cloud, store, static _ => Array.Empty<Change>(),
            static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default, coversThrough: _ => true);
        Check.Require(cloud.Items.ContainsKey("folder/keep"), "Partial delete removed the protected child");
        Check.Require(!cloud.Items.ContainsKey("folder/a") && !cloud.Items.ContainsKey("folder/b"), "Partial delete left an unprotected child");
        AtMost(cloud.Requests[nameof(ISide.DeleteAsync)], 3, "partial folder delete requests");
        Exact(Transfer(pc), 0, "partial folder delete pc work");
    }

    // A Dropbox folder delete refused with too-many-files (the real recursive-delete refusal) splits into one
    // delete per child plus the parent's final delete; the refused recursive attempt never reaches the endpoint
    // counter, so the provider attempts are counted at the observation boundary: the refused root first, then only
    // the selected children, then the root once more (child order immaterial). The fan-out is fixed by the fixture's
    // child count, so it does not grow with the unrelated population `size`.
    public static async Task RefusedFolderSplitAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string root = "folder";
        var folder = ItemChecks.File(Side.Dropbox, root, 0, "cd-folder", folder: true);
        cloud.Seed(folder, []);
        var children = new[] { "folder/a", "folder/b" };
        foreach (var name in children) cloud.Seed(ItemChecks.File(Side.Dropbox, name, 1, "cd-" + name), [1]);
        Boundary(pc); Boundary(cloud);
        bool refused = false;
        var attempted = new List<string>();
        var deleting = FolderDeleteChecks.Observe.On(cloud, (op, args) =>
        {
            // Count every provider delete attempt at the observation boundary, including the one this fake then refuses.
            if (op == nameof(ISide.DeleteAsync) && args.Length == 4 && args[0] is Entry victim)
            {
                attempted.Add(victim.Path);
                if (victim.Path == root && !refused)
                {
                    refused = true;
                    throw new ItemFailedException(root, TextKey.FailDropboxTooManyFiles, null) { Disposition = FailureDisposition.Permanent };
                }
            }
            return null;
        });
        var result = await new FolderDelete().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, folder, null, Side.Dropbox,
            ActionKind.DeleteDropbox, pc, deleting, store, _ => [],
            static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default, coversThrough: _ => true);
        Check.Require(result.Outcome == RemovalOutcome.Removed, "Refused folder split did not settle");
        Check.Require(!cloud.Items.ContainsKey(root) && children.All(c => !cloud.Items.ContainsKey(c)), "Refused folder split left an entry");
        // The initial refused root attempt, each selected child exactly once, then the final root removal, in order.
        Check.Require(attempted.Count > 0 && attempted[0] == root && attempted[^1] == root &&
            attempted.Count(p => p == root) == 2 && children.All(c => attempted.Count(p => p == c) == 1),
            "Refused folder split did not try the root, each selected child, then the root again");
        Exact(Transfer(pc), 0, "refused folder split pc work");
    }

    public static async Task PcRecycleNoteAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string root = "folder";
        var folder = ItemChecks.File(Side.Local, root, 0, "pl-folder", folder: true);
        pc.Seed(folder, []); pc.Seed(ItemChecks.File(Side.Local, "folder/a", 1, "pl-a"), [1]);
        Boundary(pc); Boundary(cloud);
        await new FolderDelete().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, folder, null, Side.Local,
            ActionKind.DeleteLocal, pc, cloud, store, static _ => Array.Empty<Change>(),
            static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default, coversThrough: _ => true);
        Exact(pc.Requests[nameof(ISide.DeleteAsync)], 1, "pc recycle requests");
        Exact(Transfer(cloud), 0, "pc recycle cloud work");
        Check.Require(!pc.Items.ContainsKey(root) && pc.History.Any(v => v.Entry.Path == root), "PC folder not recycled into history");
    }

    // A folder move/merge into an existing destination moves the written side's children by identity (no content
    // transfer) and removes only the source. Because the destination already exists, the merge must create nothing
    // (zero-ensure budget); the child move and the source removal are counted separately at the observation boundary,
    // and both are fixed by the fixture so neither grows with the unrelated population `size`.
    public static async Task MoveOrMergeDestinationAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        var source = ItemChecks.File(Side.Local, "old", 0, "source", folder: true);
        var dest = source with { Path = "new", Id = "dest" };
        var child = ItemChecks.File(Side.Local, "old/free", 1, "file");
        var peer = ItemChecks.File(Side.Dropbox, "old/free", 1, "peer");
        pc.Seed(source, []); pc.Seed(dest, []); pc.Seed(child, [1]); cloud.Seed(peer, [1]);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(child, peer), null, null, default);
        Boundary(pc); Boundary(cloud);
        long ensures = 0, moves = 0, deletes = 0;
        var local = FolderDeleteChecks.Observe.On(pc, (op, args) =>
        {
            if (op == nameof(ISide.EnsureFolderAsync)) ensures++;
            else if (op == nameof(ISide.MoveAsync)) moves++;
            else if (op == nameof(ISide.DeleteAsync)) deletes++;
            return null;
        });
        var result = await new FolderMerge().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, source, dest, Side.Local, local, cloud, store,
            _ => [], static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default, coversThrough: _ => true);
        Check.Require(pc.Items.TryGetValue("new/free", out var moved) && moved.Entry.Id == "file", "Move or merge did not move the child by identity");
        Exact(ensures, 0, "existing destination folder ensures");
        Exact(moves, 1, "merge destination child move");
        Exact(deletes, 1, "merge source folder delete");
        Exact(ContentTransfer(pc) + ContentTransfer(cloud), 0, "merge content transfer");
        Check.Require(!pc.Items.ContainsKey("old") && result.Outcome == RemovalOutcome.Removed, "Merge did not settle the source");
    }
    // A selected folder move refused for provider size is driven through the real Effects path: it creates the
    // absent destination exactly once and then moves each child once (subdivision). Counts are fixed by the fixture
    // so neither the ensure nor the per-child fan-out grows with the unrelated population `size`.
    public static async Task MoveRefusalSubdivideAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        var source = ItemChecks.File(Side.Dropbox, "old", 0, "sel", folder: true);
        var peer = ItemChecks.File(Side.Local, "old", 0, "pc-old", folder: true);
        cloud.Seed(source, []); pc.Seed(peer, []);
        var saved = SyncedEntries.From(peer, source);
        await store.CommitAsync(pair.PairId, null, saved, null, null, default);
        cloud.Seed(ItemChecks.File(Side.Dropbox, "old/child", 1, "child"), [1]);
        Boundary(pc); Boundary(cloud);
        // The root move is refused for size; Effects must create the destination and subdivide into child moves.
        cloud.MoveOutcomes.Enqueue(new(RemovalOutcome.NoEffect, source, TextKey.FailDropboxTooManyFiles));
        long ensures = 0, childMoves = 0, deletes = 0;
        var remote = FolderDeleteChecks.Observe.On(cloud, (op, args) =>
        {
            if (op == nameof(ISide.EnsureFolderAsync)) ensures++;
            else if (op == nameof(ISide.MoveAsync) && args[1] is string to && to.StartsWith("new/", StringComparison.Ordinal)) childMoves++;
            else if (op == nameof(ISide.DeleteAsync)) deletes++;
            return null;
        });
        var action = new SyncAction(ActionKind.MoveDropbox,
            new Row(saved.LocalKey, peer, source, saved, [], null, RowContext.Neutral), Target: "new");
        var outcome = await new Effects().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, action, pc, remote, store,
            static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default, _ => [], _ => true);
        Check.Require(outcome == EffectOutcome.Applied, "Refused folder move was not subdivided");
        Exact(ensures, 1, "refused folder move created the absent destination");
        Exact(childMoves, 1, "refused folder move subdivided per child");
        Exact(deletes, 1, "refused folder move removed the source");
        Check.Require(cloud.Items.ContainsKey("new/child") && !cloud.Items.ContainsKey("old"), "Refused folder move lost the child or the source");
    }
    // R94-PENDING: the mapped-range compare precedes a folder repath whose source cleanup is the same held removal
    // under standalone Repair (R94(b)); the compare itself is read-only but the enclosing Repair cannot settle.
    public static Task MappedRangeBeforeRepathAsync(int size) => throw new NotImplementedException();
    // Source cleanup after a merge removes only the original source folder, not one delete per moved child, and
    // preserves the destination children, an unrelated local file and an untouched cloud peer. The source removal
    // and the destination moves are counted and observed separately, and nothing grows with `size`.
    public static async Task SourceCleanupAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        var source = ItemChecks.File(Side.Local, "old", 0, "source", folder: true);
        var dest = source with { Path = "new", Id = "dest" };
        pc.Seed(source, []); pc.Seed(dest, []);
        var unrelated = ItemChecks.File(Side.Local, "unrelated", 1, "keep");
        pc.Seed(unrelated, [7]);
        var names = new[] { "a", "b" };
        foreach (var name in names)
        {
            var child = ItemChecks.File(Side.Local, $"old/{name}", 1, $"file-{name}");
            var peer = ItemChecks.File(Side.Dropbox, $"old/{name}", 1, $"peer-{name}");
            pc.Seed(child, [1]); cloud.Seed(peer, [1]);
            await store.CommitAsync(pair.PairId, null, SyncedEntries.From(child, peer), null, null, default);
        }
        Boundary(pc); Boundary(cloud);
        var deletes = new List<string>();
        var local = FolderDeleteChecks.Observe.On(pc, (op, args) =>
        {
            if (op == nameof(ISide.DeleteAsync) && args[0] is Entry victim) deletes.Add(victim.Path);
            return null;
        });
        var result = await new FolderMerge().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, source, dest, Side.Local, local, cloud, store,
            _ => [], static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default, coversThrough: _ => true);
        foreach (var name in names) Check.Require(pc.Items.ContainsKey($"new/{name}"), $"Source cleanup lost moved child {name}");
        // The only removal is the original source folder; the destination children, an unrelated file and the peer survive.
        Check.Require(deletes.SequenceEqual(new[] { "old" }), "Source cleanup removed more than the original source folder");
        Check.Require(pc.Items.ContainsKey("unrelated") && pc.Items["unrelated"].Bytes.SequenceEqual(new byte[] { 7 }), "Source cleanup deleted an unrelated file");
        Check.Require(cloud.Items.ContainsKey("old/a") && cloud.Items.ContainsKey("old/b"), "Source cleanup removed an untouched cloud peer");
        Exact(pc.Requests[nameof(ISide.MoveAsync)], names.Length, "source cleanup destination moves");
        Exact(ContentTransfer(pc) + ContentTransfer(cloud), 0, "source cleanup content transfer");
        Check.Require(!pc.Items.ContainsKey("old") && result.Outcome == RemovalOutcome.Removed, "Source cleanup left the source folder");
    }

    // ---- Folder-job checks --------------------------------------------------------------------------------
    // One scripted delete job settles on its first check; each check is one read of the returned job.

    private static AcceptedJob DeleteJob(PairConfig pair) =>
        new(pair.PairId, "files/delete_v2", "job-1", [new JobMember("cd-job", "folder", null) { Origin = ActionKind.DeleteDropbox }]);

    private static async Task<(InMemoryStateStore Store, FakeSide Pc, FakeSide Cloud, Unfinished Unfinished, AcceptedJob Job)> JobAsync(PairConfig pair, int size)
    {
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        var job = DeleteJob(pair);
        await store.SaveJobAsync(job, default);
        Boundary(pc); Boundary(cloud);
        cloud.JobReplies[job.JobId] = new Queue<JobReply>();
        cloud.JobReplies[job.JobId].Enqueue(new JobReply.Terminal(
            new (Entry? Entry, TextKey? Cause, FailureDisposition? Failure)[] { (null, null, null) }) { AbsentOrdinals = new HashSet<int> { 0 } });
        // A standalone owner: StartQueueAsync would run its own boot check and consume the scripted job first.
        var unfinished = new PairRunner().Unfinished;
        unfinished.AcquiredThrough = _ => true;
        return (store, pc, cloud, unfinished, job);
    }

    public static async Task FolderJobBootAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, unfinished, _) = await JobAsync(pair, size);
        await unfinished.CheckAsync(pair, ItemChecks.Settings, _ => { }, paths, null, pc, cloud, store,
            static _ => Array.Empty<Change>(), static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default);
        Exact(cloud.Requests[nameof(ISide.ReadJobAsync)], 1, "boot folder-job checks");
    }

    public static async Task FolderJobRepairAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, unfinished, _) = await JobAsync(pair, size);
        await unfinished.CheckAsync(pair, ItemChecks.Settings, _ => { }, paths, null, pc, cloud, store,
            static _ => Array.Empty<Change>(), static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default);
        Exact(cloud.Requests[nameof(ISide.ReadJobAsync)], 1, "repair-start folder-job checks");
    }

    public static async Task FolderJobOverlapAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, unfinished, _) = await JobAsync(pair, size);
        // An overlapping feed change wakes the job; an unrelated change does not.
        await unfinished.CheckAsync(pair, ItemChecks.Settings, _ => { }, paths,
            new Change(Side.Dropbox, ChangeKind.Changed, "folder/member", null, "cd-x", null, false, false), pc, cloud, store,
            static _ => Array.Empty<Change>(), static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default);
        long overlapping = cloud.Requests[nameof(ISide.ReadJobAsync)];
        AtMost(overlapping, 1, "overlapping folder-job checks");
        await unfinished.CheckAsync(pair, ItemChecks.Settings, _ => { }, paths,
            new Change(Side.Dropbox, ChangeKind.Changed, "elsewhere", null, "cd-y", null, false, false), pc, cloud, store,
            static _ => Array.Empty<Change>(), static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default);
        Exact(cloud.Requests[nameof(ISide.ReadJobAsync)] - overlapping, 0, "unrelated folder-job checks");
    }

    // A wake that arrives while a check is still in flight joins one follow-up read instead of opening a second
    // concurrent provider read. The JournalProbe holds the first ReadJobAsync open and counts every read.
    public static async Task FolderJobCoalescedWakeAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, unfinished, _) = await JobAsync(pair, size);
        ISide proxy = System.Reflection.DispatchProxy.Create<ISide, HostChecks.JournalProbe>();
        var probe = (HostChecks.JournalProbe)(object)proxy; probe.Local = cloud;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        probe.Job = async token =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new JobReply.Running();
            }
            return new JobReply.Terminal(new (Entry? Entry, TextKey? Cause, FailureDisposition? Failure)[] { (null, null, null) }) { AbsentOrdinals = new HashSet<int> { 0 } };
        };
        Change Wake(string path) => new(Side.Dropbox, ChangeKind.Changed, path, null, "cd-" + path, null, false, false);
        var first = unfinished.CheckAsync(pair, ItemChecks.Settings, _ => { }, paths, Wake("folder/member"), pc, proxy, store,
            static _ => Array.Empty<Change>(), static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default);
        await entered.Task; // the first check is inside ReadJobAsync with its key held
        var second = unfinished.CheckAsync(pair, ItemChecks.Settings, _ => { }, paths, Wake("folder/other"), pc, proxy, store,
            static _ => Array.Empty<Change>(), static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default);
        await second; // the mid-check wake coalesces and returns without a second provider read
        release.TrySetResult();
        await first;
        Exact(probe.JobReads, 2, "coalesced folder-job checks");
    }

    // DOUBLE: the block-and-delta split lives in the real Dropbox deleted-history adapter (Dropbox/DeletedHistory.cs);
    // the in-memory acquisition history models the cursor, spent mask and deleted-name enumeration but carries no
    // encoded compressed blocks, so the block/delta request split cannot be driven through the scenario. That adapter
    // contract is already proven by Dropbox/DeletedHistoryChecks.
    public static Task DeletedNameBlockAndDeltaAsync(int size) => throw new NotImplementedException();
    // COUNTER: scratch scan rows are now written by AddScan inside a Repair acquisition (BeginScan works), but neither
    // InMemoryStateStore nor the real Store exposes a scratch-row counter to assert "1 row per listed entry, cleared
    // after". Needs a scratch-row counter/reader on the store; the real Store is required for SQL counts and has none.
    public static Task ScratchScanWritesAndClearAsync(int size) => throw new NotImplementedException();

    // ---- Scoped compare -----------------------------------------------------------------------------------

    public static async Task ScopeConfigWriteAsync(int size)
    {
        var pair = ItemChecks.Pair();
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        var scopes = new (string Owner, Scope Scope)[] { (pair.PairId, new Scope("a", "a")), (pair.PairId, new Scope("b", "b")) };
        await store.SavePairAsync(pair, default, scopes);
        var saved = await store.PairAsync(pair.PairId, default);
        // One write per distinct returned scope, and no provider work for a configuration write.
        Exact(saved!.OwedScopes!.Count, 2, "configuration scope writes");
        Exact(Transfer(pc) + Transfer(cloud), 0, "configuration scope provider work");
    }

    // A scoped Repair compares one mapped range and reads/transfers nothing per unchanged item. Provider-call budget on
    // the parity-proven in-memory double (R93); the scoped-compare path itself is the same BeginScan/ScanTouched route.
    public static async Task ScopeCompareAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        var scope = new Scope("unchanged-0", "unchanged-0");
        var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, cloud, store);
        await Repair.RunAsync(pair, ItemChecks.Settings, _ => { }, paths, queue, pc, cloud, store, scope, null, default);
        foreach (var side in new[] { pc, cloud })
        {
            Check.Require(side.Requests[nameof(ISide.ReadAllAsync)] > 0, "Scoped Repair skipped a side");
            foreach (var op in new[] { nameof(ISide.OpenReadAsync), nameof(ISide.ContentHashAsync), nameof(ISide.PublishAsync), nameof(ISide.DeleteAsync), nameof(ISide.MoveAsync) })
                Exact(side.Requests[op], 0, $"{side.Side} {op} per unchanged scoped item");
        }
    }

    // DOUBLE (unworkable interruption seam): an interrupted-and-repeated scoped compare needs the acquisition to abort
    // midway at a chosen entry; the FakeSide enumerates atomically so the interruption point cannot be fixed, and no
    // double holds a scan open. The scoped budget itself is proven by ScopeCompareAsync.
    public static Task ScopePartialRepeatAsync(int size) => throw new NotImplementedException();

    // ---- Failure and notices ------------------------------------------------------------------------------

    public static async Task FailureNewAndRefreshedAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string path = "item";
        var local = ItemChecks.File(Side.Local, path, 2, "pl-item");
        pc.Seed(local, [2]);
        // A content read failure is a permanent failure: one Notice, and a refresh reuses the same slot.
        pc.ContentFailures[path] = new ItemFailedException(path, TextKey.ReadLocalFailed, null);
        var change = new Change(Side.Local, ChangeKind.Changed, path, null, local.Id, local, true, false);
        try { await ApplyFile(pair, paths, change, pc, cloud, store); } catch (ItemFailedException) { }
        Exact(store.OpenNotices.Count(n => n.Kind == NoticeKind.Failure), 1, "one failure notice");
        try { await ApplyFile(pair, paths, change, pc, cloud, store); } catch (ItemFailedException) { }
        Exact(store.OpenNotices.Count(n => n.Kind == NoticeKind.Failure), 1, "refreshed failure keeps one slot");
    }

    public static async Task NoticeCloseAsync(int size)
    {
        var pair = ItemChecks.Pair();
        var (store, _, _, _) = await SteadyAsync(pair, size);
        var notice = new Notice(0, NoticeKind.Failure, TextKey.FailChanged, "item",
            new IssueSubject(new NoticeItem[] { new(Side.Local, "item", "pl") }, Side.Local), DateTimeOffset.UnixEpoch, false);
        await store.SaveNoticeAsync(pair.PairId, notice, default);
        var saved = store.OpenNotices.Single();
        Check.Require(await new Notices().CloseAsync(store, pair.PairId, saved, default), "Notice did not close");
        Exact(store.OpenNotices.Count, 0, "notices after close");
    }

    // A move whose reply was lost is re-observed by the exact selected identity; a different name-reused successor
    // at the old name is neither adopted nor mutated, and no unproved write is resent.
    public static async Task UnknownEffectReadbacksAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        const string old = "item";
        var pcOriginal = ItemChecks.File(Side.Local, old, 1, "pc-original");
        var original = ItemChecks.File(Side.Dropbox, old, 1, "id:original");
        pc.Seed(pcOriginal, [1]); cloud.Seed(original, [1]);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(pcOriginal, original), null, null, default);
        // A move whose reply was lost: the selected identity now sits at "moved"; a different successor reused the old name.
        var job = new AcceptedJob(pair.PairId, "files/move_batch/check", "lost", [new(original.Id, old, "moved")]);
        await store.SaveJobAsync(job, default);
        cloud.Items.Remove(old);
        cloud.Seed(original with { Path = "moved" }, [1]);
        var successor = ItemChecks.File(Side.Dropbox, old, 2, "id:successor");
        cloud.Seed(successor, [2]);
        cloud.JobReplies[job.JobId] = new([new JobReply.Invalid()]);
        Boundary(pc); Boundary(cloud);
        var unfinished = new PairRunner().Unfinished; unfinished.AcquiredThrough = _ => true;
        await unfinished.CheckAsync(pair, ItemChecks.Settings, _ => { }, paths, null, pc, cloud, store,
            static _ => Array.Empty<Change>(), static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default);
        Exact(cloud.Requests[nameof(ISide.ReadJobAsync)], 1, "lost reply job reads");
        Check.Require(cloud.Requests[nameof(ISide.ReadByIdAsync)] > 0, "lost reply did not read the selected identity back by id");
        Check.Require(cloud.Items[old].Entry == successor && cloud.Items[old].Bytes.SequenceEqual(new byte[] { 2 }), "lost reply adopted or overwrote a successor at the old name");
        Check.Require(cloud.Mutations.All(id => id != successor.Id), "lost reply mutated a successor");
    }
    // STOPPED: held-version retry needs an armed note, a ReopenStageAsync proof and recoverable versions; the
    // InMemoryStateStore keeps armed notes but the recovery chain is not exercised by the existing doubles.
    public static Task HeldVersionRetryAsync(int size) => throw new NotImplementedException();

    // ---- Restart / resume ---------------------------------------------------------------------------------

    public static async Task StartupOpenObligationsAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, cloud, store);
        // No open job: startup touches no provider and no per-item read.
        await queue.Unfinished.CheckAsync(pair, ItemChecks.Settings, _ => { }, paths, null, pc, cloud, store,
            static _ => Array.Empty<Change>(), static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default);
        Exact(cloud.Requests[nameof(ISide.ReadJobAsync)], 0, "startup job reads with none open");
        Exact(Transfer(pc) + Transfer(cloud), 0, "startup per-item provider work");
    }

    // DOUBLE: replay of held reports needs a journal replay source (PairRunner.JournalReplay / the Local journal).
    // The doubles still expose no replay source (harness-f1 added BeginScan/ScanTouched, not a journal); the resumed
    // Repair that consumes the replay has no fixture to seed it.
    public static Task ResumeReplayAsync(int size) => throw new NotImplementedException();
    // DOUBLE: paused-volume history is a journal-replay path whose history fixture the doubles do not provide; the
    // resumed pair likewise has no replay source to drive the counts.
    public static Task PausedVolumeHistoryAsync(int size) => throw new NotImplementedException();

    public static async Task CleanBootNoBindingWalkAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, cloud, store);
        long finds = store.Requests[nameof(IStore.FindAsync)];
        // An idle clean boot replays nothing: it must not walk the saved bindings at all.
        var idle = new ChangePage([], "cursor", new JournalPosition(1, 1)) { CaughtUp = true };
        await AutoSync.ApplyPageAsync(pair, ItemChecks.Settings, _ => { }, paths, queue, Side.Dropbox, idle, pc, cloud, store, default);
        Exact(store.Requests[nameof(IStore.FindAsync)] - finds, 0, "clean boot binding lookups");
        Exact(Transfer(pc) + Transfer(cloud), 0, "clean boot per-item provider work");
    }

    // COUNTER: per-SQL-statement and rows-changed counts. Per the rule these must use the real SQLite Store, and Repair
    // now acquires (BeginScan works), but neither the real Store nor the double exposes a statement/rows counter. Needs
    // a counting IStore wrapper (or a per-statement hook on Store); no such seam exists and this helper may not build
    // new ones.
    public static Task PerPhaseSqlAndRowCountsAsync(int size) => throw new NotImplementedException();
    // COUNTER: durable bytes per item need a byte/page counter. Neither InMemoryStateStore nor the real Store exposes
    // one, and opening state.db for a file-size measure is forbidden by the brief.
    public static Task DurableBytesPerItemAsync(int size) => throw new NotImplementedException();
    // COUNTER: scratch bytes per item need a scratch-byte counter; the scan now runs (BeginScan works) but no store
    // exposes a scratch-byte/page counter.
    public static Task ScratchBytesPerItemAsync(int size) => throw new NotImplementedException();
    // COUNTER: window/waiter memory has no counter and section 7 certifies no memory bound; measuring live memory is
    // not a reproducible check and the doubles expose no measurement seam.
    public static Task WindowAndWaiterMemoryAsync(int size) => throw new NotImplementedException();

    private static string Hash(byte value)
    {
        using var hash = new ContentHash(); hash.Append([value]); return hash.Finish();
    }
}
