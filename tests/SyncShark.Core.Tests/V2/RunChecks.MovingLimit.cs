namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationMovingLimitAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("moving-limit"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Folder", "", ItemKind.Folder);
        await FirstSyncAsync(fx.Run(), fx);
        await fx.A.MoveAsync(new("seed-move", 25, ActionKind.Move, Side.A,
            "Moved", "Folder", null, null, false, null), default);
        for (var i = 0; i < V2Settings.SettleCandidateCap; i++) fx.A.Seed($"Moved/{i:D5}.txt", "x");
        fx.B.AppliedActions.Clear();
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var report = await run.ReconcileScopeAsync(fx.Pair.PairId,
                new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
            Require(report.Deferred > 0 && fx.B.AppliedActions.Count == 0 && store.CountDeferredPlanning(fx.Pair.PairId) > 0,
                "oversized unit plans no member and retains counted deferred debt");
            Require(store.UnresolvedNotices(fx.Pair.PairId).Count(notice =>
                notice.Detail?.Contains("Folder rename too large for automatic repair:", StringComparison.Ordinal) == true) == 1,
                "oversized unit has one resolvable notice even after the repair tail and a repeated Repair");
        }
    }
}
