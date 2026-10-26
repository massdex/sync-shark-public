using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationAccountedBacklogDoesNotRestart()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("reconcile-accounted-backlog"),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var selected = fx.B.Seed("blocked.txt", "content") with { Hash = null };
        fx.B.InventoryWithoutHashes = true;
        fx.B.Unhashable.Add(selected.Path);
        SelectionSnapshot(store, fx.Pair.B, Side.B, [selected]);
        store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.B.RootId, selected)]);
        Require(store.HasPlanningBacklog(fx.Pair.PairId), "unaccounted no-baseline cloud row triggers repair");
        var run = fx.Run();
        await run.ReconcileScopeAsync(fx.Pair.PairId, StateMonitor.PlanningBacklog(), new RecordingProgress(), default);
        Require(store.CountDeferredPlanning(fx.Pair.PairId) > 0 &&
            store.UnresolvedNotices(fx.Pair.PairId).Any(notice => notice.Path == selected.Path),
            "unhashable cloud item retains planning debt with a visible reason");
        var walks = fx.B.FullCalls;
        Require(!store.HasPlanningBacklog(fx.Pair.PairId) &&
            !await run.MonitorAsync(fx.Pair.PairId, new RecordingProgress(), default) &&
            !await run.MonitorAsync(fx.Pair.PairId, new RecordingProgress(), default) && fx.B.FullCalls == walks,
            "two monitor wakes do not restart already-accounted planning debt");
        foreach (var notice in store.UnresolvedNotices(fx.Pair.PairId).Where(notice => notice.Path == selected.Path))
            if (notice.NoticeId is { } id) store.CloseNotice(id, "resolved");
        var sleeping = new SyncRetry(selected.Path, "planning", 1, "Content unavailable", DateTimeOffset.MaxValue);
        store.SaveSyncRetry(fx.Pair.PairId, sleeping);
        Require(!store.HasPlanningBacklog(fx.Pair.PairId), "sleeping retry independently accounts for backlog debt");
        store.SaveSyncRetry(fx.Pair.PairId, sleeping with { RetryAfter = DateTimeOffset.MinValue });
        Require(store.HasPlanningBacklog(fx.Pair.PairId), "a due retry without an open notice is eligible again");
    }

    private static Task ReconciliationDependencyReason()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("reconcile-dependency-reason"),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var folder = fx.A.Seed("Parent", "", ItemKind.Folder);
        var batch = BatchFor(folder);
        batch.DeferredFolders.Add(folder.Path);
        typeof(SyncRun).GetMethod("FinishReconciliationDisposition", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fx.Run(), [fx.Pair, batch]);
        var reason = MessageCatalog.Default.Template("reconcile-dependency-pending");
        Require(batch.Dispositions[folder.Path].Contains(reason, StringComparison.Ordinal) &&
            store.SyncRetryAt(fx.Pair.PairId, folder.Path)?.LastError == reason &&
            store.UnresolvedNotices(fx.Pair.PairId).Any(notice => notice.Detail.Contains(reason, StringComparison.Ordinal)),
            "folder dependency has a concrete disposition, durable retry and visible notice");
        var action = new SyncAction("deferred-move", 25, ActionKind.Move, Side.B, "Moved", "Parent", folder, folder, false, null);
        var plan = new Plan(fx.Pair.PairId, 0, [], []) { DeferredPlanningActions = [action] };
        typeof(SyncRun).GetMethod("RetainPlanningFailures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fx.Run(), [fx.Pair, plan, true]);
        Require(store.SyncRetryAt(fx.Pair.PairId, "Moved")?.LastError == reason,
            "the original planning retry producer names a dependency, not a failed post-pass");
        return Task.CompletedTask;
    }

    private static async Task ReconciliationEveryItemAccountable()
    {
        var folder = FixtureFolder("reconcile-accountability");
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            var selected = fx.A.Seed("selected.txt", "current");
            var gone = fx.A.Seed("gone.txt", "removed before repair");
            SelectionSnapshot(store, fx.Pair.A, Side.A, [selected, gone]);
            await fx.A.DeleteAsync(new SyncAction("remove-gone", 6, ActionKind.Delete, Side.A,
                gone.Path, null, null, gone, false, gone.ObjectId), default);
            store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, selected), (fx.Pair.A.RootId, gone)], urgent: true);
            var batch = BatchFor(selected, gone);
            var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
                new RunOptions { Batch = batch });
            Require(report.Failed == 0 && batch.Paths.All(batch.Dispositions.ContainsKey),
                "each selected item receives an accountable disposition from completion");
            Require(batch.Dispositions[selected.Path].StartsWith("actioned:", StringComparison.Ordinal) &&
                batch.Dispositions[gone.Path].StartsWith("settled:", StringComparison.Ordinal) &&
                store.CountDeferredPlanning(fx.Pair.PairId) == 0,
                "a copy is actioned and a verified empty path is settled, retiring both debts");
        }
        finally { DeleteFixture(folder); }
    }

    private static async Task ReconciliationRetainedNoticeResolves()
    {
        var folder = FixtureFolder("reconcile-retained-notice");
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            var selected = fx.A.Seed("selected.txt", "same");
            fx.B.Seed(selected.Path, "same");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, selected)], urgent: true);
            var page = store.AdmitDeferredPlanning(fx.Pair.PairId, DateTime.UtcNow.Ticks);
            var batch = BatchFor(selected);
            var side = new SyncRun.SideScan(1, "cursor", true, false, [], [],
                new Dictionary<string, DateTimeOffset?>(), []);
            var blocked = side with { IncompleteSubtrees = [selected.Path],
                IncompleteReasons = new Dictionary<string, string> { [selected.Path] = "Metadata access denied" } };
            var acknowledge = typeof(SyncRun).GetMethod("AcknowledgeVerifiedRehash", BindingFlags.Instance | BindingFlags.NonPublic)!;
            // Same spelling, no prior notice: the actual completion owner observes a failed exact read.
            acknowledge.Invoke(run, [fx.Pair, new SyncRun.BothScan(blocked, side),
                new Plan(fx.Pair.PairId, 1, [], []), new SyncRun.Tally(Settings),
                (Func<string, bool>)(_ => false), (Func<string, bool>)(_ => true), (Func<string, bool>)(_ => true),
                CancellationToken.None, page, null, batch]);
            var notices = store.OpenNotices(fx.Pair.PairId).Where(n => n.Path == selected.Path).ToArray();
            Require(store.CountDeferredPlanning(fx.Pair.PairId) == 1 && notices.Length == 1 &&
                notices[0].Detail.Contains("Metadata access denied", StringComparison.Ordinal) &&
                batch.Dispositions[selected.Path].Contains("Metadata access denied", StringComparison.Ordinal),
                "retained same-spelling work has one item notice and its observed blocker");
            run.SaveFailedRetry(fx.Pair, selected.Path, "Metadata access denied", null);
            Require(store.SyncRetryAt(fx.Pair.PairId, selected.Path)?.Kind == "sync",
                "retained work uses the ordinary durable failure owner");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
                new RunOptions { Batch = BatchFor(selected) });
            Require(store.CountDeferredPlanning(fx.Pair.PairId) == 0 &&
                store.SyncRetryAt(fx.Pair.PairId, selected.Path) is null &&
                !store.OpenNotices(fx.Pair.PairId).Any(n => n.Path == selected.Path),
                "a fresh settled batch retires the debt and closes its blocker notice");
        }
        finally { DeleteFixture(folder); }
    }
}
