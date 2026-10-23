namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static async Task ProviderNamesTransientAsync(string root)
    {
        var settings = StorageChecks.Fast with { MaxWorkers = 8, ProgressInterval = TimeSpan.Zero };
        var clock = new CompletionClock();
        using var store = SqliteStateStore.Open(root, settings, clock);
        var fx = Build(suppliedStore: store);
        for (var i = 0; i < 30; i++) fx.A.Seed($"refused-{i}", "unchanged");
        fx.A.Seed("ordinary", "normal retry");
        var ordinaryAttempts = 0;
        var refusedAttempts = 0;
        fx.WriterB.PutFailure = action =>
        {
            if (action.Path == "ordinary")
                return Interlocked.Increment(ref ordinaryAttempts) == 1 ? new IOException("temporary connection failure") : null;
            Interlocked.Increment(ref refusedAttempts);
            return new ProviderNameRefusedException(new IOException("structured test refusal"));
        };
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), fx.Provider,
            settings, clock);
        var first = await FirstSyncAsync(run, fx);
        Require(first.Failed == 1 && first.Uploaded == 0 && first.Skipped == 30 && first.Done == 0 &&
            store.FirstSyncDone("p1") && store.SyncRetryAt("p1", "ordinary") is not null,
            "decision 85: item failure remains failed and durable without blocking completion");
        Require(store.OpenNotices("p1").Any(n => n.ReasonKey == ProviderNameRefusedException.NoticeKey) &&
            fx.WriterA.Calls == 0 && fx.WriterB.Deletes == 0 &&
            (await MapAsync(fx.A)).Count == 31 && (await MapAsync(fx.B)).Count == 0,
            "refusal notice is visible; refused items and their parent receive no write or deletion");
        Require(store.UnresolvedIntents("p1").Count == 1 &&
            store.Live("ra").Count(item => item.Flags.HasFlag(ItemFlags.ProviderNameRefused)) == 30,
            "parallel refusal commits mark every item and atomically settle their intents");
        clock.Now += TimeSpan.FromMinutes(6);
        var second = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(second.Failed == 0 && second.Uploaded == 1 && store.FirstSyncDone("p1") &&
            ordinaryAttempts == 2 && refusedAttempts == 30,
            "transient failure retries, refused items never retry, and first sync finishes");
        Require(fx.WriterA.Calls == 0, "parallel refusal handling performs no local effects");
        Require(store.OpenNotices("p1").Count(n => n.ReasonKey == ProviderNameRefusedException.NoticeKey) == 30,
            "parallel refusals have one actionable notice per entity");
    }
}
