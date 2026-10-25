namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconcileSubtreeScopeOnly()
    {
        var folder = FixtureFolder("reconcile-subtree");
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("inside/file.txt", "before");
            fx.A.Seed("outside/file.txt", "before");
            await FirstSyncAsync(fx.Run(), fx);
            var unchangedOutside = await fx.B.ObserveAsync("outside/file.txt", default);
            var changed = fx.A.Seed("inside/file.txt", "after");
            fx.A.Seed("outside/file.txt", "after");
            var cursor = store.Cursor(fx.Pair.A.RootId);
            var report = await fx.Run().ReconcileScopeAsync(fx.Pair.PairId,
                new(null, new ReconciliationScope([], ["inside"]), ReconciliationTrigger.JournalReplaced),
                new RecordingProgress(), default);
            Require(report.Failed == 0 && !report.CompletionFailed &&
                (await fx.B.ObserveAsync(changed.Path, default))?.Hash == changed.Hash,
                "subtree reconciliation repairs its selected current contents");
            Require(await fx.B.ObserveAsync("outside/file.txt", default) == unchangedOutside &&
                store.Cursor(fx.Pair.A.RootId) == cursor,
                "subtree reconciliation neither repairs another subtree nor advances the root cursor");
        }
        finally { DeleteFixture(folder); }
    }
}
