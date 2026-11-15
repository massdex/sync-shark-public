using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private static async Task SplitFailureCompositionAsync(string mode)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        fixture.Local.Seed(FolderEntry("Destination", row.Synced!.LocalId), null);
        fixture.Dropbox.Seed(FolderEntry(PathNames.Child(source.Path, "Child"), "child-id"), null);
        var root = new FolderMove(source.Id, source.Id, null, source.Path, "Destination", null,
            FolderMovePhase.Armed, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        var incoming = RemoteEntry("Failing", "failing-id");
        fixture.Dropbox.Seed(incoming, [1]);
        SeedRemote(fixture, incoming);
        var primary = new InvalidOperationException("synthetic lane failure");
        var secondary = new InvalidOperationException("synthetic walk failure");
        var callback = new InvalidOperationException("synthetic cancellation callback failure");
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var walkFaultObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false;
        CancellationTokenRegistration registration = default;
        var events = new System.Collections.Concurrent.ConcurrentQueue<RunEvent>();
        var remote = new ProxySide(fixture.Dropbox)
        {
            SubmitMove = (_, _, _) => Task.FromResult<FolderSubmitResult>(new FolderSubmitResult.Accepted("move-job")),
            ReadMove = async (_, _, _, ct) =>
            {
                // The walk's TaskSet cancels its token only after recording an actual member fault.
                registration = ct.Register(() =>
                {
                    if (mode == "callback") throw callback;
                    walkFaultObserved.TrySetResult();
                });
                polling.TrySetResult();
                try
                {
                    await laneStarted.Task.WaitAsync(ct);
                    if (mode == "secondary") throw secondary;
                    await release.Task.WaitAsync(ct);
                    throw new InvalidOperationException("the pending poll was not cancelled");
                }
                finally { exited = true; }
            }
        };
        fixture.LocalView = new ProxySide(fixture.Local)
        {
            BeforeStage = async (path, ct) =>
            {
                if (path != incoming.Path) return;
                await polling.Task.WaitAsync(ct);
                laneStarted.TrySetResult();
                if (mode == "secondary") await walkFaultObserved.Task.WaitAsync(ct);
                throw primary;
            }
        };
        var run = LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
            configure: live => live with
            {
                Dropbox = IdentitySide.Truthful(remote, fixture.Dropbox), Events = events.Enqueue
            }).SyncWorkAsync(guard.Token);
        Exception? caught = null;
        var endedAtReturn = false;
        try { await run.WaitAsync(guard.Token); endedAtReturn = exited; }
        catch (Exception error) { caught = error; endedAtReturn = exited; }
        finally
        {
            release.TrySetResult();
            try { await run; } catch { }
            registration.Dispose();
        }
        Check.Require(ReferenceEquals(caught, primary) && endedAtReturn,
            "a lane's primary failure survives the split walk join, which completes before return");
        Check.Require(fixture.Inner.FolderMoves(PairId, source.Id).Single() is { JobId: "move-job", Launch.Count: 1 },
            "the failed opportunity preserves accepted split job custody");
        var failures = events.Where(value => value.Kind == RunEventKind.Failed).Select(value => value.Error).ToArray();
        if (mode == "callback")
            Check.Require(failures.Length == 1 && failures[0] is AggregateException aggregate &&
                aggregate.InnerExceptions.Count == 1 && ReferenceEquals(aggregate.InnerExceptions[0], callback),
                "a throwing cancellation callback is reported once as its aggregate without skipping the walk join");
        else if (mode == "secondary")
            Check.Require(failures.Length == 1 && ReferenceEquals(failures[0], secondary),
                "an already-faulted walk is reported once without replacing the lane's primary failure");
        else Check.Require(failures.Length == 0, "expected split-walk cancellation is not a secondary failure event");
    }
}
