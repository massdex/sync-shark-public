namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationExactLocalDeletionAsync()
    {
        var folder = FixtureFolder("exact-local-deletion");
        var directory = Path.Combine(folder, "root");
        LocalChecks.Seed(directory, "Parent/child.txt", "original bytes");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = await LocalCaseFixtureAsync(folder, store);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var selected = store.Live(fx.Pair.A.RootId, ["Parent"]).Single();
        // Moving fixture-owned content outside the root proves absence without discarding bytes.
        Directory.Move(Path.Combine(directory, "Parent"), Path.Combine(folder, "retained"));
        fx.B.AppliedActions.Clear();
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { Batch = BatchFor(selected) });
        Require(report.Failed == 0 && report.Deleted > 0 &&
            await fx.B.ObserveAsync("Parent", default) is null &&
            await fx.B.ObserveAsync("Parent/child.txt", default) is null,
            "an exact missing local folder propagates its proven subtree deletion");
        Require(store.LiveUnder(fx.Pair.A.RootId, ["Parent"]).Count == 0 &&
            !Directory.Exists(Path.Combine(directory, "Parent")) &&
            fx.B.AppliedActions.All(action => action.Kind != ActionKind.MkDir) &&
            store.OpenNotices(fx.Pair.PairId).Count == 0,
            "local descendant deletes retire together without KeepFolder or a false notice");
    }
}
