using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationEveryItemAccountable()
    {
        var folder = FixtureFolder("reconcile-accountability");
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            var selected = fx.A.Seed("selected.txt", "current");
            SelectionSnapshot(store, fx.Pair.A, Side.A, [selected]);
            store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, selected)], urgent: true);
            var batch = BatchFor(selected);
            var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
                new RunOptions { Batch = batch });
            Require(report.Failed == 0 && batch.Paths.All(batch.Dispositions.ContainsKey),
                "each selected item receives an accountable disposition from completion");
            Require(batch.Dispositions[selected.Path].StartsWith("actioned:", StringComparison.Ordinal) &&
                store.CountDeferredPlanning(fx.Pair.PairId) == 0,
                "a completed selected copy is actioned and retires its planning debt");
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
                batch.Dispositions[selected.Path].Contains("Metadata access denied", StringComparison.Ordinal),
                "retained same-spelling work has one item notice and its observed blocker");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
                new RunOptions { Batch = BatchFor(selected) });
            Require(store.CountDeferredPlanning(fx.Pair.PairId) == 0 &&
                !store.OpenNotices(fx.Pair.PairId).Any(n => n.Path == selected.Path),
                "a fresh settled batch retires the debt and closes its blocker notice");
        }
        finally { DeleteFixture(folder); }
    }
}
