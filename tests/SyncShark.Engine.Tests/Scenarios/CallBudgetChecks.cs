using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
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

    // STOPPED: first-run context (Row.Context.FirstRun) is set only by the Repair acquisition; a direct executor
    // call always sees the ordinary path. Reaching first run therefore needs Repair.RunAsync, which throws the
    // product default IStore.BeginScan (IStore.cs:87) because InMemoryStateStore does not implement it.
    public static Task FirstSyncStampMatchAsync(int size) => throw new NotImplementedException();
    public static Task FirstSyncDeletedNameProbeAsync(int size) => throw new NotImplementedException();

    // STOPPED: Repair.RunAsync / AutoSync.PopulateAsync call IStore.BeginScan and IStore.ScanTouched, whose product
    // default implementations throw NotImplementedException (src/SyncShark.Engine/IStore.cs:87,89) and which
    // InMemoryStateStore does not implement. Every gap-repair case is NI until the double gains those two members.
    public static Task RepairAfterGapAsync(int size) => throw new NotImplementedException();

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

    // STOPPED: a refusal split is a Dropbox batch reply (FailDropboxTooManyFiles) that FakeSide does not model;
    // its child-fan-out counting needs a batch-answer double.
    public static Task RefusedFolderSplitAsync(int size) => throw new NotImplementedException();

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

    // STOPPED: folder move/merge destination creation and child fan-out need the physical-hop Repair callback,
    // which throws the same IStore.BeginScan default (IStore.cs:87).
    public static Task MoveOrMergeDestinationAsync(int size) => throw new NotImplementedException();
    // STOPPED: a refused move is scripted through FakeSide.MoveOutcomes, but the follow-up child fan-out reaches
    // FolderMerge + the hop callback and thus IStore.BeginScan (IStore.cs:87).
    public static Task MoveRefusalSubdivideAsync(int size) => throw new NotImplementedException();
    // STOPPED: mapped-range compare happens inside a physical-hop Repair.RunAsync, which calls IStore.BeginScan
    // (IStore.cs:87) that InMemoryStateStore does not implement.
    public static Task MappedRangeBeforeRepathAsync(int size) => throw new NotImplementedException();
    // STOPPED: source cleanup is FolderDelete's post-merge step, reachable only through the hop Repair callback
    // (IStore.BeginScan, IStore.cs:87).
    public static Task SourceCleanupAsync(int size) => throw new NotImplementedException();

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

    // STOPPED: coalescing a wake that arrives mid-check needs a concurrent re-entrant check; the scripted job double
    // has no way to hold a check open. Needs a double change.
    public static Task FolderJobCoalescedWakeAsync(int size) => throw new NotImplementedException();

    // STOPPED: the block-and-delta split is the real Dropbox deleted-history adapter (Dropbox/DeletedHistory.cs);
    // the acquisition that binds its cursor is a Repair scan (IStore.BeginScan, IStore.cs:87).
    public static Task DeletedNameBlockAndDeltaAsync(int size) => throw new NotImplementedException();
    // STOPPED: scratch scan rows are written by AddScan inside a Repair/AutoSync scan (IStore.BeginScan,
    // IStore.cs:87); InMemoryStateStore also exposes no scratch-row count.
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

    // STOPPED: a scoped compare runs inside Repair acquisition (IStore.BeginScan, IStore.cs:87); an interrupted
    // repeat needs the same scan.
    public static Task ScopeCompareAsync(int size) => throw new NotImplementedException();
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

    // STOPPED: unknown-effect readbacks need an Unknown move reply plus the armed-stage recovery path; the fake
    // scripts Unknown removal outcomes but not the attributable read-back chain.
    public static Task UnknownEffectReadbacksAsync(int size) => throw new NotImplementedException();
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

    // STOPPED: replay of held reports needs a journal replay fixture (PairRunner.JournalReplay / Local journal);
    // the doubles do not provide a replay source, and the resumed Repair reaches IStore.BeginScan (IStore.cs:87).
    public static Task ResumeReplayAsync(int size) => throw new NotImplementedException();
    // STOPPED: paused-volume history is a journal-replay path over the real store; the doubles have no paused-volume
    // or history fixture, and resuming reaches IStore.BeginScan (IStore.cs:87).
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

    // STOPPED: per-statement row/VM counts need a SQL-level counting store and a full Repair acquisition; the
    // in-memory double counts only method calls, and Repair reaches IStore.BeginScan (IStore.cs:87).
    public static Task PerPhaseSqlAndRowCountsAsync(int size) => throw new NotImplementedException();
    // STOPPED: durable bytes per item need a byte/page counter, which InMemoryStateStore does not expose; measuring
    // it requires the real SQLite store (state.db), which the rules forbid the check to open.
    public static Task DurableBytesPerItemAsync(int size) => throw new NotImplementedException();
    // STOPPED: scratch bytes per item need a scratch-byte counter; InMemoryStateStore has none and the scratch store
    // is written only inside a scan that reaches IStore.BeginScan (IStore.cs:87).
    public static Task ScratchBytesPerItemAsync(int size) => throw new NotImplementedException();
    // STOPPED: window/waiter memory has no counter and section 7 states no certified memory bound; measuring live
    // memory is not a reproducible check and the doubles expose no measurement seam.
    public static Task WindowAndWaiterMemoryAsync(int size) => throw new NotImplementedException();

    private static string Hash(byte value)
    {
        using var hash = new ContentHash(); hash.Append([value]); return hash.Finish();
    }
}
