namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task AutoSyncInScopeIntentHorizon()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "active-intents-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var old = fx.A.Seed("Parent/old.txt", "completed content");
        fx.B.Seed(old.Path, "completed content");
        var later = fx.A.Seed("Parent/later.txt", "pending content");
        var crashed = new SyncAction("crashed", 1, ActionKind.CopyNew, Side.B, old.Path, null, old, null, false, old.ObjectId);
        var fresh = new SyncAction("fresh", 1, ActionKind.CopyNew, Side.B, later.Path, null, later, null, false, later.ObjectId);
        store.RecordIntent(fx.Pair.PairId, crashed);
        var executor = new IntentCaptureExecutor(fx.Executor, () => store.RecordIntent(fx.Pair.PairId, fresh));
        var run = new SyncRun(store, executor, fx.Provider, Settings, TimeProvider.System);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { Batch = BatchFor(old, later) });
        Require(executor.Seen.SequenceEqual([crashed.ActionId]), "batch recovery sees only intents captured at its admission");
        Require(store.UnresolvedIntents(fx.Pair.PairId).Select(item => item.ActionId).SequenceEqual([fresh.ActionId]),
            "the crashed in-scope intent settles, while the intent created after capture remains owned by its run");
        Require(fx.WriterA.Calls + fx.WriterB.Calls == 0, "recovery observes a completed effect without replay or a new effect");
    }

    private sealed class IntentCaptureExecutor(IExecutor inner, Action afterCapture) : IExecutor
    {
        internal List<string> Seen { get; } = [];
        public IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b, CancellationToken ct) =>
            inner.ApplyAsync(plan, a, b, ct);
        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null)
        {
            Seen.AddRange(intents.Select(item => item.ActionId));
            afterCapture();
            return inner.ReconcileAsync(intents, gen, a, b, ct, progress);
        }
    }
}
