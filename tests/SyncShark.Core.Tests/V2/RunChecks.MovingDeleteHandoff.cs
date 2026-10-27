namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationMovingDeleteHandoffAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("moving-delete-handoff"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Folder", "", ItemKind.Folder);
        fx.A.Seed("Folder/Sub", "", ItemKind.Folder);
        fx.A.Seed("Folder/Sub/child.txt", "unchanged");
        await FirstSyncAsync(fx.Run(), fx);
        fx.B.RefuseNonEmptyFolderDelete = true;
        await fx.A.MoveAsync(new("rename", 25, ActionKind.Move, Side.A,
            "Moved", "Folder", null, null, false, null), default);
        foreach (var path in new[] { "Moved/Sub/child.txt", "Moved/Sub" })
            await fx.A.DeleteAsync(new("remove:" + path, 19, ActionKind.Delete, Side.A,
                path, null, null, null, false, null), default);
        fx.B.AppliedActions.Clear();
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
        var first = await run.ReconcileScopeAsync(fx.Pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
        Require(!first.CompletionFailed && fx.B.AppliedActions.Count == 1 &&
            fx.B.AppliedActions[0].Kind == ActionKind.Move && store.CountDeferredPlanning(fx.Pair.PairId) > 0 &&
            await fx.B.ObserveAsync("Moved/Sub/child.txt", default) is not null,
            "first Repair only moves the folder and retains deletion debt under both aliases");
        fx.B.AppliedActions.Clear();
        var second = await ((ISyncRun)run).AutoSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var child = fx.B.AppliedActions.FindIndex(action => action.Kind == ActionKind.Delete && action.Path == "Moved/Sub/child.txt");
        var folder = fx.B.AppliedActions.FindIndex(action => action.Kind == ActionKind.Delete && action.Path == "Moved/Sub");
        Require(!second.CompletionFailed && second.Failed == 0 && child >= 0 && folder > child &&
            store.CountDeferredPlanning(fx.Pair.PairId) == 0,
            "ordinary AutoSync replans per-child deletes after the move and retires debt only after completion");
        await MirrorAsync(fx);
    }
}
