namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationMovingDeleteSafetyAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("moving-delete-safety"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Folder", "", ItemKind.Folder);
        fx.A.Seed("Folder/Sub", "", ItemKind.Folder);
        fx.A.Seed("Folder/Sub/child.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        var original = (await fx.B.ObserveAsync("Folder", default))!;
        var sub = (await fx.B.ObserveAsync("Folder/Sub", default))!;
        await fx.A.MoveAsync(new("seed-move", 25, ActionKind.Move, Side.A, "Moved", "Folder", null, null, false, null), default);
        await fx.A.DeleteAsync(new("seed-child", 19, ActionKind.Delete, Side.A, "Moved/Sub/child.txt", null, null, null, false, null), default);
        await fx.A.DeleteAsync(new("seed-sub", 19, ActionKind.Delete, Side.A, "Moved/Sub", null, null, null, false, null), default);
        var edited = fx.B.Seed("Folder/Sub/child.txt", "edited on target");
        fx.B.AppliedActions.Clear();
        var moved = (await fx.A.ObserveAsync("Moved", default))!;
        var move = new SyncAction("move", 25, ActionKind.Move, Side.B, "Moved", "Folder", moved, original, false, null);
        var delete = new SyncAction("delete-sub", 19, ActionKind.Delete, Side.B, "Moved/Sub", null, null,
            sub with { Path = "Moved/Sub" }, false, null) { AfterFolderMove = move.ActionId };
        var results = new List<ActionResult>();
        await foreach (var result in fx.Executor.ApplyAsync(new(fx.Pair.PairId, 0, [move, delete], []), fx.A, fx.B, default))
            results.Add(result);
        Require(results.Any(result => result.ActionId == move.ActionId && result.Outcome == Outcome.Done) &&
            results.Any(result => result.ActionId == delete.ActionId && result.Outcome == Outcome.Skipped),
            "dependent subfolder removal is skipped before expansion while its parent move commits");
        Require(!fx.B.AppliedActions.Any(action => action.Kind == ActionKind.Delete) &&
            (await fx.B.ObserveAsync("Moved/Sub/child.txt", default))?.Hash == edited.Hash,
            "pre-move expansion cannot delete an edited descendant");
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
        await run.ReconcileScopeAsync(fx.Pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
        Require((await fx.B.ObserveAsync("Moved/Sub/child.txt", default))?.Hash == edited.Hash &&
            store.UnresolvedNotices(fx.Pair.PairId).Count > 0 &&
            !fx.B.AppliedActions.Any(action => action.Kind == ActionKind.Delete && action.Path == "Moved/Sub/child.txt"),
            "next-run fresh planning retains the edited child rather than replaying the folder deletion");
    }
}
