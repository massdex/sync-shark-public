using System.Collections;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

/// <summary>Fast observation lifetime regressions; no filesystem, database or provider work.</summary>
internal static class ObserverMemoryChecks
{
    public static async Task RunAsync()
    {
        await BackgroundCallsDoNotIndexPlans();
        await ManualRowsAreCompactAndDrained();
        await CaseCollisionKeepsBothDisplayRows();
        await IndependentFlowsDoNotShareSessions();
        await FailureAndCancellationDisposeSessions();
        await ClosedSessionRejectsLateResults();
        Console.WriteLine("v2 observer memory checks passed.");
    }

    private static async Task BackgroundCallsDoNotIndexPlans()
    {
        var result = Result("a", Outcome.Done);
        var inner = new ProbeExecutor { Results = [result] };
        var observed = new ObservedExecutor(inner);
        // The CLI never opens a session. The decorator must not even enumerate/index its plan.
        var plan = new Plan("cli", 1, new UnreadableActions(), []);
        await Consume(observed, plan, result);
        var reconciled = await observed.ReconcileAsync(plan.Actions, 1, null!, null!, default);
        Require(ReferenceEquals(reconciled, inner.Results), "reconcile forwards the original result collection");
        // AutoSync repeatedly uses this same decorator, without Begin/Take calls between cycles.
        for (var cycle = 0; cycle < 24; cycle++) await Consume(observed, plan, result);
        using var manual = observed.Begin();
        Require(manual.Take().Count == 0, "background cycles leave no manual rows");
    }

    private static async Task ManualRowsAreCompactAndDrained()
    {
        var inner = new ProbeExecutor();
        var observed = new ObservedExecutor(inner);
        using var session = observed.Begin();
        var actions = new[]
        {
            Action("held", ActionKind.Hold), Action("done", ActionKind.CopyNew),
            Action("skip", ActionKind.Move), Action("fail", ActionKind.Delete),
            Action("unknown", ActionKind.Update), Action("bookkeeping", ActionKind.Adopt)
        };
        inner.Results = [Result("done", Outcome.Done), Result("skip", Outcome.Skipped, "changed"),
            Result("fail", Outcome.Failed, "denied"), Result("unknown", Outcome.Uncertain, new string('x', 4096)),
            Result("bookkeeping", Outcome.Done)];
        await Consume(observed, new Plan("manual", 1, actions, []));
        // A later reconcile for the same path replaces, rather than appends to, its display outcome.
        inner.Results = [Result("retry", Outcome.Done)];
        await observed.ReconcileAsync([Action("retry", ActionKind.CopyNew) with { Path = "fail" }],
            2, null!, null!, default);
        var rows = session.Take();
        Require(rows.Count == 5, "one row per exact path and no successful bookkeeping row");
        Require(rows.Single(row => row.Path == "held").Outcome is null, "held action remains visible");
        Require(rows.Single(row => row.Path == "done").Outcome == Outcome.Done, "successful action recorded");
        Require(rows.Single(row => row.Path == "skip").Reason == "changed", "short failure detail unchanged");
        Require(rows.Single(row => row.Path == "fail").Outcome == Outcome.Done, "retry replaces failed path");
        var unknown = rows.Single(row => row.Path == "unknown");
        Require(unknown.Outcome == Outcome.Uncertain && unknown.Reason!.Length < 1024,
            "uncertain outcome kept but large provider error is not retained");
        Require(session.Take().Count == 0, "Take drains instead of copying retained history");
        // A successful baseline-only action cannot erase an already displayed transfer at the same path.
        using var next = observed.Begin();
        inner.Results = [Result("copy", Outcome.Done), Result("adopt", Outcome.Done)];
        await Consume(observed, new Plan("manual", 2,
            [Action("copy", ActionKind.CopyNew), Action("adopt", ActionKind.Adopt) with { Path = "copy" }], []));
        Require(next.Take() is [{ Kind: ActionKind.CopyNew }], "bookkeeping does not hide a transfer");
    }

    private static async Task CaseCollisionKeepsBothDisplayRows()
    {
        var inner = new ProbeExecutor { Results = [Result("copy", Outcome.Done)] };
        var observed = new ObservedExecutor(inner);
        using var session = observed.Begin();
        await Consume(observed, new Plan("manual", 1,
            [Action("held", ActionKind.Hold) with { Path = "Report.txt" },
             Action("copy", ActionKind.CopyNew) with { Path = "report.txt" }], []));
        var rows = session.Take();
        Require(rows.Count == 2, "case-collision hold and transfer remain separate display rows");
        Require(rows.Single(row => row.Path == "Report.txt") is { Kind: ActionKind.Hold, Outcome: null },
            "case-collision conflict remains visible");
        Require(rows.Single(row => row.Path == "report.txt") is { Kind: ActionKind.CopyNew, Outcome: Outcome.Done },
            "case-collision transfer remains visible");
    }

