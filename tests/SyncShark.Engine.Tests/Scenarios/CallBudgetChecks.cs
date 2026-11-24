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

    /// <summary>Every provider mutation attempted on a side, regardless of outcome.</summary>
    private static long EffectAttempts(FakeSide side) =>
        new[]{nameof(ISide.StageAsync),nameof(ISide.PublishAsync),nameof(ISide.DeleteAsync),nameof(ISide.MoveAsync),
              nameof(ISide.EnsureFolderAsync),nameof(ISide.CreateFolderExclusiveAsync)}
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

    // A changed upload that must use a session costs one content request per provider window, never one per byte.
    // The real UploadContent writer is driven over a scripted append/finish wire and the content requests are
    // counted: the window is a quarter of the file, so the derived count is four at either registry population.
    public static async Task ChangedUploadSessionAsync(int size)
    {
        var writer = new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        long limit = (size + 3) / 4; // a quarter of the file: forces the session split with a bounded, derived window
        static Uri Endpoint(string name) => new(new Uri("https://example.invalid/"), name);
        var file = ItemChecks.File(Side.Local, "item", 1, "source");
        var sent = new List<(string Route, bool Close, int Length)>();
        async Task<System.Net.Http.HttpResponseMessage> Send(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            using var args = System.Text.Json.JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            var close = args.RootElement.GetProperty("close").GetBoolean();
            var append = args.RootElement.TryGetProperty("cursor", out _);
            var chunk = await request.Content!.ReadAsByteArrayAsync(ct);
            sent.Add((request.RequestUri!.Segments[^1], close, chunk.Length));
            return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(append ? string.Empty : "{\"session_id\":\"receipt\"}") };
        }
        // Session path: a seekable source larger than the window is still sent in bounded windows.
        using var source = new MemoryStream(new byte[size]);
        await using var staged = await writer.StageAsync(Send, Endpoint, limit, file.Path, source, file with { Size = size, ContentHash = "" }, null, default);
        var expected = (int)((size + limit - 1) / limit);
        Check.Require(sent.Count == expected && sent[0].Route == "start" && sent.Skip(1).All(r => r.Route == "append_v2") &&
            sent.All(r => r.Close == (r == sent[^1])) && sent.Sum(r => r.Length) == size && staged.SessionId == "receipt",
            "Changed upload session did not cost one bounded content request per provider window");
        // Direct path: a seekable source no larger than the window costs no content request at all.
        using var direct = new MemoryStream(new byte[limit]);
        int directSends = 0;
        Task<System.Net.Http.HttpResponseMessage> Refuse(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        { directSends++; return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("{}") }); }
        await using var single = await writer.StageAsync(Refuse, Endpoint, limit, file.Path, direct, file with { Size = limit, ContentHash = "" }, null, default);
        Exact(directSends, 0, "direct upload content requests");
        Check.Require(single.ConsumesSourceOnPublish, "A direct upload did not keep its source until publication");
    }

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
        await RowExecution.ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, null, [new RenameCandidate(Side.Dropbox, moved, binding)],
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

    // The first-run PC-only probe of an eligible deleted name costs exactly three provider reads: the exact-path
    // metadata miss, the revision history head, and the by-id metadata miss. The real DeletedHistory adapter is
    // driven over a scripted wire; the stub only accepts (and consumes) those three routes, so an extra read fails.
    public static async Task FirstSyncDeletedNameProbeAsync(int size)
    {
        var roots = new RootIdentity(@"T:\Pair A", "/Pair A", "connection-a",
            new DirectoryIdentity(1, 2, null), new DropboxRootIdentity("account", "ns-root", "ns-home", "folder"));
        var pair = ItemChecks.Pair() with { Roots = roots };
        using var wire = new Massdex.SyncShark.Engine.Tests.Dropbox.DropboxStubHandler();
        var api = wire.Client();
        const string child = "A/Child", native = "/Pair A/A/Child", id = "id:retained", revision = "rev-a";
        var hash = new string('a', 64);
        var head = System.Text.Json.JsonSerializer.Serialize(new
        {
            is_deleted = true, server_deleted = "2024-03-02T14:05:06Z",
            entries = new[] { new { id, rev = revision, path_display = native, content_hash = hash } }
        });
        wire.Add("files/get_metadata", native, Massdex.SyncShark.Engine.Tests.Dropbox.DropboxStubHandler.Missing, System.Net.HttpStatusCode.Conflict);
        wire.Add("files/list_revisions", native, head);
        wire.Add("files/get_metadata", id, Massdex.SyncShark.Engine.Tests.Dropbox.DropboxStubHandler.Missing, System.Net.HttpStatusCode.Conflict);
        var result = await new global::Massdex.SyncShark.Engine.Dropbox.DeletedHistory().ReadAsync(api, pair, child, false, default);
        wire.Done(); // all three scripted routes were required, in order, and no fourth was sent
        Check.Require(result is DeletionEvidence.DeletedFile deleted && deleted.Path == child && deleted.RemoteId == id &&
            deleted.Revision == revision && deleted.ContentHash == hash, "A retained deletion did not cost exactly the three evidence reads");
    }

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
        var unrelated = ItemChecks.File(Side.Dropbox, "other", 1, "other");
        cloud.Seed(unrelated, [9]);
        Boundary(pc); Boundary(cloud);
        bool refused = false;
        var attempted = new List<string>();
        var deleting = FolderDeleteChecks.Observe.On(cloud, (op, args) =>
        {
            // Count every provider delete attempt at the observation boundary, on either overload, including the refused root.
            if (op == nameof(ISide.DeleteAsync) && args.Length >= 1 && args[0] is Entry victim)
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
        // Exactly the refused root, each selected child once, then the final root removal: no extra delete target of any kind.
        Check.Require(attempted.Count == children.Length + 2 && attempted[0] == root && attempted[^1] == root &&
            attempted.Count(p => p == root) == 2 && children.All(c => attempted.Count(p => p == c) == 1) &&
            attempted.All(p => p == root || children.Contains(p)),
            "Refused folder split did not delete exactly the root, each selected child, then the root again");
        Check.Require(cloud.Items.TryGetValue("other", out var kept) && kept.Entry == unrelated && kept.Bytes.SequenceEqual(new byte[] { 9 }),
            "Refused folder split deleted an unrelated cloud item");
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
        var (store, pc, cloud, source, dest, children) = await LocalMergeAsync(pair, size, ["free"], null);
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
        Check.Require(pc.Items.TryGetValue("new/free", out var moved) && moved.Entry == (children[0] with { Path = "new/free" }),
            "Move or merge did not move the child by identity");
        Exact(ensures, 0, "existing destination folder ensures");
        Exact(moves, children.Length, "merge destination child moves");
        Exact(deletes, 1, "merge source folder delete");
        Exact(ContentTransfer(pc) + ContentTransfer(cloud), 0, "merge content transfer");
        Check.Require(!pc.Items.ContainsKey("old") && result.Outcome == RemovalOutcome.Removed, "Merge did not settle the source");
    }
    // Shared local-written merge setup: a source folder "old" with the named children bound to cloud peers, an
    // existing destination "new", and (optionally) an unrelated local sentinel. Each check adds its own observers.
    private static async Task<(InMemoryStateStore Store, FakeSide Pc, FakeSide Cloud, Entry Source, Entry Dest, Entry[] Children)>
        LocalMergeAsync(PairConfig pair, int size, string[] names, Entry? unrelated)
    {
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        var source = ItemChecks.File(Side.Local, "old", 0, "source", folder: true);
        var dest = source with { Path = "new", Id = "dest" };
        pc.Seed(source, []); pc.Seed(dest, []);
        if (unrelated is { } keep) pc.Seed(keep, [7]);
        var children = new Entry[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            var child = ItemChecks.File(Side.Local, $"old/{names[i]}", 1, $"file-{names[i]}");
            var peer = ItemChecks.File(Side.Dropbox, $"old/{names[i]}", 1, $"peer-{names[i]}");
            pc.Seed(child, [1]); cloud.Seed(peer, [1]);
            await store.CommitAsync(pair.PairId, null, SyncedEntries.From(child, peer), null, null, default);
            children[i] = child;
        }
        Boundary(pc); Boundary(cloud);
        return (store, pc, cloud, source, dest, children);
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
        var children = new[] { "a", "b" };
        foreach (var name in children) cloud.Seed(ItemChecks.File(Side.Dropbox, $"old/{name}", 1, name), [1]);
        Boundary(pc); Boundary(cloud);
        // The root move is refused for size; Effects must create the destination and subdivide into per-child moves.
        cloud.MoveOutcomes.Enqueue(new(RemovalOutcome.NoEffect, source, TextKey.FailDropboxTooManyFiles));
        long ensures = 0;
        var moves = new List<string>(); var deletes = new List<string>();
        var remote = FolderDeleteChecks.Observe.On(cloud, (op, args) =>
        {
            if (op == nameof(ISide.EnsureFolderAsync)) ensures++;
            else if (op == nameof(ISide.MoveAsync) && args[1] is string to) moves.Add(to);
            else if (op == nameof(ISide.DeleteAsync) && args.Length >= 1 && args[0] is Entry victim) deletes.Add(victim.Path);
            return null;
        });
        var action = new SyncAction(ActionKind.MoveDropbox,
            new Row(saved.LocalKey, peer, source, saved, [], null, RowContext.Neutral), Target: "new");
        var outcome = await new Effects().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, action, pc, remote, store,
            static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default, _ => [], _ => true);
        Check.Require(outcome == EffectOutcome.Applied, "Refused folder move was not subdivided");
        Exact(ensures, 1, "refused folder move created the absent destination");
        // Exactly the refused root attempt plus one move per selected child, and no other move target.
        Exact(moves.Count, children.Length + 1, "refused folder move attempts");
        Check.Require(moves.Count(m => m == "new") == 1 && children.All(n => moves.Count(m => m == $"new/{n}") == 1),
            "Refused folder move did not target exactly the root then each child");
        // Exactly the original source folder is removed, and no content is transferred.
        Check.Require(deletes.SequenceEqual(new[] { "old" }), "Refused folder move did not remove exactly the original source");
        Exact(ContentTransfer(pc) + ContentTransfer(cloud), 0, "refused folder move content transfer");
        foreach (var name in children)
            Check.Require(cloud.Items.TryGetValue($"new/{name}", out var moved) && moved.Entry.Id == name && moved.Bytes.SequenceEqual(new byte[] { 1 }) && !cloud.Items.ContainsKey($"old/{name}"),
                $"Refused folder move lost or altered child {name}");
        Check.Require(!cloud.Items.ContainsKey("old"), "Refused folder move left the source folder");
    }
    // Cross-lane (Run/Harness): the covered-hop mapped-range rig lives in RepairChecks.CoveredHopAsync (Run/). The
    // read-only mapped-range acquisition ordered before the repath must be counted there; this single-file lane
    // (CallBudgetChecks.cs only) may not edit that rig. Left NI here; reported in cross_lane.
    public static Task MappedRangeBeforeRepathAsync(int size) => throw new NotImplementedException();
    // Source cleanup after a merge removes only the original source folder, not one delete per moved child, and
    // preserves the destination children, an unrelated local file and an untouched cloud peer. The source removal
    // and the destination moves are counted and observed separately, and a local-only merge makes no cloud effect.
    public static async Task SourceCleanupAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var unrelated = ItemChecks.File(Side.Local, "unrelated", 1, "keep");
        var (store, pc, cloud, source, dest, children) = await LocalMergeAsync(pair, size, ["a", "b"], unrelated);
        var cloudKeep = ItemChecks.File(Side.Dropbox, "cloud-keep", 1, "cloud-keep");
        cloud.Seed(cloudKeep, [9]);
        var deletes = new List<string>();
        var local = FolderDeleteChecks.Observe.On(pc, (op, args) =>
        {
            if (op == nameof(ISide.DeleteAsync) && args.Length >= 1 && args[0] is Entry victim) deletes.Add(victim.Path);
            return null;
        });
        var result = await new FolderMerge().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, source, dest, Side.Local, local, cloud, store,
            _ => [], static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, default, coversThrough: _ => true);
        // Each moved child kept its identity and bytes at the destination.
        foreach (var child in children)
        {
            var at = "new" + child.Path["old".Length..];
            Check.Require(pc.Items.TryGetValue(at, out var moved) && moved.Entry.Id == child.Id && moved.Bytes.SequenceEqual(new byte[] { 1 }),
                $"Source cleanup lost moved child {at}");
        }
        // The only removal is the original source folder; the destination children, an unrelated file and the peers survive.
        Check.Require(deletes.SequenceEqual(new[] { "old" }), "Source cleanup removed more than the original source folder");
        Check.Require(pc.Items.ContainsKey("unrelated") && pc.Items["unrelated"].Bytes.SequenceEqual(new byte[] { 7 }), "Source cleanup deleted an unrelated file");
        Check.Require(cloud.Items.TryGetValue("old/a", out var peerA) && cloud.Items.TryGetValue("old/b", out var peerB) && peerA.Bytes.SequenceEqual(new byte[] { 1 }) && peerB.Bytes.SequenceEqual(new byte[] { 1 }),
            "Source cleanup removed or altered an untouched cloud peer");
        Check.Require(cloud.Items.TryGetValue("cloud-keep", out var kept) && kept.Bytes.SequenceEqual(new byte[] { 9 }), "Source cleanup deleted an unrelated cloud item");
        // A local-only merge must leave the cloud endpoint completely untouched by effects.
        Exact(cloud.Requests[nameof(ISide.MoveAsync)] + cloud.Requests[nameof(ISide.DeleteAsync)] + cloud.Requests[nameof(ISide.PublishAsync)] +
            cloud.Requests[nameof(ISide.EnsureFolderAsync)] + cloud.Requests[nameof(ISide.CreateFolderExclusiveAsync)], 0, "source cleanup cloud effect attempts");
        Exact(pc.Requests[nameof(ISide.MoveAsync)], children.Length, "source cleanup destination moves");
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

    // Several wakes that arrive while a check is still in flight all collapse into one follow-up read instead of
    // opening concurrent provider reads. The JournalProbe holds the first ReadJobAsync open and counts every read.
    public static Task FolderJobCoalescedWakeAsync(int size) => AutoSyncChecks.WithinDeadline(async ct =>
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, unfinished, _) = await JobAsync(pair, size);
        ISide proxy = System.Reflection.DispatchProxy.Create<ISide, HostChecks.JournalProbe>();
        var probe = (HostChecks.JournalProbe)(object)proxy; probe.Local = cloud;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Job = async token =>
        {
            if (probe.JobReads == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new JobReply.Running();
            }
            return new JobReply.Terminal(new (Entry? Entry, TextKey? Cause, FailureDisposition? Failure)[] { (null, null, null) }) { AbsentOrdinals = new HashSet<int> { 0 } };
        };
        Change Wake(string path) => new(Side.Dropbox, ChangeKind.Changed, path, null, "cd-" + path, null, false, false);
        Task Check(string path) => unfinished.CheckAsync(pair, ItemChecks.Settings, _ => { }, paths, Wake(path), pc, proxy, store,
            static _ => Array.Empty<Change>(), static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, ct);
        try
        {
            var first = Check("folder/first");
            await entered.Task.WaitAsync(ct); // the first check is inside ReadJobAsync with its key held
            Exact(probe.JobReads, 1, "held folder-job read");
            // Multiple overlapping wakes arrive mid-check; each coalesces and returns with no concurrent provider read.
            var overlapping = new[] { "folder/second", "folder/third", "folder/fourth" }.Select(Check).ToArray();
            await Task.WhenAll(overlapping).WaitAsync(ct);
            Exact(probe.JobReads, 1, "overlapping wakes opened a concurrent provider read");
            release.TrySetResult();
            await first;
            // Exactly one follow-up read performs the single coalesced check.
            Exact(probe.JobReads, 2, "overlapping wakes did not collapse into exactly one follow-up read");
        }
        finally { release.TrySetResult(); }
    });

    // A published deleted-name block and an independent delta both answer lookups, and the encoded block is decoded
    // once (at most twice) across many probes rather than replayed per name. The real SQLite DeletedNames fixture is used.
    public static async Task DeletedNameBlockAndDeltaAsync(int size)
    {
        using var db = await Massdex.SyncShark.Engine.Tests.Storage.DeletedNamesChecks.OpenAsync();
        var table = new Massdex.SyncShark.Engine.Storage.ScanTable();
        var scan = Guid.NewGuid();
        // One compressed block holds at most the provider's row cap; use that many names regardless of population.
        var count = Math.Min(size, Massdex.SyncShark.Engine.Storage.DeletedNamesCodec.DeletedBlockRows);
        var keys = Enumerable.Range(0, count).Select(i => PathNames.Key($"old/{i:D6}")).ToArray();
        var bytes = Massdex.SyncShark.Engine.Storage.DeletedNamesCodec.EncodeDeletedNames(keys);
        const string delta = "append";
        using (var seed = db.CreateCommand())
        {
            seed.CommandText = "UPDATE pair SET deleted_history_cursor='previous',deleted_history_owed=0;"
                + "INSERT INTO discovery_deleted_block VALUES(1,$last,$count,$bytes);"
                + "INSERT INTO discovery_deleted_delta VALUES(1,'append',0);";
            seed.Parameters.AddWithValue("$last", keys[^1]);
            seed.Parameters.AddWithValue("$count", count);
            seed.Parameters.AddWithValue("$bytes", bytes);
            await seed.ExecuteNonQueryAsync();
        }
        int blockReads = 0;
        SQLitePCL.strdelegate_trace trace = (_, text) =>
        {
            if (text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && text.Contains("FROM discovery_deleted_block", StringComparison.OrdinalIgnoreCase)) blockReads++;
        };
        SQLitePCL.raw.sqlite3_trace(db.Handle!, trace, null);
        try
        {
            // Probe both partitions: names from the compressed block and the independent delta name.
            foreach (var index in new[] { 0, count / 3, count / 2, count * 2 / 3, count - 1 })
                Check.Require(await table.History.ContainsAsync(db, "p", scan, keys[index], false, default), "A compact block name was lost");
            Check.Require(await table.History.ContainsAsync(db, "p", scan, delta, false, default), "The independent delta name was lost");
            Check.Require(!await table.History.ContainsAsync(db, "p", scan, PathNames.Key("absent"), false, default), "A missing name was selected");
        }
        finally { SQLitePCL.raw.sqlite3_trace(db.Handle!, (SQLitePCL.strdelegate_trace)null!, null); }
        Check.Require(blockReads is > 0 and <= 2, $"Deleted-name block was decoded {blockReads} times");
    }
    // A real store's scratch scan records exactly one row per listed entry, and ClearScan removes every row. The
    // derived synthetic .scan image is inspected directly between ingestion and clear; the live database is not used.
    public static async Task ScratchScanWritesAndClearAsync(int size)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var image = Path.Combine(dir.FullName, "fixture.sqlite");
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = image, Pooling = false }.ToString()))
            {
                await db.OpenAsync();
                db.CreateFunction<string, string>("pathkey", PathNames.Key, isDeterministic: true);
                using var schema = db.CreateCommand();
                var version = global::Massdex.SyncShark.Engine.Storage.Schema.Version;
                schema.CommandText = global::Massdex.SyncShark.Engine.Storage.Schema.Create + $"PRAGMA user_version={version};";
                await schema.ExecuteNonQueryAsync();
            }
            using var store = new global::Massdex.SyncShark.Engine.Storage.Store(image, false, ItemChecks.Settings);
            var pair = ItemChecks.Pair(); await store.SavePairAsync(pair, default);
            var scan = Guid.NewGuid();
            var paths = Enumerable.Range(0, size).Select(i => $"s/{i:D6}").ToArray();
            await store.AddScanAsync(pair.PairId, scan, Side.Local, Rows(paths), null, default);
            var recorded = await ScratchPathsAsync(image);
            Check.Require(recorded.SequenceEqual(paths.Order(StringComparer.Ordinal)), "Scratch scan did not record one row per listed entry");
            await store.ClearScanAsync(pair.PairId, scan, default);
            Check.Require((await ScratchPathsAsync(image)).Count == 0, "Scratch scan rows survived ClearScan");
        }
        finally { dir.Delete(true); }

        static async IAsyncEnumerable<Listed> Rows(string[] paths)
        {
            await Task.CompletedTask;
            foreach (var path in paths) yield return new(path, ItemChecks.File(Side.Local, path, 1, "id-" + path));
        }
    }
    // Read the derived scratch image (main image path + ".scan") without touching the live store or state database.
    private static async Task<List<string>> ScratchPathsAsync(string image)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = image + ".scan", Pooling = false }.ToString());
        await db.OpenAsync();
        db.CreateFunction<string, string>("pathkey", PathNames.Key, isDeterministic: true);
        using (var pragma = db.CreateCommand()) { pragma.CommandText = "PRAGMA busy_timeout=5000"; await pragma.ExecuteNonQueryAsync(); }
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT path FROM scan ORDER BY path";
        using var reader = await cmd.ExecuteReaderAsync();
        var paths = new List<string>();
        while (await reader.ReadAsync()) paths.Add(reader.GetString(0));
        return paths;
    }

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

    // A scoped compare is partial: it reads only its scope and transfers nothing per unchanged item, leaving an
    // out-of-scope item byte-identical on both sides. Same BeginScan/ScanTouched route as ScopeCompareAsync.
    public static async Task ScopePartialRepeatAsync(int size)
    {
        var pair = ItemChecks.Pair(); var paths = Paths(pair);
        var (store, pc, cloud, _) = await SteadyAsync(pair, size);
        var outsideLocal = ItemChecks.File(Side.Local, "outside", 1, "out-local");
        var outsideCloud = ItemChecks.File(Side.Dropbox, "outside", 1, "out-cloud");
        pc.Seed(outsideLocal, [1]); cloud.Seed(outsideCloud, [1]);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(outsideLocal, outsideCloud), null, null, default);
        var scope = new Scope("unchanged-0", "unchanged-0");
        var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, cloud, store);
        await Repair.RunAsync(pair, ItemChecks.Settings, _ => { }, paths, queue, pc, cloud, store, scope, null, default);
        foreach (var side in new[] { pc, cloud })
        {
            Check.Require(side.Requests[nameof(ISide.ReadAllAsync)] > 0, "Scoped compare skipped a side");
            foreach (var op in new[] { nameof(ISide.OpenReadAsync), nameof(ISide.ContentHashAsync), nameof(ISide.PublishAsync), nameof(ISide.DeleteAsync), nameof(ISide.MoveAsync) })
                Exact(side.Requests[op], 0, $"{side.Side} {op} per unchanged scoped item");
        }
        // The out-of-scope item is byte-identical on both sides: the compare touched only its scope.
        Check.Require(pc.Items.TryGetValue("outside", out var keptLocal) && keptLocal.Bytes.SequenceEqual(new byte[] { 1 }) &&
            cloud.Items.TryGetValue("outside", out var keptCloud) && keptCloud.Bytes.SequenceEqual(new byte[] { 1 }),
            "Scoped compare altered an out-of-scope item");
    }

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

    // A move whose reply was lost is re-observed by the exact selected identity: exactly one by-id read, no effect on
    // either side before that proof, and the job stays owed. A different name-reused successor at the old name is
    // neither adopted nor mutated. This proves evidence-based checking only, not terminal recovery or stage replay.
    public static Task UnknownEffectReadbacksAsync(int size) => AutoSyncChecks.WithinDeadline(async ct =>
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
        await store.SaveJobAsync(job, ct);
        cloud.Items.Remove(old);
        cloud.Seed(original with { Path = "moved" }, [1]);
        var successor = ItemChecks.File(Side.Dropbox, old, 2, "id:successor");
        cloud.Seed(successor, [2]);
        cloud.JobReplies[job.JobId] = new([new JobReply.Invalid()]);
        Boundary(pc); Boundary(cloud);
        var unfinished = new PairRunner().Unfinished; unfinished.AcquiredThrough = _ => true;
        await unfinished.CheckAsync(pair, ItemChecks.Settings, _ => { }, paths, null, pc, cloud, store,
            static _ => Array.Empty<Change>(), static (_, _, _, _) => Task.CompletedTask, static (_, _, _, _) => Task.CompletedTask, ct);
        Exact(cloud.Requests[nameof(ISide.ReadJobAsync)], 1, "lost reply job reads");
        // A one-member invalid reply re-observes its selected identity exactly once, and nothing before that proof.
        Exact(cloud.Requests[nameof(ISide.ReadByIdAsync)], 1, "lost reply by-id readbacks");
        Exact(EffectAttempts(pc) + EffectAttempts(cloud), 0, "unproved write attempts before the by-id proof");
        Check.Require(cloud.Items[old].Entry == successor && cloud.Items[old].Bytes.SequenceEqual(new byte[] { 2 }), "lost reply adopted or overwrote a successor at the old name");
        Check.Require(cloud.Mutations.All(id => id != successor.Id), "lost reply mutated a successor");
        // The job is still owed, and the recorded Failure notice names the original subject.
        var owed = new List<AcceptedJob>();
        await foreach (var pending in store.JobsAsync(pair.PairId, ct)) owed.Add(pending);
        Check.Require(owed.Count == 1 && owed[0].JobId == job.JobId, "an unproved invalid reply settled the job");
        Check.Require(store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure && n.Cause == TextKey.FailDropboxDeleteJobInvalid &&
            n.Subject.Items.Any(m => m.Side == Side.Dropbox && m.Path == old && m.Id == original.Id)),
            "an unproved invalid reply did not name its original subject in a failure notice");
    });
    // Held version retry: an armed PutBack version is recovered from its exact selected identity through the real
    // HeldRecovery.RetryHeldAsync path. The recovery is read exactly once and the bytes land on both sides, and the
    // reviewed notice closes; no resend or unbounded re-read is allowed.
    public static Task HeldVersionRetryAsync(int size) => AutoSyncChecks.WithinDeadline(async ct =>
    {
        var roots = new RootIdentity(@"T:\Pair A", "/Pair A", "connection-a",
            new DirectoryIdentity(1, 2, null), new DropboxRootIdentity("account", "ns-root", "ns-home", "folder"));
        var pair = ItemChecks.Pair() with { Roots = roots };
        var db = new FakeSide(Side.Dropbox); var local = new FakeSide(Side.Local);
        using var store = new InMemoryStateStore();
        var version = ItemChecks.File(Side.Dropbox, "held/item", 1, "selected");
        int recoveries = 0;
        var remote = FolderDeleteChecks.Observe.On(db, (op, args) =>
        {
            if (op != nameof(ISide.RecoverAsync)) return null;
            recoveries++;
            return Task.FromResult<Recovery?>(new Recovery.File(version, new MemoryStream([1])));
        });
        await store.SaveNoticeAsync(pair.PairId, new(0, NoticeKind.PutBack, TextKey.FailChanged, version.Path,
            new([new(Side.Dropbox, version.Path, version.Id)], Side.Dropbox), DateTimeOffset.UnixEpoch, false), ct);
        var report = new Change(Side.Dropbox, ChangeKind.Changed, version.Path, null, version.Id, version, false, false);
        bool ok = await HeldRecovery.RetryHeldAsync(pair, new Scope("held", "held"), store.OpenNotices.Single(), local, remote, store,
            _ => [report], (_, _, _, _) => Task.CompletedTask, ct);
        Check.Require(ok, "Held version retry did not recover the selected version");
        Check.Require(local.Items.TryGetValue(version.Path, out var recovered) && recovered.Bytes.SequenceEqual(new byte[] { 1 }),
            "Held version retry did not restore the selected bytes to the local side");
        Check.Require(db.Items.TryGetValue(version.Path, out var peer) && peer.Bytes.SequenceEqual(new byte[] { 1 }),
            "Held version retry did not restore the selected bytes to the cloud side");
        Exact(recoveries, 1, "held version recovery reads");
        Exact(store.OpenNotices.Count(n => n.Kind == NoticeKind.PutBack), 0, "held version retry left its notice open");
    });

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

    // Resume replay: a held report is admitted but its effect is held open, as on a resume from a held journal
    // position. A reset at resume discards that stale admission, the report is replayed from the held position, and
    // only the replayed settlement counts; the reset leaves no reusable coverage.
    public static Task ResumeReplayAsync(int size) => AutoSyncChecks.WithinDeadline(async ct =>
    {
        var pair = ItemChecks.Pair();
        using var store = new InMemoryStateStore();
        var queue = new WorkQueue();
        bool staleSettled = false, replaySettled = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new Change(Side.Local, ChangeKind.Changed, "held", null, null, null, false, false);
        var first = queue.AdmitAsync(pair, new ChangePage([held], null, new JournalPosition(1, 31)),
            async (change, token) => { await release.Task.WaitAsync(token); },
            (position, token) => { if (position.NextUsn == 31) staleSettled = true; return Task.CompletedTask; }, ct);
        await queue.LoseCoverage(pair, Side.Local, new ChangePage([], null, new JournalPosition(1, 30)) { ContinuityLost = true }, store, ct);
        var replay = queue.AdmitAsync(pair, new ChangePage([held], null, new JournalPosition(1, 32)),
            (change, token) => Task.CompletedTask,
            (position, token) => { if (position.NextUsn == 32) replaySettled = true; return Task.CompletedTask; }, ct);
        release.TrySetResult();
        await first.WaitAsync(ct);
        await replay.WaitAsync(ct);
        Check.Require(replaySettled, "Resume replay did not settle the replayed held report");
        Check.Require(!staleSettled, "A stale pre-resume admission settled the held report");
        Check.Require(!queue.CoversHop((new JournalPosition(1, 31), "cursor")), "A resume reused stale coverage after its reset");
    });
    // Paused volume history: a read-ahead page admitted before a pause/reset must not settle; the record re-admitted
    // after the rewind settles, and the pause leaves no reusable coverage. Mirrors the proven WorkQueue reset fence.
    public static Task PausedVolumeHistoryAsync(int size) => AutoSyncChecks.WithinDeadline(async ct =>
    {
        var pair = ItemChecks.Pair();
        using var store = new InMemoryStateStore();
        var queue = new WorkQueue();
        bool readAheadSettled = false, rewoundSettled = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = new Change(Side.Local, ChangeKind.Changed, "older", null, null, null, false, false);
        var readAhead = queue.AdmitAsync(pair, new ChangePage([older], null, new JournalPosition(1, 31)),
            async (change, token) => { await release.Task.WaitAsync(token); },
            (position, token) => { if (position.NextUsn == 31) readAheadSettled = true; return Task.CompletedTask; }, ct);
        // Pause/rewind: a continuity reset discards the read-ahead settlement and opens a coverage gap.
        await queue.LoseCoverage(pair, Side.Local, new ChangePage([], null, new JournalPosition(1, 30)) { ContinuityLost = true }, store, ct);
        Check.Require(!queue.CoversHop((new JournalPosition(1, 31), "cursor")), "A paused volume still reported coverage");
        var rewound = queue.AdmitAsync(pair, new ChangePage([older], null, new JournalPosition(1, 32)),
            (change, token) => Task.CompletedTask,
            (position, token) => { if (position.NextUsn == 32) rewoundSettled = true; return Task.CompletedTask; }, ct);
        release.TrySetResult();
        await readAhead.WaitAsync(ct);
        await rewound.WaitAsync(ct);
        Check.Require(rewoundSettled, "The rewound record did not settle after the pause");
        Check.Require(!readAheadSettled, "A pre-pause read-ahead admission settled the record");
    });

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

    // WRITABLE-SMALL-HOOK, harness/designer-owned: attach the existing SQLite statement and row-change observer to
    // the fixture Store connections used in each phase. Left NI for that lane.
    public static Task PerPhaseSqlAndRowCountsAsync(int size) => throw new NotImplementedException();
    // Durable bytes per item: a disposable real Store image is measured with its items and with twice its items. The
    // marginal bytes for the extra items must stay within one SQLite page each (the platform's own sizing, R136);
    // no product-declared per-item number is invented.
    public static async Task DurableBytesPerItemAsync(int size)
    {
        var count = Math.Min(size, 512);
        var (doubled, pageSize) = await DurableImageAsync(2 * count);
        var (single, _) = await DurableImageAsync(count);
        AtMost(doubled - single, (long)count * pageSize, "durable bytes per added item");
        Check.Require(doubled > single, "Durable image did not grow with its items");
    }
    // Scratch bytes per item: the derived .scan image holds one row per listed entry and stays within one SQLite page
    // per row (the platform's own sizing); ClearScan then removes every row.
    public static async Task ScratchBytesPerItemAsync(int size)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var image = Path.Combine(dir.FullName, "fixture.sqlite");
            await CreateStoreImageAsync(image);
            var pair = ItemChecks.Pair();
            using var store = new global::Massdex.SyncShark.Engine.Storage.Store(image, false, ItemChecks.Settings);
            await store.SavePairAsync(pair, default);
            var scan = Guid.NewGuid();
            var count = Math.Min(size, 512);
            var paths = Enumerable.Range(0, count).Select(i => $"s/{i:D6}").ToArray();
            await store.AddScanAsync(pair.PairId, scan, Side.Local, Rows(paths), null, default);
            var scratch = image + ".scan";
            AtMost(OnDiskBytes(scratch), (long)count * await StoragePageSizeAsync(scratch), "scratch bytes per listed item");
            Check.Require((await ScratchPathsAsync(image)).Count == count, "Scratch scan did not record one row per listed item");
            await store.ClearScanAsync(pair.PairId, scan, default);
            Check.Require((await ScratchPathsAsync(image)).Count == 0, "Scratch scan rows survived ClearScan");
        }
        finally { dir.Delete(true); }
        static async IAsyncEnumerable<Listed> Rows(string[] paths)
        {
            await Task.CompletedTask;
            foreach (var path in paths) yield return new(path, ItemChecks.File(Side.Local, path, 1, "id-" + path));
        }
    }
    private static async Task CreateStoreImageAsync(string image)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = image, Pooling = false }.ToString());
        await db.OpenAsync();
        db.CreateFunction<string, string>("pathkey", PathNames.Key, isDeterministic: true);
        using var schema = db.CreateCommand();
        var version = global::Massdex.SyncShark.Engine.Storage.Schema.Version;
        schema.CommandText = global::Massdex.SyncShark.Engine.Storage.Schema.Create + $"PRAGMA user_version={version};";
        await schema.ExecuteNonQueryAsync();
    }
    private static async Task<(long Bytes, long PageSize)> DurableImageAsync(int count)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var image = Path.Combine(dir.FullName, "fixture.sqlite");
            await CreateStoreImageAsync(image);
            var pair = ItemChecks.Pair();
            using (var store = new global::Massdex.SyncShark.Engine.Storage.Store(image, false, ItemChecks.Settings))
            {
                await store.SavePairAsync(pair, default);
                for (int i = 0; i < count; i++)
                {
                    var a = ItemChecks.File(Side.Local, $"durable-{i}", 1, $"dl-{i}");
                    var b = ItemChecks.File(Side.Dropbox, $"durable-{i}", 1, $"dr-{i}");
                    await store.CommitAsync(pair.PairId, null, SyncedEntries.From(a, b), null, null, default);
                }
            }
            return (OnDiskBytes(image), await StoragePageSizeAsync(image));
        }
        finally { dir.Delete(true); }
    }
    private static async Task<long> StoragePageSizeAsync(string image)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = image, Pooling = false }.ToString());
        await db.OpenAsync();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA page_size;";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
    private static long OnDiskBytes(string image)
    {
        long total = 0;
        foreach (var path in new[] { image, image + "-wal", image + "-shm" })
        {
            var file = new FileInfo(path);
            if (file.Exists) total += file.Length;
        }
        return total;
    }
    // COUNTER: window/waiter memory has no counter and section 7 certifies no memory bound; measuring live memory is
    // not a reproducible check and the doubles expose no measurement seam.
    public static Task WindowAndWaiterMemoryAsync(int size) => throw new NotImplementedException();

    private static string Hash(byte value)
    {
        using var hash = new ContentHash(); hash.Append([value]); return hash.Finish();
    }
}
