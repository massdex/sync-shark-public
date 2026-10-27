namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationMovingUnitAsync()
    {
        foreach (var destination in new[] { "Earlier", "Later" })
        {
            using var store = SqliteStateStore.Open(FixtureFolder("moving-unit"), StorageChecks.Fast,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Folder", "", ItemKind.Folder);
            for (var index = 0; index < 9; index++) fx.A.Seed($"Folder/{index}.txt", "unchanged");
            await FirstSyncAsync(fx.Run(), fx);
            var identities = store.LiveUnder(fx.Pair.B.RootId, ["Folder"])
                .ToDictionary(item => item.Path, item => item.ObjectId);
            await fx.A.MoveAsync(new SyncAction("seed-unit", 25, ActionKind.Move, Side.A,
                destination, "Folder", null, null, false, null), default);
            fx.B.AppliedActions.Clear();
            fx.A.HashedPaths.Clear(); fx.B.HashedPaths.Clear();
            var run = new SyncRun(store, fx.Executor, fx.Provider,
                Settings with { ReconciliationBatch = new(2, 1) }, TimeProvider.System);
            var report = await run.ReconcileScopeAsync(fx.Pair.PairId,
                new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
            Require(!report.CompletionFailed && report.Failed == 0 && report.Moved == 1 &&
                report.Uploaded == 0 && report.Downloaded == 0 && report.Deleted == 0 &&
                fx.B.AppliedActions.Count(action => action.Kind == ActionKind.Move) == 1 &&
                fx.B.AppliedActions.All(action => action.Kind is not (ActionKind.CopyNew or ActionKind.Delete)),
                "a dedicated folder unit exceeding MaxPaths completes as one move in one Repair run");
            foreach (var (path, identity) in identities)
                Require((await fx.B.ObserveAsync(PairPath.Rebase(path, "Folder", destination), default))?.ObjectId == identity,
                    "dedicated folder move preserves every target child identity");
            Require(fx.A.HashedPaths.Count == 0 && fx.B.HashedPaths.Count == 0,
                "unchanged children need no content reads to plan the atomic folder move");
            await MirrorAsync(fx);
        }
    }
}