    private static async Task IndependentFlowsDoNotShareSessions()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ProbeExecutor { Results = [Result("same-id", Outcome.Done)], BeforeResults = release.Task };
        var observed = new ObservedExecutor(inner);
        // Start this before either manual context, just as the AutoSync pair loop is started independently.
        var background = Consume(observed, new Plan("auto", 1, new UnreadableActions(), []));
        var first = Manual("first");
        var second = Manual("second");
        release.SetResult();
        await background;
        var rows = await Task.WhenAll(first, second);
        Require(rows[0] is [{ Path: "first" }] && rows[1] is [{ Path: "second" }],
            "overlapping manual scopes and AutoSync have isolated recording ownership");

        async Task<IReadOnlyList<ObservedExecutor.Observation>> Manual(string path)
        {
            using var session = observed.Begin();
            await Consume(observed, new Plan(path, 1,
                [Action("same-id", ActionKind.CopyNew) with { Path = path }], []));
            return session.Take();
        }
    }

    private static async Task FailureAndCancellationDisposeSessions()
    {
        foreach (var failure in new Exception[] { new InvalidOperationException("failed"), new OperationCanceledException() })
        {
            var inner = new ProbeExecutor { Failure = failure };
            var observed = new ObservedExecutor(inner);
            ObservedExecutor.Session? ended = null;
            try
            {
                using var session = observed.Begin();
                ended = session;
                await Consume(observed, new Plan("manual", 1, [Action("held", ActionKind.Hold)], []));
                throw new InvalidDataException("the executor failure was lost");
            }
            catch (Exception error) when (ReferenceEquals(error, failure)) { }
            Require(ended!.Take().Count == 0, "exception disposal releases held rows");
            inner.Failure = null;
            // This would throw if a closed session were left installed in the caller's async context.
            await Consume(observed, new Plan("auto", 2, new UnreadableActions(), []));
        }
    }

    private static async Task ClosedSessionRejectsLateResults()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ProbeExecutor { Results = [Result("a", Outcome.Done)], BeforeResults = release.Task };
        var observed = new ObservedExecutor(inner);
        using var session = observed.Begin();
        var running = Consume(observed, new Plan("manual", 1, [Action("a", ActionKind.CopyNew)], []));
        session.Dispose();
        using var next = observed.Begin();
        release.SetResult();
        await running;
        Require(session.Take().Count == 0 && next.Take().Count == 0,
            "late result cannot refill a disposed session or contaminate its successor");
    }

    private static async Task Consume(ObservedExecutor observed, Plan plan, ActionResult? expected = null)
    {
        await foreach (var result in observed.ApplyAsync(plan, null!, null!, default))
            if (expected is not null) Require(ReferenceEquals(result, expected), "result forwarded unchanged");
    }

    private static SyncAction Action(string id, ActionKind kind) =>
        new(id, 1, kind, Side.B, id, null, null, null, false, null);

    private static ActionResult Result(string id, Outcome outcome, string? reason = null) =>
        new(id, outcome, null, null, null, reason);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Observer memory: " + message);
    }

    // Only the inner executor owns plan consumption on a non-recorded run. This catches the old unconditional
    // _planned population deterministically, without a large fixture, heap timing or private-field inspection.
    private sealed class UnreadableActions : IReadOnlyList<SyncAction>
    {
        public int Count => throw new InvalidOperationException("unobserved plan was indexed");
        public SyncAction this[int index] => throw new InvalidOperationException("unobserved plan was indexed");
        public IEnumerator<SyncAction> GetEnumerator() => throw new InvalidOperationException("unobserved plan was enumerated");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ProbeExecutor : IExecutor
    {
        internal IReadOnlyList<ActionResult> Results { get; set; } = [];
        internal Task BeforeResults { get; set; } = Task.CompletedTask;
        internal Exception? Failure { get; set; }

        public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan p, IEndpointWriter a, IEndpointWriter b,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await BeforeResults;
            if (Failure is not null) throw Failure;
            ct.ThrowIfCancellationRequested();
            foreach (var result in Results) yield return result;
        }

        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            Task.FromResult(Results);
    }
}
