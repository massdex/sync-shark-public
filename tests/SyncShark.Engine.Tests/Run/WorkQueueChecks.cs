using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Held feed evidence belongs to the removal until its exact subjects settle.</summary>
public static class WorkQueueChecks
{
    public static async Task RunAsync()
    {
        var queue = new WorkQueue();
        var pair = ItemChecks.Pair();
        const string child = "remote/item";
        var version = ItemChecks.File(Side.Dropbox, child, 1, "child");
        var first = new Change(Side.Dropbox, ChangeKind.Changed, child, null, version.Id, version, false, false);
        var second = first with { Observed = ItemChecks.File(Side.Dropbox, child, 2, version.Id) };
        var deleted = first with { Kind = ChangeKind.Deleted, Id = null, Observed = null };
        var parent = new Change(Side.Dropbox, ChangeKind.Deleted, "remote", null, null, null, false, false);
        var footprint = new Scope("local", "remote");
        Task? pending = null;
        var independent = 0;
        try
        {
            await queue.AdmitAsync(pair, new([parent], "before", null), (_, ct) =>
            {
                pending = queue.AdmitAsync(pair, new([first, second, deleted], "after", null), (_, _) =>
                {
                    Interlocked.Increment(ref independent);
                    return Task.CompletedTask;
                }, null, ct);
                var reports = queue.Held(footprint).Where(x => x.Path == child).ToArray();
                Check.Require(reports.Length == 3 && reports[0].Observed == first.Observed &&
                    reports[1].Observed == second.Observed && reports[2].Kind == ChangeKind.Deleted,
                    "Removal did not receive both revisions followed by the path-only tombstone");
                reports[0].ConsumeHeld!();
                var retry = queue.Held(footprint).Where(x => x.Path == child).ToArray();
                Check.Require(retry.Length == 2 && retry[0].Observed == second.Observed, "Retry lost the unconsumed revision");
                foreach (var change in retry) change.ConsumeHeld!();
                return Task.CompletedTask;
            }, null, default);
        }
        catch (NotImplementedException error)
        {
            throw new InvalidDataException("Removal cannot read the ordered held versions", error);
        }
        Check.Require(pending != null, "Removal did not start");
        await pending!;
        Check.Require(independent == 0 && !queue.Held(footprint).Any(), "Consumed children executed independently");
    }
}
