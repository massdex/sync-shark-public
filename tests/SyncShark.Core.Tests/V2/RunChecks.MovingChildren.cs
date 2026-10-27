namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationMovingChildrenAsync()
    {
        foreach (var mode in new[] { "added", "unsynced", "deleted", "converged", "failed", "replacement" })
        {
            using var store = SqliteStateStore.Open(FixtureFolder("moving-children"), StorageChecks.Fast,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Folder", "", ItemKind.Folder);
            fx.A.Seed("Folder/keep.txt", "keep");
            fx.A.Seed("Folder/child.txt", "child");
            await FirstSyncAsync(fx.Run(), fx);
            if (mode == "unsynced")
            {
                var added = fx.A.Seed("Folder/new.txt", "new");
                var gen = store.BeginGeneration(fx.Pair.A.RootId, "delta");
                var cursor = store.Cursor(fx.Pair.A.RootId)!;
                store.WriteDelta(new(fx.Pair.A.RootId, Side.A, gen, cursor, cursor,
                    [new(ChangeKind.Upsert, added.ObjectId, added.Path, added, null)], false, []));
                store.CompleteGeneration(gen, cursor, true);
            }
            var move = new SyncAction("seed-move", 25, ActionKind.Move, Side.A, "Moved", "Folder", null, null, false, null);
            await fx.A.MoveAsync(move, default);
            if (mode is "added" or "failed") fx.A.Seed("Moved/new.txt", "new");
            if (mode is "deleted" or "replacement") await fx.A.DeleteAsync(new("seed-delete", 19, ActionKind.Delete,
                Side.A, "Moved/child.txt", null, null, null, false, null), default);
            if (mode == "replacement") fx.A.Seed("Moved/child.txt", "replacement");
            if (mode == "converged") await fx.B.MoveAsync(move with { Target = Side.B }, default);
            fx.B.AppliedActions.Clear();
            if (mode == "failed") fx.B.FailNextWrites = 100;
            var run = new SyncRun(store, fx.Executor, fx.Provider,
                Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
            var report = await run.ReconcileScopeAsync(fx.Pair.PairId,
                new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
            if (mode == "failed")
                Require(fx.B.FailNextWrites < 100 && fx.B.AppliedActions.Count == 0 && store.CountDeferredPlanning(fx.Pair.PairId) > 0,
                    "failed folder Move releases no child effects and retains debt");
            else
            {
                Require(!report.CompletionFailed && report.Failed == 0 && report.Deferred == 0,
                    "rename with child changes or already-applied target converges in one Repair run: " + mode);
                var moved = fx.B.AppliedActions.FindIndex(a => a.Kind == ActionKind.Move);
                if (mode is "added" or "unsynced") Require(moved >= 0 &&
                    fx.B.AppliedActions.FindIndex(a => a.Kind == ActionKind.CopyNew && a.Path == "Moved/new.txt") > moved,
                    "new or previously observed partnerless child copies after the move");
                if (mode is "deleted" or "replacement")
                {
                    var removed = fx.B.AppliedActions.FindIndex(a => a.Kind == ActionKind.Delete && a.Path == "Moved/child.txt");
                    Require(moved >= 0 && removed > moved, "child delete follows successful move");
                    if (mode == "replacement") Require(fx.B.AppliedActions.FindIndex(a =>
                        a.Kind == ActionKind.CopyNew && a.Path == "Moved/child.txt") > removed,
                        "same-path replacement removes before creating after the move");
                }
                await MirrorAsync(fx);
            }
        }
    }
}
