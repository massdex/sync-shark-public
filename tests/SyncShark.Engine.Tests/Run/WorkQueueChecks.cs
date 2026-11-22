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
        var applied = new List<string>();
        var arrival = first with {Kind=ChangeKind.Renamed,OldPath="a",Path="b"};
        var prerequisite = first with {Path="b/child"};
        var departure = first with {Kind=ChangeKind.Renamed,OldPath="b",Path="c"};
        await queue.AdmitAsync(pair,new([arrival,prerequisite,departure],"moved",null),(change,_) =>
        { lock(applied) applied.Add(change.Path!); return Task.CompletedTask; },null,default);
        Check.Require(applied.SequenceEqual(new[]{"b/child","c","b"}),"Arrival ran before the departure or its earlier prerequisite");

        var retained = first with {Path="retained"};
        var boundary = false;
        var blocked = queue.AdmitAsync(pair,new([retained],"pending",null),(_,_)=>throw new WorkQueue.Unsettled(),
            null,(_,_)=>{boundary=true;return Task.CompletedTask;},null,default);
        Check.Require(!boundary && !blocked.IsCompleted,"Unreproducible failure advanced its cursor");
        queue.Held(new("retained","retained")).Single().ConsumeHeld!();
        await blocked;
        Check.Require(boundary,"Exact retry settlement did not release the cursor");
        var hop = (Local: new JournalPosition(2,10),Dropbox: "hop");
        Check.Require(!queue.CoversHop(hop),"An empty queue was treated as physical-hop coverage");
        await queue.AdmitAsync(pair,new([],null,hop.Local),(_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,default);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = queue.AdmitAsync(pair,new([first],hop.Dropbox,null),(_,_)=>release.Task,null,default);
        Check.Require(!queue.CoversHop(hop),"An unread or unsettled feed range was covered");
        release.SetResult(); await feed;
        Check.Require(queue.CoversHop(hop) && !queue.CoversHop((new(3,10),hop.Dropbox)) &&
            !queue.CoversHop((hop.Local,"other")),"A stale journal or different opaque cursor was treated as covered");
        var regressions=new List<string>();
        if(queue.CoversHop((new(2,11),hop.Dropbox))) regressions.Add("covered a higher unobserved USN");
        await queue.AdmitAsync(pair,new ChangePage([],null,hop.Local){ContinuityLost=true},(_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,default);
        await queue.AdmitAsync(pair,new([],null,new(2,11)),(_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,default);
        if(queue.CoversHop(hop)) regressions.Add("same-journal gap revived stale hop coverage");
        using var cancel=new CancellationTokenSource();
        var retried=0; var rename=first with {Kind=ChangeKind.Renamed,OldPath="old",Path="new",Observed=null};
        var outstanding=queue.AdmitAsync(pair,new([rename],"rename",null),(change,_)=>{
            Check.Require(change==rename,"Retry replaced held rename with a synthetic change");
            if(++retried==1) throw new WorkQueue.Unsettled(); return Task.CompletedTask;
        },null,cancel.Token);
        var follow=queue.AdmitAsync(pair,new([rename with {Kind=ChangeKind.Changed,OldPath=null}],"follow",null),(_,_)=>Task.CompletedTask,null,cancel.Token);
        try { await Task.WhenAll(outstanding,follow).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch(TimeoutException) {regressions.Add("later input never retried the held rename");}
        finally {cancel.Cancel(); try {await Task.WhenAll(outstanding,follow);} catch(OperationCanceledException) {} }
        Check.Require(regressions.Count==0,string.Join("; ",regressions));
    }
}
