namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    internal static async Task PlanningGapFolderDeleteAsync(string folder)
    {
        foreach (var edited in new[] { false, true })
        {
            var home = Path.Combine(folder, edited ? "edited" : "unchanged");
            using var store = SqliteStateStore.Open(home, StorageChecks.Fast, TimeProvider.System);
            using var h = new Harness();
            var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
            local.Seed("Folder", "", ItemKind.Folder);
            h.Wire.SeedFolder("Folder");
            foreach (var path in new[] { "Folder/One", "Folder/Two" })
            {
                local.Seed(path, "same");
                h.Wire.SeedFile(path, "same");
            }
            var pair = new PairDef("p1", "Pair A", new("ra", SyncProviders.Local, null, "/local", "id:ra"), Root(), [], [], true);
            store.SavePair(pair);
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, pair.PairId);
            var writer = h.Writer(Path.Combine(home, "spool"));
            var provider = new FakeEndpointProvider().Add(pair.A, local).AddInventory(pair.B, inventory).AddWriter(pair.B, writer);
            var settings = V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
            var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission()), provider, settings, TimeProvider.System);
            var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(provider), Path.Combine(home, "prepare"));
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, default);
            var initial = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { }, default);
            Require(initial.Failed == 0 && store.FirstSyncDone(pair.PairId), "gap folder fixture first sync completes");
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(pair.A.RootId, "cursor", 1, false, "synthetic gap");
            if (edited) local.Seed("Folder/One", "edited survivor");
            var victim = await writer.ObserveAsync("Folder", default);
            Require(victim is not null, "remote folder exists before user deletion");
            await h.Client.DeleteFoldersAsync([DropboxItems.ToEntry(victim!)], settings);
            var before = store.Cursor(RootId)!;
            h.Wire.SeedDeletedEntry("Folder");
            h.Wire.SeedChangePage(before, [h.Wire.ChangeDeleted("/Pilot/Folder")], false, "gap-after");
            h.Wire.SeedChangePage("gap-after", [], false, "gap-next");
            var held = await run.IncrementalAsync(pair.PairId, new Progress<RunProgress>(), default);
            var deferred = store.DeferredPlanningPage(pair.PairId);
            Require(deferred.Count(row => row.RootId == RootId && row.Path.StartsWith("Folder/", StringComparison.Ordinal)) == 2,
                "real Dropbox folder delta retains every removed descendant identity during the gap");
            Require(await local.ObserveAsync("Folder/One", default) is not null && await local.ObserveAsync("Folder/Two", default) is not null,
                "unpublished gap never propagates the hidden folder delete");
            var epoch = gaps.GapEpoch(pair.A.RootId)!;
            gaps.GapDiscoveredBatch(pair.A.RootId, epoch, store.Live(pair.A.RootId).Where(item => item.Kind == ItemKind.File).Select(item => (item, false)).ToArray());
            gaps.GapDiscoveryDone(pair.A.RootId, epoch);
            gaps.PromoteGapDiscovery(pair.A.RootId);
            var replay = await run.IncrementalAsync(pair.PairId, new Progress<RunProgress>(), default);
            Require(replay.Failed == 0, "folder-delete replay completes");
            Require((await local.ObserveAsync("Folder/One", default) is not null) == edited,
                "fresh deletion evidence removes unchanged descendants but preserves an edited survivor");
            Require(await local.ObserveAsync("Folder/Two", default) is null,
                "unchanged descendant deletion replays without a new provider delta");
        }
    }

    // Review 4 finding 2: a subtree re-read by a scan retry must join the dirty set, so an edit it
    // finds under a gap is retained and replayed instead of merely touched.
    internal static async Task PlanningGapScanRetryAsync(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        using var h = new Harness();
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("Folder", "", ItemKind.Folder);
        h.Wire.SeedFolder("Folder");
        var original = local.Seed("Folder/One", "same");
        local.Seed("Folder/Two", "same");
        h.Wire.SeedFile("Folder/One", "same");
        h.Wire.SeedFile("Folder/Two", "same");
        var pair = new PairDef("p1", "Pair A", new("ra", SyncProviders.Local, null, "/local", "id:ra"), Root(), [], [], true);
        store.SavePair(pair);
        var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, pair.PairId);
        var writer = h.Writer(Path.Combine(folder, "spool"));
        var provider = new FakeEndpointProvider().Add(pair.A, local).AddInventory(pair.B, inventory).AddWriter(pair.B, writer);
        var settings = V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission()), provider, settings, TimeProvider.System);
        var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(provider), Path.Combine(folder, "prepare"));
        await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, default);
        var initial = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { }, default);
        Require(initial.Failed == 0 && store.FirstSyncDone(pair.PairId), "gap scan-retry fixture first sync completes");
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(pair.A.RootId, "cursor", 1, false, "synthetic gap");
        // The edit is absent from the change feed: only the retried subtree re-read can find it.
        h.Wire.SeedFile("Folder/One", "edited during gap");
        store.SaveSyncRetry(pair.PairId, new("Folder", "sync", 1, "folder could not be read",
            DateTimeOffset.UnixEpoch, ReadRoots: new[] { RootId }));
        await run.IncrementalAsync(pair.PairId, new Progress<RunProgress>(), default);
        Require(store.DeferredPlanningPage(pair.PairId).Any(row => row.RootId == RootId && row.Path == "Folder/One"),
            "a Dropbox scan retry during a gap retains the edit it re-read");
        Require((await local.ObserveAsync("Folder/One", default))?.Hash == original.Hash,
            "an unpublished gap never propagates the re-read edit");
        store.ClearSyncRetry(pair.PairId, "Folder");
        var epoch = gaps.GapEpoch(pair.A.RootId)!;
        gaps.GapDiscoveredBatch(pair.A.RootId, epoch, store.Live(pair.A.RootId).Where(item => item.Kind == ItemKind.File).Select(item => (item, false)).ToArray());
        gaps.GapDiscoveryDone(pair.A.RootId, epoch);
        gaps.PromoteGapDiscovery(pair.A.RootId);
        var replay = await run.IncrementalAsync(pair.PairId, new Progress<RunProgress>(), default);
        Require(replay.Failed == 0 && store.CountDeferredPlanning(pair.PairId) == 0,
            "the scan-retry deferred work drains after the gap publishes");
        var synced = await local.ObserveAsync("Folder/One", default);
        Require(synced is not null && synced.Hash != original.Hash,
            "the edit found by a scan retry is replayed after the gap publishes");
    }
}
