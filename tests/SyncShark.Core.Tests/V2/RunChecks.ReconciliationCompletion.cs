namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconcileSubtreeScopeOnly()
    {
        await ReconcileDeferredFolderRetry(false);
        await ReconcileDeferredFolderRetry(true);
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

    private static async Task ReconcileDeferredFolderRetry(bool persistent)
    {
        var folder = FixtureFolder("reconcile-folder-retry");
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("existing.txt", "unchanged");
            await FirstSyncAsync(fx.Run(), fx);
            fx.A.Seed("Parent", "", ItemKind.Folder);
            var passes = 0;
            Planner.PostPassFault.Value = () =>
            {
                passes++;
                if (persistent || passes == 1) throw new InvalidOperationException("injected folder planning failure");
            };
            try
            {
                var report = await fx.Run().ReconcileScopeAsync(fx.Pair.PairId,
                    new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced),
                    new RecordingProgress(), default);
                Require(!report.CompletionFailed && passes == 2,
                    "deferred folder receives a fresh retry and stops after a no-progress pass");
                Require((await fx.B.ObserveAsync("Parent", default) is not null) == !persistent,
                    "only a successfully replanned folder executes");
                Require((store.SyncRetryAt(fx.Pair.PairId, "Parent") is not null) == persistent,
                    "unsettled folder retains durable retry; successful replanning retires it");
                Require(store.ReadRunHistory(fx.Pair.PairId, 10).Count(row =>
                    row.RunId.StartsWith("reconcile-", StringComparison.Ordinal)) == 1,
                    "deferred passes remain inside one reconciliation run");
            }
            finally { Planner.PostPassFault.Value = null; }
        }
        finally { DeleteFixture(folder); }
    }
}
