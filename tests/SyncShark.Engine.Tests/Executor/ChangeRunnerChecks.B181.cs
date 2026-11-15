using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void RunRefusedNewFolderRetry() => RefusedNewFolderRetryAsync().GetAwaiter().GetResult();

    // B-181: a new Local folder whose Dropbox copy was created and whose batch was then refused as a pending ancestor
    // (its own creation echo) leaves this durable state: the folder bound and empty on Dropbox, its Local selection
    // pending with discovery required, the echo pending after it. On a top-of-Dropbox ("/") pair the retry must
    // upload every old, unchanged child; 0.3.81 failed each one ChangedUnderneath before any provider call (B-105).
    private static async Task RefusedNewFolderRetryAsync()
    {
        string[] files = [.. Enumerable.Range(0, 13).Select(index => "T/f" + index), "T/sub/g0"];
        using var fixture = new Fixture(realStore: true);
        var pair = fixture.Pair with { DropboxRoot = "/", Roots = fixture.Pair.Roots! with { DropboxRoot = "/" } };
        // Storage keys footprints by its own saved definition, so it holds the same root as the run.
        fixture.Inner.SavePair(pair);
        var local = FolderEntry("T", "local-T");
        var remote = FolderEntry("T", "dropbox-T");
        fixture.Local.Seed(local, null);
        fixture.Local.Seed(FolderEntry("T/sub", "local-sub"), null);
        foreach (var file in files) fixture.Local.Seed(LocalEntry(file, "l-" + file), [1]);
        fixture.Dropbox.Seed(remote, null);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        var refused = Seed(fixture, EchoNotice(Side.Local, local));
        fixture.Inner.RequireDiscovery(refused, refused.AnchorWorkId);
        SeedRemote(fixture, remote);
        RepairDone(fixture);
        var events = new List<RunEvent>();
        var context = fixture.Context(refused) with
        {
            Pair = pair, Scope = new PathScope(pair, [pair]), Events = item => { lock (events) events.Add(item); }
        };
        var boundary = new Boundary(_ => new Lease());
        boundary.Capture = (chosen, _, _) => Task.FromResult(Coverage(chosen));
        var ports = new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 0);
        var syncer = new Syncer(context, ports with
        {
            RunBatchAsync = (work, side, scope, retries, settles, ct) =>
                SyncJobs.RunBatchAsync(context, ports, work, scope, ct, side, retries, settles)
        });
        // Each ordinary AutoSync call is one retry opportunity; the live folder never converged in any number.
        for (var pass = 0; pass < 3 && PendingAt(fixture, Side.Local, "T").Count +
            PendingAt(fixture, Side.Dropbox, "T").Count != 0; pass++)
            await syncer.SyncWorkAsync(default);
        var missing = new List<string>();
        foreach (var file in files)
            if (await fixture.Dropbox.ReadOneAsync(file, default) is null) missing.Add(file);
        string changed;
        lock (events)
            changed = string.Join(" | ", events.Where(item => item.Error is ChangedUnderneathException)
                .Select(item => item.Path));
        var issues = fixture.Inner.Issues(PairId);
        Check.Require(missing.Count == 0 && changed.Length == 0 && issues.Count == 0,
            "B-181: the retry under a '/' Dropbox root uploads every unchanged child with no ChangedUnderneath; missing=" +
            string.Join(",", missing) + " changed=" + changed + " issues=" +
            string.Join(" | ", issues.Select(issue => issue.Path + ":" + issue.Cause)));
        Console.WriteLine("RefusedNewFolderRetryAsync: " + files.Length + " children uploaded on the retry");
    }
}
