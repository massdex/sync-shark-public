namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationCompletesCapturedEpochOnly()
    {
        await ReconcileCapturedEpoch(0);
        await ReconcileCapturedEpoch(1);
        await ReconcileCapturedEpoch(2);
    }

    private static async Task ReconcileCapturedEpoch(int replacements)
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
            var writes = fx.B.WriteCalls;
            fx.Provider.AddInventory(fx.Pair.A, new ReconciliationBarrierInventory(fx.A, () =>
            {
                selections++;
                if (selections == 2 && replacements > 0)
                {
                    using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False");
                    db.Open();
                    Require(Convert.ToInt64(StoreSchema.Scalar(db, null, "SELECT discovery_done FROM local_gap;")) == 1,
                        "replacement invalidation is committed before cycle two selection, not a stale cycle-one batch");
                    Require(store.RehashPendingCount(root) == 1 && fx.B.WriteCalls == writes,
                        "discarded cycle one performs no batch proof or provider effect");
                }
                if (selections <= replacements)
                    gaps.BeginGap(root, new NtfsUsnCheckpoint((ulong)(selections + 1), 0, 300, 0).Cursor, 200, false, protect: false);
                return Task.CompletedTask;
            }));
            var progress = new RecordingProgress();
            var report = await fx.Run().ReconcileScopeAsync(fx.Pair.PairId,
                new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), progress, default);
            Require(progress.Reports.Any(row => row.Phase == RunPhases.Invalidating) == (replacements > 0),
                "untrusted proof invalidation reaches the live progress sink");
            Require(progress.Reports.Any(row => row.Phase == RunPhases.RestartingSelection) == (replacements > 0),
                "epoch replacement reports the selection restart through the live progress sink");
            Require(selections == (replacements == 0 ? 1 : replacements == 1 ? 3 : 2) &&
                !report.CompletionFailed && report.Failed == 0,
                "one replacement restarts selection before one fresh batch; two replacements run no batches");
            Require(store.GapInventoryPending(root) == (replacements == 2),
                "completion acknowledges the replacement only after its own stable cycle");
            Require((gaps.GapEpoch(root)?.Epoch != captured!.Epoch) == (replacements > 0),
                "completion retains the current epoch identity rather than completing the superseded identity");
            if (replacements == 2)
                Require(report.Deferred > 0 && store.ReadRunHistory(fx.Pair.PairId, 10).Any(row =>
                    row.RunId.StartsWith("reconcile-", StringComparison.Ordinal) && row.Outcome == RunOutcomes.Partial),
                    "a second replacement ends the same end-to-end run Partial with the epoch pending");
            Require(fx.B.WriteCalls == writes, "epoch completion does not create provider effects");
        }
        finally { DeleteFixture(folder); }
    }
}
