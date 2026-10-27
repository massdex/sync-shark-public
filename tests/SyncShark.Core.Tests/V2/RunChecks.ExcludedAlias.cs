namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationExcludedAliasAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("reconcile-excluded-alias"),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var original = fx.A.Seed("original.txt", "content");
        await FirstSyncAsync(fx.Run(), fx);
        var pair = fx.Pair with { Excluded = ["excluded"] };
        store.SavePair(pair);
        await fx.A.MoveAsync(new SyncAction("excluded-move", 1, ActionKind.Move, Side.A,
            "excluded/moved.txt", original.Path, null, null, false, null), default);
        // Persist the moved identity so alias closure sees both old and excluded spellings.
        var current = (await fx.A.ObserveAsync("excluded/moved.txt", default))!;
        store.WriteSnapshot(new(pair.A.RootId, Side.A, store.BeginGeneration(pair.A.RootId, "delta"), "", [current], []));
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
        var report = await run.ReconcileScopeAsync(pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.ScheduledConsistency), new RecordingProgress(), default);
        Require(!report.CompletionFailed && report.Failed == 0 &&
            !store.UnresolvedNotices(pair.PairId).Any(notice => notice.Detail.Contains(
                MessageCatalog.Default.Template("reconcile-dependency-pending"), StringComparison.Ordinal)),
            "an excluded move alias is not deferred forever as an unobserved reconciliation dependency");
        Require((await fx.A.ObserveAsync("excluded/moved.txt", default))?.Hash == current.Hash,
            "reconciliation never mutates the excluded content");
    }
}
