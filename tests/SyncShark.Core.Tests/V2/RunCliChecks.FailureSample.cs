using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    public static async Task RunFailureSampleChecksAsync()
    {
        // Exercise both the RunReport and the real CLI adapter with a decision failure added after 240
        // executor failures. Its full reason must not depend on that appended example fitting in the sample.
        foreach (var throughCli in new[] { false, true })
        {
            var (fx, noticeId, token) = await TieAfterFirstSyncAsync();
            fx.Store.SavePair(fx.Pair with { Excluded = ["t.txt"] });
            fx.A.Seed("pending.txt", "pending content");
            var run = new SyncRun(fx.Store, new SampleFailureExecutor(), fx.Provider, Settings, TimeProvider.System);
            if (throughCli)
            {
                var runtime = new V2CliRuntime(null, fx.Store, run, new EndpointRootProbe(fx.Provider), "C:\\root");
                var result = await runtime.ResolveDecisionAsync(fx.Pair.PairId, "t.txt", "a", token,
                    _ => { }, CancellationToken.None);
                Require(!result.NoticeClosed && result.Detail == "decision could not be applied: the two sides still differ",
                    "CLI preserves the specific decision reason beyond the 200 failure examples");
            }
            else
            {
                var report = await run.ResolveDecisionAsync(fx.Pair.PairId, "t.txt", token, Side.A,
                    new RecordingProgress(), CancellationToken.None);
                Require(report.Failures.Count == 200 && report.Failed == 206 && report.Uncertain == 35 &&
                    report.FailureCount == 241 && report.Failures.All(result => result.ActionId != noticeId),
                    "report cap includes appended decision failures without truncating totals");
                Require(report.DecisionFailureReason == "decision could not be applied: the two sides still differ",
                    "decision detail is separate from the bounded example sample");
            }
        }
    }

    // Expanded executor children are not required to have an action in the supplied plan.
    private sealed class SampleFailureExecutor : IExecutor
    {
        public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            for (var i = 0; i < 240; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ActionResult("sample-child-" + i, i < 205 ? Outcome.Failed : Outcome.Uncertain,
                    null, null, null, "sample failure " + i);
            }
        }

        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            Task.FromResult<IReadOnlyList<ActionResult>>([]);
    }
}
