namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task SelectionRetryPublishesAfterAutoSync()
    {
        await InvalidationWaitsForAutoSync();
        using var store = SqliteStateStore.Open(FixtureFolder("selection-retry-admission"),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("file.txt", "same");
        fx.B.Seed("file.txt", "same");
        var inventory = new ScriptedInventory(fx.A);
        fx.Provider.AddInventory(fx.Pair.A, inventory);
        await using var owner = new GapRecoveryRun(fx.Run());
        var run = owner.Run;
        await FirstSyncAsync(run, fx);
        CheckScopedRetryGate(store, fx, run);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = store.Live(fx.Pair.A.RootId).Single(item => item.Path == "file.txt");
        inventory.FullError = new System.ComponentModel.Win32Exception("selection failed while AutoSync held admission");
        inventory.OnFull = () => failed.TrySetResult();
        var auto = run.WithPairAdmissionAsync(fx.Pair.PairId, async () =>
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Require(store.SyncRetryAt(fx.Pair.PairId, "") is null,
                "selection has not published read failure while an older AutoSync owns admission");
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(!report.CompletionFailed, "the older admitted AutoSync completes before selection publishes");
            return true;
        }, default);
        var repair = run.ReconcileScopeAsync(fx.Pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.ScheduledConsistency),
            new RecordingProgress(), default);
        await Task.WhenAll(auto, repair).WaitAsync(TimeSpan.FromSeconds(30));
        Require(store.SyncRetryAt(fx.Pair.PairId, "") is { Kind: "scan" } &&
            store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "" && notice.ReasonKey == "sync-retry") &&
            store.ReadRunHistory(fx.Pair.PairId, 10).Any(row => row.RunId.StartsWith("reconcile-", StringComparison.Ordinal) &&
                row.Outcome == RunOutcomes.Partial),
            "selection publishes under admission after AutoSync: retry and notice survive and reconciliation is Partial");
        Require(store.ByObject(fx.Pair.A.RootId, before.ObjectId)!.Hash == before.Hash &&
            store.RehashPendingCount(fx.Pair.A.RootId) == 0,
            "an unlocalized enumeration exception retains unrelated cached proof rather than invalidating the whole root");
    }
}
