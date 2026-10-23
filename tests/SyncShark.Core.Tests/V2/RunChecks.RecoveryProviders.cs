namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task Incremental_LocalLocalDeleteCreatesRecoveryAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "local-local-recovery-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store, localLocal: true);
        fx.A.Seed("same.txt", "content");
        var run = fx.Run();
        var first = await FirstSyncAsync(run, fx);
        Require(first.Failed == 0, "local/local recovery fixture completes first sync");
        var deleted = await fx.B.ObserveAsync("same.txt", CancellationToken.None);
        await fx.B.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.B, "same.txt", null,
            null, deleted, false, null), CancellationToken.None);

        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Deleted == 1 && report.Failed == 0 && report.Outcome == RunOutcomes.Complete &&
            fx.WriterA.Deletes == 1 && fx.WriterA.Preserves == 1 &&
            await fx.A.ObserveAsync("same.txt", CancellationToken.None) is null,
            "equal-hash local/local delete preserves the victim before removing it");
        var recovery = store.Recoveries(fx.Pair.PairId).Single();
        Require(recovery.Side == Side.A && recovery.Path == "same.txt" && recovery.Hash == deleted!.Hash &&
            recovery.Size == deleted.Size && !string.IsNullOrWhiteSpace(recovery.Location),
            "local/local deletion creates a durable Recovery row with victim provenance");
    }
}
