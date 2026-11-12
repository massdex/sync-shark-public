using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void CompletionFailureContained() => CompletionFailureContainedAsync().GetAwaiter().GetResult();

    // B-204 (never stop on failure), on the real store: two changed Local files in one AutoSync pass; the first
    // selection completion throws a per-item failure. The pass and the run continue, the other file completes, the
    // failed item has exactly one Failure notice and its work stays pending, and a later run completes it.
    private static async Task CompletionFailureContainedAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var inject = true;
        TrustEchoSources(fixture);
        const string First = "Parent/b204-a.txt";
        const string Second = "Parent/b204-b.txt";
        fixture.Local.Seed(LocalEntry(First, "b204-a"), [1]);
        fixture.Local.Seed(LocalEntry(Second, "b204-b"), [2]);
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, First, null, "b204-a", null, false, false));
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Second, null, "b204-b", null, false, false), 2);
        // Every completion of the first file's selection fails as the live store did (SQLITE_CONSTRAINT); the other
        // selection completes normally.
        var injected = 0;
        var completions = 0;
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name != nameof(IStateStore.CompleteWork)) return;
            Interlocked.Increment(ref completions);
            if (args?[0] is WorkSelection { Identity: "b204-a" } && inject)
            {
                Interlocked.Increment(ref injected);
                throw new Microsoft.Data.Sqlite.SqliteException("B-204 injected constraint failure", SQLitePCL.raw.SQLITE_CONSTRAINT);
            }
        };
        var boundary = new Boundary(_ => new Lease()) { Capture = (work, _, _) => Task.FromResult(Coverage(work)) };
        var ports = new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 0);
        var events = new List<RunEvent>();
        var context = fixture.Context(selected) with { Events = value => { lock (events) events.Add(value); } };
        // A run repeats its pass after another selection retires, so the failing item is tried once per pass: a few
        // times per run, never a hot loop.
        for (var run = 0; run < 2; run++)
        {
            Exception? escaped = null;
            try { await new Syncer(context, ports).SyncWorkAsync(default); }
            catch (Exception error) { escaped = error; }
            var (pendingLocal, _) = fixture.Inner.CountWork(PairId, Generation, long.MaxValue);
            var failures = fixture.Inner.Issues(PairId).Where(issue => issue.Kind == IssueKind.Failure).ToArray();
            Check.Require(escaped is null && injected > run && injected <= 3 * (run + 1) &&
                    await fixture.Dropbox.ReadOneAsync(Second, default) is not null &&
                    pendingLocal == 1 && failures.Length == 1 &&
                    events.Count(value => value.Kind == RunEventKind.Failed) >= run + 1,
                $"B-204 (run {run}): a selection whose completion fails every time is contained: " +
                $"escaped={escaped?.GetType().Name}:{escaped?.Message} injected={injected} completions={completions} " +
                $"pending={pendingLocal} failures={failures.Length}");
        }
        inject = false;
        await new Syncer(context, ports).SyncWorkAsync(default);
        var (retried, _) = fixture.Inner.CountWork(PairId, Generation, long.MaxValue);
        Check.Require(retried == 0,
            $"B-204: the contained item retries on a later opportunity and completes: pending={retried} " +
            $"notices={fixture.Inner.Issues(PairId).Count}");
    }
}
