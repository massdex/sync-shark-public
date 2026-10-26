namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Exercise the shared run/admission owners before adding monitor scheduling. Barriers,
    // rather than delays, put the AutoSync request between two admitted run batches.
    private static async Task ReconciliationAdmissionUsesCurrentBaselineAsync()
    {
        var fx = Build();
        using var store = fx.Store;
        fx.A.Seed("file.txt", "original");
        fx.B.Seed("file.txt", "original");
        var run = fx.Run();
        var planned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var first = run.WithPairAdmissionAsync(fx.Pair.PairId, async () =>
        {
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(plan.Actions.Any(action => action.Kind == ActionKind.Adopt),
                "first admitted batch plans an Adopt");
            planned.TrySetResult();
            await release.Task;
            await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default);
            order.Add("first");
            return true;
        }, default);
        await Task.WhenAny(planned.Task, first);
        if (!planned.Task.IsCompleted) await first;
        Baseline? updated = null;
        var auto = run.WithPairAdmissionAsync(fx.Pair.PairId, async () =>
        {
            fx.A.Seed("file.txt", "updated by change feed");
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(report.Failed == 0 && report.Uncertain == 0, "intervening AutoSync run settles");
            updated = store.Baselines(fx.Pair.PairId).Single();
            order.Add("auto");
            return true;
        }, default);
        var next = run.WithPairAdmissionAsync(fx.Pair.PairId, async () =>
        {
            Require(updated is not null, "queued AutoSync gets admission before the next batch");
            var before = fx.WriterA.Calls + fx.WriterB.Calls;
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var current = store.Baselines(fx.Pair.PairId).Single();
            Require(current.A == updated!.A && current.B == updated.B,
                "next batch retains AutoSync's current baseline rather than an earlier Adopt");
            Require(fx.WriterA.Calls + fx.WriterB.Calls == before,
                "next batch re-reading synced state performs no duplicate provider effect");
            order.Add("next");
            return true;
        }, default);
        try
        {
            Require(!auto.IsCompleted && !next.IsCompleted,
                "an admitted batch protects its entire plan-to-commit interval");
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, auto, next);
        Require(order.SequenceEqual(new[] { "first", "auto", "next" }),
            "AutoSync waits for one batch, not for the following reconciliation batch");
    }
}
