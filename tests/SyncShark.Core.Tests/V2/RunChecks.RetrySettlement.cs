namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task Incremental_SplitBaselineRetrySettlesAsync()
    {
        var fx = Build();
        const string path = "Parent/capsules/work/item.json";
        fx.A.Seed(path, "original");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var originalKey = fx.Store.Baselines(fx.Pair.PairId).Single().ObjectKey;
        fx.A.Seed(path, "local-edit");
        fx.B.Seed(path, "cloud-edit");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var baseline = fx.Store.Baselines(fx.Pair.PairId).Single(item => item.ObjectKey == PlanIds.Split(originalKey, Side.B));
        fx.A.InventoryWithoutHashes = true;
        var gen = fx.Store.BeginGeneration(fx.Pair.A.RootId, "null-hash-retry");
        fx.Store.WriteSnapshot(await fx.A.FullAsync(gen, _ => true, null!, default));
        Require(fx.Store.Live(fx.Pair.A.RootId, [baseline.A.Path]).Single().Hash is null && baseline.B.Hash is not null,
            "retry fixture has a null local hash and a known remote baseline hash");
        fx.Store.SaveSyncRetry(fx.Pair.PairId, new SyncRetry(baseline.A.Path, "sync", 4,
            "effect outcome unknown", DateTimeOffset.MinValue));
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.Info, baseline.A.Path,
            "effect outcome unknown", "sync-retry")]);
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0, "settlement has no pending effect");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, baseline.A.Path) is null &&
            !fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.Path == baseline.A.Path && n.ReasonKey == "sync-retry"),
            "an evaluated unchanged split correspondence settles stale retry debt and closes its notice");
        Console.WriteLine("PASS Incremental_SplitBaselineRetrySettles");
    }
}
