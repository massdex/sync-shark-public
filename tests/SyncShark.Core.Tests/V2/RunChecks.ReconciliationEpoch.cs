namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationCompletesCapturedEpochOnly()
    {
        await ReconcileCapturedEpoch(false);
        await ReconcileCapturedEpoch(true);
    }

    private static async Task ReconcileCapturedEpoch(bool replaceDuringSelection)
    {
        var folder = FixtureFolder("reconcile-epoch-completion");
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("existing.txt", "same");
            await FirstSyncAsync(fx.Run(), fx);
            var gaps = (ILocalGapStore)store;
            var root = fx.Pair.A.RootId;
            gaps.BeginGap(root, new NtfsUsnCheckpoint(1, 0, 200, 0).Cursor, 100, true, protect: false);
            var captured = gaps.GapEpoch(root);
            Require(captured is not null && store.GapInventoryPending(root), "fixture starts with a pending gap epoch");
            var selections = 0;
            fx.Provider.AddInventory(fx.Pair.A, new ReconciliationBarrierInventory(fx.A, () =>
            {
                selections++;
                if (replaceDuringSelection)
                    gaps.BeginGap(root, new NtfsUsnCheckpoint(2, 0, 300, 0).Cursor, 200, false, protect: false);
                return Task.CompletedTask;
            }));
            var writes = fx.B.WriteCalls;
            var report = await fx.Run().ReconcileScopeAsync(fx.Pair.PairId,
                new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
            Require(selections == 1 && !report.CompletionFailed && report.Failed == 0,
                "unchanged inventory completes selection without a repair batch");
            Require(store.GapInventoryPending(root) == replaceDuringSelection,
                "completion clears only the inventory obligation captured before selection");
            Require((gaps.GapEpoch(root)?.Epoch != captured!.Epoch) == replaceDuringSelection,
                "completion retains the current epoch identity, including a replacement during selection");
            Require(fx.B.WriteCalls == writes, "epoch completion does not create provider effects");
        }
        finally { DeleteFixture(folder); }
    }
}
