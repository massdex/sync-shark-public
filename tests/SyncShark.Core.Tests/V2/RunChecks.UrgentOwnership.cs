namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task UrgentUndecidableKeepsRetryOwnerAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("urgent-planning-owner"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("update.txt", "before");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var changed = fx.A.Seed("update.txt", "after");
        var gen = store.BeginGeneration(fx.Pair.A.RootId, "fixture");
        var snapshot = await fx.A.FullAsync(gen, _ => true, SilentProgress, default);
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(gen, snapshot.CursorAfter, true);
        store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, changed)], urgent: true);
        Planner.PostPassFault.Value = () => throw new InvalidOperationException("undecidable planning fixture");
        try { await ((ISyncRun)run).AutoSyncAsync(fx.Pair.PairId, new RecordingProgress(), default); }
        finally { Planner.PostPassFault.Value = null; }
        var retry = store.SyncRetryAt(fx.Pair.PairId, changed.Path);
        Require(retry is { Kind: "planning", Attempts: > 0 } && retry.RetryAfter > DateTimeOffset.UtcNow &&
            store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == "planning-degraded") &&
            store.IsUserChange(fx.Pair.A.RootId, changed.ObjectId),
            "undecidable urgent work retains its tag with a visible planning notice and backoff retry owner");
        store.SaveSyncRetry(fx.Pair.PairId, retry! with { RetryAfter = DateTimeOffset.MinValue });
        await ((ISyncRun)run).AutoSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.SyncRetryAt(fx.Pair.PairId, changed.Path) is null && store.UrgentPending(fx.Pair.PairId) == 0 &&
            (await fx.B.ObserveAsync(changed.Path, default))?.Hash == changed.Hash,
            "the existing retry owner later resolves urgent work without a new repair handoff");
    }
}
