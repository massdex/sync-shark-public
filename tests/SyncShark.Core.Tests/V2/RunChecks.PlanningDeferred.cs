namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task PlanningDeferredReplaysNextRunAsync()
    {
        await PlanningDeferredGapReplaysNextRunAsync();
        await PlanningDeferredEmptyPathAsync();
        await PlanningDeferredGapAndParentRetryAsync();
        await PlanningGapDropboxFixturesAsync();
        Console.WriteLine("PASS PlanningDeferredReplaysNextRun");
    }

    private static async Task PlanningDeferredGapReplaysNextRunAsync()
    {
        var folder = FixtureFolder("planning-replay");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Held/File", "original");
            fx.B.Seed("Held/File", "original");
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System);
            await FirstSyncAsync(run, fx);
            var local = store.Live(fx.Pair.A.RootId, ["Held/File"]).Single();
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(fx.Pair.A.RootId, "cursor", 1, false, "synthetic unpublished gap");
            fx.B.Seed("Held/File", "changed during gap");
            var held = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var deferred = store.AdmitDeferredPlanning(fx.Pair.PairId, DateTime.UtcNow.Ticks, rotate: false);
            Require(deferred.Count == 1 && deferred[0].RootId == fx.Pair.B.RootId,
                "SyncRun retains only the changed cloud entity under the local gap");
            // Review rc3-1 finding 2a: planning_deferred rows are not sync_retry debt and are not counted by
            // IncrementalOutcome, so a run whose only outstanding work is deferral rows still reports Complete.
            Require(held.Deferred == 0 && store.CountSyncRetries(fx.Pair.PairId) == 0,
                "verification deferrals alone never create scheduling debt");
            Require(held.Outcome == RunOutcomes.Complete,
                "verification deferrals alone never make the run Partial");
            var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
            gaps.GapDiscoveredBatch(fx.Pair.A.RootId, epoch, new[] { (local, false) });
            gaps.GapDiscoveryDone(fx.Pair.A.RootId, epoch);
            gaps.PromoteGapDiscovery(fx.Pair.A.RootId);
            var replay = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(replay.Failures.Count == 0 && store.CountDeferredPlanning(fx.Pair.PairId) == 0,
                "the next SyncRun drains published deferred work without another provider delta");
            Require(store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != RehashProtection.Reason),
                "verification summary closes in the successful replay run");
            await MirrorAsync(fx);
        }
        finally { DeleteFixture(folder); }
    }

    // Review 4 finding 1: a gap and a parent-folder retry together still retain the hidden change.
    private static async Task PlanningDeferredGapAndParentRetryAsync()
    {
        var folder = FixtureFolder("planning-gap-retry");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Folder/File", "original");
            fx.B.Seed("Folder/File", "original");
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System);
            await FirstSyncAsync(run, fx);
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(fx.Pair.A.RootId, "cursor", 1, false, "synthetic unpublished gap");
            var edited = fx.B.Seed("Folder/File", "changed during gap");
            // A sleeping ancestor retry protects the whole folder, so this entity is hidden by both.
            store.SaveSyncRetry(fx.Pair.PairId, new("Folder", "sync", 1, "folder retry",
                TimeProvider.System.GetUtcNow().AddDays(1)));
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.AdmitDeferredPlanning(fx.Pair.PairId, DateTime.UtcNow.Ticks, rotate: false).Any(row => row.ObjectId == edited.ObjectId),
                "a gap plus a parent-folder retry still retains the changed hidden entity");
            store.ClearSyncRetry(fx.Pair.PairId, "Folder");
            var local = store.Live(fx.Pair.A.RootId, ["Folder/File"]).Single();
            var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
            gaps.GapDiscoveredBatch(fx.Pair.A.RootId, epoch, new[] { (local, false) });
            gaps.GapDiscoveryDone(fx.Pair.A.RootId, epoch);
            gaps.PromoteGapDiscovery(fx.Pair.A.RootId);
            var replay = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var synced = await fx.A.ObserveAsync("Folder/File", CancellationToken.None);
            Require(replay.Failures.Count == 0 && store.CountDeferredPlanning(fx.Pair.PairId) == 0 &&
                synced?.Hash == edited.Hash,
                "clearing the retry and publishing the gap replays the hidden change instead of losing it");
        }
        finally { DeleteFixture(folder); }
    }

    private static async Task PlanningDeferredEmptyPathAsync()
    {
        var folder = FixtureFolder("planning-empty");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System);
            // BeginGap deliberately does nothing for an empty stored root.
            fx.A.Seed("Anchor", "same");
            fx.B.Seed("Anchor", "same");
            await FirstSyncAsync(run, fx);
            var a = fx.A.Seed("Gone", "new");
            var b = fx.B.Seed("Gone", "new");
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(fx.Pair.A.RootId, "cursor", 1, false, "synthetic gap");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(store.CountDeferredPlanning(fx.Pair.PairId) > 0 &&
                store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == RehashProtection.Reason),
                "new hidden work has a verification summary before disappearing");
            await fx.A.DeleteAsync(new("remove-a", 6, ActionKind.Delete, Side.A, a.Path, null, null, a, false, null), default);
            await fx.B.DeleteAsync(new("remove-b", 6, ActionKind.Delete, Side.B, b.Path, null, null, b, false, null), default);
            var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
            gaps.GapDiscoveredBatch(fx.Pair.A.RootId, epoch, store.Live(fx.Pair.A.RootId)
                .Where(item => item.Kind == ItemKind.File).Select(item => (item, false)).ToArray());
            gaps.GapDiscoveryDone(fx.Pair.A.RootId, epoch);
            gaps.PromoteGapDiscovery(fx.Pair.A.RootId);
            Require(!gaps.HasGapProtection(fx.Pair.A.RootId), "empty-path fixture completes gap discovery before replay");
            var complete = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(complete.Failed == 0 && store.CountDeferredPlanning(fx.Pair.PairId) == 0 &&
                store.OpenNotices(fx.Pair.PairId).All(n => n.ReasonKey != RehashProtection.Reason),
                "complete run settles an empty touched path without a baseline and closes verification notice");
        }
        finally { DeleteFixture(folder); }
    }

    private static async Task PlanningGapDropboxFixturesAsync()
    {
        var folderDelete = FixtureFolder("planning-folder");
        try { await DropboxChecks.PlanningGapFolderDeleteAsync(folderDelete); }
        finally { DeleteFixture(folderDelete); }
        var scanRetry = FixtureFolder("planning-scan-retry");
        try { await DropboxChecks.PlanningGapScanRetryAsync(scanRetry); }
        finally { DeleteFixture(scanRetry); }
    }

    private static string FixtureFolder(string name) =>
        Path.Combine(PhysicalFixtureRoot, name + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteFixture(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
