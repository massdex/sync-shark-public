namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationSustainedAutoSync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("reconciliation-sustained"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        for (var index = 0; index < 6; index++) fx.A.Seed($"bulk-{index}.txt", "original");
        fx.A.Seed("user.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        for (var index = 0; index < 6; index++) fx.A.Seed($"bulk-{index}.txt", "gap change");
        var cursor = (await fx.A.FullAsync(0, _ => true, SilentProgress, default)).CursorAfter;
        ((ILocalGapStore)store).BeginGap(fx.Pair.A.RootId, PriorityCheckpoint.Cursor, 100, false, protect: false);
        var generation = store.BeginGeneration(fx.Pair.A.RootId, "fixture-retained-replay");
        store.CompleteGeneration(generation, cursor, true, settleInventory: false, cursorHeldForEvidence: false);
        var entered = Enumerable.Range(0, 6).Select(_ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = Enumerable.Range(0, 6).Select(_ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var reads = 0;
        var edits = 0;
        fx.Provider.AddInventory(fx.Pair.A, new ReconciliationBarrierInventory(fx.A, async () =>
        {
            var batch = Interlocked.Increment(ref reads) - 2;
            if (batch < 0) return; // Selection precedes the exact-path batches.
            if (batch == 6) return; // The unchanged user file also needs proof after an untrusted epoch.
            Require(batch < 6 && edits >= batch, "each later repair batch follows the intervening user edit");
            entered[batch].TrySetResult();
            await release[batch].Task.WaitAsync(TimeSpan.FromSeconds(20));
        }));
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
        var puts = fx.WriterA.Puts + fx.WriterB.Puts;
        var repair = run.ReconcileScopeAsync(fx.Pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
        try
        {
            for (var index = 0; index < 5; index++)
            {
                await entered[index].Task.WaitAsync(TimeSpan.FromSeconds(20));
                var edit = index;
                var auto = run.WithPairAdmissionAsync(fx.Pair.PairId, async () =>
                {
                    fx.A.Seed("user.txt", $"user edit {edit}");
                    var before = fx.WriterA.Puts + fx.WriterB.Puts;
                    var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                    Require(report.Failed == 0 && fx.WriterA.Puts + fx.WriterB.Puts == before + 1,
                        "each intervening user edit has exactly one copy effect");
                    Require((await fx.A.ObserveAsync("user.txt", default))?.Hash ==
                        (await fx.B.ObserveAsync("user.txt", default))?.Hash,
                        "the current user edit reaches the other root before repair resumes");
                    Require(store.PendingGapInventory(fx.Pair.A.RootId), "AutoSync remains useful while repair is active");
                    Interlocked.Increment(ref edits);
                    return report;
                }, default);
                release[index].TrySetResult();
                await auto.WaitAsync(TimeSpan.FromSeconds(20));
            }
            await entered[5].Task.WaitAsync(TimeSpan.FromSeconds(20));
            release[5].TrySetResult();
            await repair.WaitAsync(TimeSpan.FromSeconds(20));
            Require(edits == 5 && reads == 8 && fx.WriterA.Puts + fx.WriterB.Puts == puts + 11,
                "one run repairs six changed files, rechecks the retained user file and applies five edits without duplicate copies");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(fx.WriterA.Puts + fx.WriterB.Puts == puts + 11, "subsequent replay cannot duplicate settled edits");
            await MirrorAsync(fx);
            Require(store.ReadRunHistory(fx.Pair.PairId, 20).Count(row =>
                row.RunId.StartsWith("reconcile-", StringComparison.Ordinal)) == 1,
                "sustained AutoSync interleaves with one end-to-end reconciliation run");
        }
        finally
        {
            foreach (var barrier in release) barrier.TrySetResult();
            await run.StopAsync();
        }
    }
}
