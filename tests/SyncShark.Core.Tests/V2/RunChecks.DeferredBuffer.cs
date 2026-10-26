using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static async Task RunDeferredBufferCheckAsync()
    {
        var fx = Build();
        fx.A.Seed("held.txt", "local");
        fx.B.Seed("ordinary.txt", "cloud");
        var evidence = new DeferredInventory(fx.B) { Block = true };
        fx.Provider.AddInventory(fx.Pair.B, evidence);
        var settings = V2Settings.Default with
        {
            MaxWorkers = 64, ProgressInterval = TimeSpan.Zero, EnableRunInstrumentation = false,
            HeartbeatInterval = TimeSpan.Zero,
        };
        using var deadline = new CancellationTokenSource(GateTimeout);
        var plan = await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
        // RQ-150: explicitly exercise the bounded merged lane, not the direct executor path.
        plan = plan with { Actions = plan.Actions.Where(action => action.Path == "ordinary.txt").ToArray(),
            HeldDeletions = [new HeldDeletion("held.txt", Side.A)] };
        var executor = new BufferedResultProbe(settings.ResultBufferCapacity);
        var run = new SyncRun(fx.Store, executor, fx.Provider, settings, TimeProvider.System);
        using var progress = new PausedApplyProgress();
        var applying = Task.Run(() => run.ApplyAsync(fx.Pair.PairId, plan, progress, deadline.Token, ReviewedHeld(plan)));
        try
        {
            await evidence.Started.Task.WaitAsync(deadline.Token);
            await progress.Paused.Task.WaitAsync(deadline.Token);
            await executor.Saturated.Task.WaitAsync(deadline.Token);
            var exceeded = false;
            try { await executor.Overflow.Task.WaitAsync(TimeSpan.FromMilliseconds(100)); exceeded = true; }
            catch (TimeoutException) { }
            Require(!exceeded, "real deferred execution does not drain results into an unbounded second queue");
        }
        finally
        {
            progress.Release.Set();
            evidence.Release.TrySetResult();
        }
        var report = await applying.WaitAsync(deadline.Token);
        Require(report.Skipped == 300, "deferred result buffer drains every ordinary outcome without loss");
    }

    private sealed class PausedApplyProgress : IProgress<RunProgress>, IDisposable
    {
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public void Report(RunProgress value)
        {
            if (value.Phase != RunPhases.Applying || value.Path != "ordinary.txt" || !Paused.TrySetResult()) return;
            if (!Release.Wait(GateTimeout)) throw new TimeoutException("result consumer was not released");
        }
        public void Dispose() => Release.Dispose();
    }

    private sealed class BufferedResultProbe(int capacity) : IExecutor
    {
        public TaskCompletionSource Saturated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Overflow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            if (!plan.Actions.Any(action => action.Path == "ordinary.txt")) yield break;
            for (var i = 0; i < 300; i++)
            {
                ct.ThrowIfCancellationRequested();
                // One consumed result, capacity queued, one pending write: requesting any more proves overflow.
                if (i == capacity + 1) Saturated.TrySetResult();
                if (i > capacity + 1) Overflow.TrySetResult();
                yield return new ActionResult(i == 0 ? plan.Actions[0].ActionId : "expanded-" + i,
                    Outcome.Skipped, null, null, null, "test superseded");
            }
        }
        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            Task.FromResult<IReadOnlyList<ActionResult>>([]);
    }
}
