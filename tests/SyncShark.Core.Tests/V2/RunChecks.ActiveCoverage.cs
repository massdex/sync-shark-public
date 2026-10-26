namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task AutoSyncInScopeFreshChange()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "active-coverage-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Parent/file.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Provider.AddInventory(fx.Pair.A, new ReconciliationBarrierInventory(fx.A, async () =>
        { entered.TrySetResult(); await release.Task; }));
        var run = fx.Run();
        var repair = run.ReconcileScopeAsync(fx.Pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fx.A.Seed("Parent/file.txt", "fresh change");
            var before = fx.WriterA.Calls + fx.WriterB.Calls;
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Require(report.Failed == 0 && fx.WriterA.Calls + fx.WriterB.Calls == before + 1,
                "fresh in-scope change belongs to AutoSync while selection is blocked");
            var after = fx.WriterA.Calls + fx.WriterB.Calls;
            release.TrySetResult();
            await repair;
            Require(fx.WriterA.Calls + fx.WriterB.Calls == after,
                "reconciliation re-reads AutoSync's settled state without a duplicate effect");
            await MirrorAsync(fx);
        }
        finally { release.TrySetResult(); await run.StopAsync(); }
    }

    private static async Task ReconciliationFolderMoveAcrossBatches()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("reconcile-folder-batches"),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Parent", "", ItemKind.Folder);
        fx.A.Seed("Parent/first.txt", "first");
        fx.A.Seed("Parent/last.txt", "last");
        await FirstSyncAsync(fx.Run(), fx);
        await fx.A.MoveAsync(new SyncAction("seed-move", 25, ActionKind.Move, Side.A,
            "Moved", "Parent", null, null, false, null), default);
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
        var report = await run.ReconcileScopeAsync(fx.Pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
        Require(!report.CompletionFailed && report.Failed == 0 && report.Moved > 0 &&
            report.Uploaded == 0 && report.Downloaded == 0 && report.Deleted == 0,
            "folder split from later child batches remains a move, not copy/delete");
        await MirrorAsync(fx);
        Require(!store.UnresolvedNotices(fx.Pair.PairId).Any(notice => notice.Path == "Moved" || notice.Path == "Parent"),
            "successful deferred folder pass leaves no unresolved dependency notice");
    }

    private static Task AutoSyncInScopeDependencies()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "coverage-dependency-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("Parent/file.txt", "content");
        var folder = fx.A.Seed("Parent", "", ItemKind.Folder);
        var a = new SyncRun.SideScan(1, "cursor", true, false, [item.Path], [],
            new Dictionary<string, DateTimeOffset?>(), []);
        var b = a with { ChangePaths = [] };
        var decision = new StateMonitorDecision(null, new([], ["Parent"]), ReconciliationTrigger.JournalReplaced);
        var coverage = new ReconciliationCoverage(decision, true, "session", store, new HashSet<Side> { Side.A }, new(a, b));
        Require(!coverage.Unknown(Side.A, item.Path) && coverage.Unknown(Side.A, "Parent/unseen.txt") &&
            !coverage.Unknown(Side.A, "Other/file.txt"), "only unobserved scoped paths are unknown");
        var move = new SyncAction("move", 1, ActionKind.Move, Side.A, "Other", "Parent", folder, folder, false, null);
        var update = new SyncAction("update", 1, ActionKind.Update, Side.B, item.Path, null, item, null, false, null);
        var plan = coverage.DeferDependencies(new(fx.Pair.PairId, 0, [move, update], []));
        Require(plan.Actions.SequenceEqual([update]) && plan.DeferredPlanningActions.SequenceEqual([move]),
            "folder dependency defers while an independent fresh update stays owned by AutoSync");
        store.StageReconcile("session:work", Side.A, [item]);
        var repairing = new ReconciliationCoverage(decision, false, "session", store, new HashSet<Side> { Side.A }, new(b, b));
        repairing.Load(new([item], [], []));
        Require(repairing.Unknown(Side.A, item.Path) && !repairing.Unknown(Side.A, "Parent/unselected.txt"),
            "repairing coverage narrows to selected staged paths");
        store.StageReconcile("session:deferred", Side.A, [folder]);
        repairing.Load(new([item, folder], [], []));
        Require(repairing.Unknown(Side.A, folder.Path) && repairing.DeferDependencies(new(fx.Pair.PairId, 0, [move], [])).Actions.Count == 0,
            "deferred folder staging remains unknown between repair passes");
        b.ExactCreationObserved.Add(item.Path);
        Require(!repairing.Unknown(Side.A, item.Path), "later exact-path observation overrides staged uncertainty");
        return Task.CompletedTask;
    }
}
