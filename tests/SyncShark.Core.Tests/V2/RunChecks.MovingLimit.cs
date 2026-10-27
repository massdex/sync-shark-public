namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationMovingLimitAsync()
    {
        var directory = FixtureFolder("moving-limit");
        using var store = SqliteStateStore.Open(directory, StorageChecks.Fast,
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
        using (var reopened = SqliteStateStore.Open(directory, StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash))
            Require(reopened.CountDeferredPlanning(fx.Pair.PairId) == store.CountDeferredPlanning(fx.Pair.PairId) &&
                reopened.CountDeferredPlanning(fx.Pair.PairId) > 0,
                "deferred entity backlog survives reopening independently of temporary reconciliation staging");
        var notice = store.UnresolvedNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "reconciliation-moving-too-large");
        Require(notice.Path == "Moved" && NoticeChoices.Catalogue(notice).Any(option =>
            option.Label == "Will complete in a future version; nothing is lost. For now, rename the folder on the other side too, or reduce it below 8192 items.") &&
            !NoticeChoices.Catalogue(notice).Any(option => option.Choice == NoticeChoices.RetryNow),
            "oversized notice displays the new path and offers no ineffective Retry");
        await fx.A.MoveAsync(new("seed-second-move", 25, ActionKind.Move, Side.A,
            "RenamedAgain", "Moved", null, null, false, null), default);
        await run.ReconcileScopeAsync(fx.Pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
        var renamedNotice = store.UnresolvedNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "reconciliation-moving-too-large");
        Require(renamedNotice.NoticeId == notice.NoticeId && renamedNotice.Path == "RenamedAgain",
            "a second rename updates the identity-owned notice instead of creating a duplicate");
        for (var i = 0; i < V2Settings.SettleCandidateCap; i++)
            await fx.A.DeleteAsync(new("remove-extra-" + i, 19, ActionKind.Delete, Side.A,
                $"RenamedAgain/{i:D5}.txt", null, null, null, false, null), default);
        var settled = await run.ReconcileScopeAsync(fx.Pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
        Require(!settled.CompletionFailed && store.NoticeById(fx.Pair.PairId, notice.NoticeId!) is { Closed: true },
            "identity-owned oversized notice auto-clears when the reduced unit converges");
        await MirrorAsync(fx);
        store.AddNotices([renamedNotice]);
        foreach (var (side, endpoint) in new[] { (Side.A, fx.A), (Side.B, fx.B) })
            await endpoint.DeleteAsync(new("remove-folder", 19, ActionKind.Delete, side,
                "RenamedAgain", null, null, null, false, null), default);
        await run.ReconcileScopeAsync(fx.Pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
        Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!) is { Closed: true },
            "oversized notice clears when the folder is deleted on both sides");
    }
}
