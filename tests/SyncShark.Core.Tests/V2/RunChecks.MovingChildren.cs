namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationMovingChildrenAsync()
    {
        foreach (var mode in new[] { "added", "deleted", "converged", "failed" })
        {
            using var store = SqliteStateStore.Open(FixtureFolder("moving-children"), StorageChecks.Fast,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Folder", "", ItemKind.Folder);
            fx.A.Seed("Folder/keep.txt", "keep");
            fx.A.Seed("Folder/child.txt", "child");
            await FirstSyncAsync(fx.Run(), fx);
            var move = new SyncAction("seed-move", 25, ActionKind.Move, Side.A, "Moved", "Folder", null, null, false, null);
            await fx.A.MoveAsync(move, default);
            if (mode is "added" or "failed") fx.A.Seed("Moved/new.txt", "new");
            if (mode == "deleted") await fx.A.DeleteAsync(new("seed-delete", 19, ActionKind.Delete,
                Side.A, "Moved/child.txt", null, null, null, false, null), default);
            if (mode == "converged") await fx.B.MoveAsync(move with { Target = Side.B }, default);
            fx.B.AppliedActions.Clear();
            if (mode == "failed") fx.B.FailNextWrites = 100;
            var run = new SyncRun(store, fx.Executor, fx.Provider,
                Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
            var report = await run.ReconcileScopeAsync(fx.Pair.PairId,
                new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
            if (mode == "failed")
                Require(fx.B.AppliedActions.Count == 0 && store.CountDeferredPlanning(fx.Pair.PairId) > 0,
                    "failed folder Move releases no child effects and retains debt");
            else
            {
                Require(!report.CompletionFailed && report.Failed == 0 && report.Deferred == 0,
                    "rename with child changes or already-applied target converges in one Repair run: " + mode);
                if (mode == "deleted") Require(fx.B.AppliedActions.FindIndex(a => a.Kind == ActionKind.Move) <
                    fx.B.AppliedActions.FindIndex(a => a.Kind == ActionKind.Delete && a.Path == "Moved/child.txt"),
                    "child delete runs after the folder move at the destination path");
                await MirrorAsync(fx);
            }
        }
    }
}
