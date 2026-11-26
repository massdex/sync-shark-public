using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Held feed evidence belongs to the removal until its exact subjects settle.</summary>
public static class WorkQueueChecks
{
    public static async Task RepairHeldRowEndsAsync()
    {
        using var store=new InMemoryStateStore();
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local,async proof=>(await store.ArmedAsync(pair.PairId,default))?.Stage==proof);
        var cloud=new FakeSide(Side.Dropbox){ClockOffset=(TimeSpan.Zero,TimeSpan.Zero)};
        await store.SavePairAsync(pair,default);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        const string path="formerly-deleted"; var entry=ItemChecks.File(Side.Local,path,1,"pc");
        pc.Seed(entry,[1]);
        cloud.DeletedHistory[path]=new DeletionEvidence.DeletedFile(path,"cloud","previous","r1",entry.Modified.AddDays(1),null,entry.ContentHash,null);
        using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,end.Token).WaitAsync(end.Token); }
        catch(OperationCanceledException) when(end.IsCancellationRequested) { throw new InvalidDataException("Repair did not finish while a row still failed"); }
        Check.Require(pc.Items["formerly-deleted"].Bytes.SequenceEqual(new byte[]{1}) && pc.History.Count==0,"Repair recycled an unproved version");
        Check.Require(store.OpenNotices.Count(n=>n.Kind==NoticeKind.Failure && PathNames.Same(n.Path,path))==1,"The failing row did not keep exactly one notice");
        Check.Require(!await queue.RetryHeldAsync(null,default),"Repair kept its failed row in the queue");
        int runs=0;
        var page=new Change(Side.Local,ChangeKind.Changed,"fresh",null,"fresh-id",null,false,false);
        await queue.AdmitAsync(pair,new([page],null,new(1,1)),(_,_)=>{Interlocked.Increment(ref runs);return Task.CompletedTask;},(_,_)=>Task.CompletedTask,end.Token).WaitAsync(end.Token);
        Check.Require(runs==1,"A queued page did not run exactly once after Repair finished");
    }

    // A held AutoSync input owns its path: Repair must finish behind it, sync an unrelated sibling, sync its own difference at the end turn and not consume the held input.
    public static async Task RepairBehindHeldPageAsync()
    {
        using var store=new InMemoryStateStore();
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var cloud=new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair,default);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        pc.Seed(ItemChecks.File(Side.Local,"a",1,"pc-a"),[1]);
        pc.Seed(ItemChecks.File(Side.Local,"b",1,"b"),[1]);
        var held=ItemChecks.File(Side.Local,"a",1,"pc-a");
        await queue.AdmitAsync(pair,new Row(held.Path,held,null,null,[],null,null!),[],_=>throw new WorkQueue.Unsettled(),default);
        using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,end.Token).WaitAsync(end.Token); }
        catch(OperationCanceledException) when(end.IsCancellationRequested) { throw new InvalidDataException("Repair joined held work at its path"); }
        Check.Require(cloud.Items.TryGetValue("b",out var b) && b.Bytes.SequenceEqual(new byte[]{1}),"Repair did not sync the healthy sibling");
        Check.Require(cloud.Items.ContainsKey("a") && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure && PathNames.Same(n.Path,"a")),"The declined row did not sync at the end of Repair and close its notice");
        Check.Require(await queue.RetryHeldAsync(null,default),"Repair consumed or dropped the other owner's held input");
    }

    // X is still running when Repair starts waiting behind the unsent rename x->y; X then parks. Repair must recompute and decline, not wait forever.
    public static async Task RepairLateParkedPrerequisiteAsync()
    {
        using var store=new InMemoryStateStore();
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var cloud=new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair,default);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); bool released=false;
        var held=ItemChecks.File(Side.Dropbox,"x",1,"x");
        var active=queue.AdmitAsync(pair,new Row(held.Path,null,held,null,[],null,null!),[],async _=>{await gate.Task;if(!released) throw new WorkQueue.Unsettled();},default);
        int moved=0;
        var rename=new Change(Side.Dropbox,ChangeKind.Renamed,"y","x","x",null,false,false);
        var page=queue.AdmitAsync(pair,new([rename],null,new(1,1)),(_,_)=>{Interlocked.Increment(ref moved);return Task.CompletedTask;},(_,_)=>Task.CompletedTask,default);
        pc.Seed(ItemChecks.File(Side.Local,"y",1,"y"),[1]);
        // z sorts after y, so its upload stages only after Repair is already waiting at y; that is when X parks.
        pc.Seed(ItemChecks.File(Side.Local,"z",1,"z"),[1]);
        cloud.AfterStage=()=>gate.TrySetResult();
        using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,end.Token).WaitAsync(end.Token); }
        catch(OperationCanceledException) when(end.IsCancellationRequested) { throw new InvalidDataException("Repair kept waiting after its prerequisite parked"); }
        Check.Require(gate.Task.IsCompleted && cloud.Items.ContainsKey("z"),"The late park was not armed by the later row");
        Check.Require(moved==0 && cloud.Items.ContainsKey("y") && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure && PathNames.Same(n.Path,"y")),"The declined row ran under parked work or did not sync at the end of Repair and close its notice");
        released=true; await queue.RetryHeldAsync(null,end.Token);
        await Task.WhenAll(active,page).WaitAsync(end.Token);
        Check.Require(moved==1,"The renamed page did not run once after its prerequisite settled");
    }

    // A healthy Repair row at y matches an unsent rename x->y but not the held change at x: it declines rather than wait through the rename, then syncs at the end turn and its notice closes.
    public static async Task TransitiveRepairBehindHeldMoveAsync()
    {
        using var store=new InMemoryStateStore();
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var cloud=new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair,default);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        var held=ItemChecks.File(Side.Dropbox,"x",1,"x"); bool released=false;
        await queue.AdmitAsync(pair,new Row(held.Path,null,held,null,[],null,null!),[],_=>released?Task.CompletedTask:throw new WorkQueue.Unsettled(),default);
        int moved=0;
        var rename=new Change(Side.Dropbox,ChangeKind.Renamed,"y","x","x",null,false,false);
        var page=queue.AdmitAsync(pair,new([rename],null,new(1,1)),(_,_)=>{Interlocked.Increment(ref moved);return Task.CompletedTask;},(_,_)=>Task.CompletedTask,default);
        pc.Seed(ItemChecks.File(Side.Local,"y",1,"y"),[1]);
        using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,end.Token).WaitAsync(end.Token); }
        catch(OperationCanceledException) when(end.IsCancellationRequested) { throw new InvalidDataException("Repair joined an unsent rename blocked by held work"); }
        Check.Require(moved==0,"The blocked rename ran under held work");
        Check.Require(cloud.Items.ContainsKey("y") && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure && PathNames.Same(n.Path,"y")),"The row declined behind held work did not sync at the end of Repair and close its notice");
        released=true; await queue.RetryHeldAsync(null,end.Token);
        await page.WaitAsync(end.Token);
        Check.Require(moved==1,"The renamed page did not run once after its prerequisite settled");
    }

    // A row declined behind held work whose item already has an open Failure from an earlier run still gets its end turn under that notice, which then closes.
    public static Task CoveredDeclineEndsAsync()=>CoveredDecline(false);
    // An open unknown-move notice on the row's Dropbox item names work the row's end turn does not re-check, so it stays open and the move stays owed.
    public static Task CoveredMoveStaysOwedAsync()=>CoveredDecline(true);
    private static async Task CoveredDecline(bool move)
    {
        using var store=new InMemoryStateStore();
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var cloud=new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair,default);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        var held=ItemChecks.File(Side.Dropbox,"x",1,"x"); bool released=false;
        await queue.AdmitAsync(pair,new Row(held.Path,null,held,null,[],null,null!),[],_=>released?Task.CompletedTask:throw new WorkQueue.Unsettled(),default);
        var rename=new Change(Side.Dropbox,ChangeKind.Renamed,"y","x","x",null,false,false);
        var page=queue.AdmitAsync(pair,new([rename],null,new(1,1)),(_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,default);
        var file=ItemChecks.File(Side.Local,"y",1,"y"); pc.Seed(file,[1]);
        var remote=ItemChecks.File(Side.Dropbox,"y",1,"dy"); if(move) cloud.Seed(remote,[1]);
        var covering=move ? new Notice(0,NoticeKind.Failure,TextKey.FailDropboxOutcomeUnknown,"y",new([new(Side.Dropbox,"y",remote.Id)],Side.Dropbox){Move=new(remote.Id,"y","target",null,false)},DateTimeOffset.UnixEpoch,false)
            : new Notice(0,NoticeKind.Failure,TextKey.WaitItemRetry,"y",new([new(Side.Local,"y",file.Id)],Side.Local),DateTimeOffset.UnixEpoch,false);
        await store.SaveNoticeAsync(pair.PairId,covering,default);
        using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,end.Token).WaitAsync(end.Token);
        if(move) Check.Require(store.OpenNotices.Any(n=>n.Subject.Move?.Destination=="target"),"A covered decline closed an unresolved move it never re-checked");
        else Check.Require(cloud.Items.ContainsKey("y") && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure && PathNames.Same(n.Path,"y")),"A declined row covered by an earlier notice got no end turn, or its notice stayed open");
        released=true; await queue.RetryHeldAsync(null,end.Token);
        await page.WaitAsync(end.Token);
    }

    public static async Task HeldRowStillOrdersAsync()
    {
        using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var queue=new WorkQueue(); var pair=ItemChecks.Pair();
        var entry=ItemChecks.File(Side.Local,"held",1,"pc"); bool ready=false; int calls=0;
        await queue.AdmitAsync(pair,new Row(entry.Path,entry,null,null,[],null,null!),[],
            _=>ready ? Task.CompletedTask : throw new WorkQueue.Unsettled(),end.Token);
        var change=new Change(Side.Local,ChangeKind.Changed,entry.Path,null,entry.Id,entry,false,false);
        var page=queue.AdmitAsync(pair,new([change],null,new(1,1)),(_,_)=>{Interlocked.Increment(ref calls);return Task.CompletedTask;},(_,_)=>Task.CompletedTask,end.Token);
        Check.Require(calls==0 && !page.IsCompleted,"An overlapping page ran before the retained row settled");
        ready=true; Check.Require(await queue.RetryHeldAsync(null,end.Token),"Retry did not find the retained row");
        await page.WaitAsync(end.Token);
        Check.Require(calls==1,"The overlapping page did not run exactly once after the retained row settled");
    }

    public static async Task RunAsync()
    {
        JournalPosition? clamped=new(2,9);
        var queue = new WorkQueue(_=>Task.FromResult<JournalPosition?>(clamped));
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
                var decoy=first with {Side=Side.Local,Path=child};
                var unrelated=queue.AdmitAsync(pair,new([decoy],null,new(3,4)),(_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,ct);
                var reports = queue.Held(footprint).Where(x => x.Path == child).ToArray();
                Check.Require(reports.All(x=>x.Side==Side.Dropbox),"Borrowed a PC report under only the Dropbox endpoint");
                pending=Task.WhenAll(pending,unrelated);
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
        foreach(bool reverse in new[]{false,true})
        {
            int marked=0,released=0;
            Change Report(Change change)=>change with {MarkHeld=()=>{
                marked++;
                return ()=>{Check.Require(marked==2,"Released a predecessor before marking its successor");released++;};
            }};
            var older=Report(first);var newer=Report(second);
            WorkQueue.ConsumeHeld(reverse ? [newer,older] : [older,newer]);
            Check.Require(released==2,"Did not release every acknowledged input");
        }
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
        var footprintHeld=new Scope("retained","retained");
        var releaseFailed=queue.Held(footprintHeld).Single().MarkHeld!();
        Check.Require(releaseFailed!=null && !queue.Held(footprintHeld).Any() && !blocked.IsCompleted && !boundary,
            "Marking a failed input released its cursor or left it eligible for retry");
        releaseFailed!();
        await blocked;
        Check.Require(boundary,"Exact retry settlement did not release the cursor");
        var hop = (Local: new JournalPosition(2,10),Dropbox: "hop");
        Check.Require(!queue.CoversHop(hop),"An empty queue was treated as physical-hop coverage");
        await queue.AdmitAsync(pair,new([],null,hop.Local),(_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,default);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = queue.AdmitAsync(pair,new([first],hop.Dropbox,null),(_,_)=>release.Task,null,default);
        Check.Require(!queue.CoversHop(hop),"An unread or unsettled feed range was covered");
        release.SetResult(); await feed;
        Check.Require(!queue.CoversHop(hop),"The offered PC USN exceeded the journal owner's settled prefix");
        clamped=hop.Local;
        await queue.AdmitAsync(pair,new([],null,hop.Local),(_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,default);
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
        var scoped=new WorkQueue();var raw=first with {Side=Side.Local,Path=null,OldPath=null,Observed=null,HistoricalParentId="bound",HistoricalName="item",AffectedPath="affected"};
        Task? evidence=null; var recovered=false;
        await scoped.AdmitAsync(pair,new([parent with {Path="affected"}],"parent",null),(_,ct)=>{
            evidence=scoped.AdmitAsync(pair,new([raw],null,new(2,10)),(_,_)=>Task.CompletedTask,null,ct);
            var held=scoped.Held(new("affected","elsewhere")).SingleOrDefault();
            recovered=held?.HistoricalParentId=="bound" && held.HistoricalName=="item" && held.Path==null;
            held?.ConsumeHeld?.Invoke(); return Task.CompletedTask;
        },null,default); await evidence!;
        Check.Require(recovered,"Verified pathless journal evidence unavailable to the scoped removal");
        var exact=new WorkQueue(); var attempts=0; bool permitted=false;
        var pendingExact=exact.AdmitAsync(pair,new([first],"exact",null),(_,_)=>{attempts++;if(!permitted) throw new WorkQueue.Unsettled();return Task.CompletedTask;},null,default);
        var notice=new Notice(1,NoticeKind.Failure,TextKey.FailChanged,child,new([new(Side.Dropbox,child,first.Id!)],Side.Dropbox),DateTimeOffset.UnixEpoch,false);
        var before=attempts;var unrelatedNotice=notice with {Subject=new([new(Side.Local,child,first.Id!)],Side.Local)};
        Check.Require(!await exact.RetryHeldAsync(null,default,unrelatedNotice) && attempts==before,"CLI Retry ran a different reviewed subject");
        permitted=true; Check.Require(await exact.RetryHeldAsync(null,default,notice),"Reviewed retained subject never ran"); await pendingExact;
        var hot = new WorkQueue(); var hold=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hotPage=Enumerable.Repeat(rename,128).ToArray();
        var busy=hot.AdmitAsync(pair,new(hotPage,"busy",null),(_,_)=>hold.Task,null,default);
        Check.Require(!busy.IsCompleted,"Blocked rename backlog completed before its prerequisite");
        hold.SetResult(); await busy;
        var early=new WorkQueue(); using var finish=new CancellationTokenSource();
        var running=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int tries=0;
        var earlier=early.AdmitAsync(pair,new([rename],"first",null),async (_,ct)=>{if(++tries==1) await running.Task.WaitAsync(ct); if(tries<3) throw new WorkQueue.Unsettled();},null,finish.Token);
        var later=early.AdmitAsync(pair,new([rename with {Kind=ChangeKind.Changed,OldPath=null}],"later",null),(_,_)=>Task.CompletedTask,null,finish.Token);
        running.SetResult();
        try {await Task.WhenAll(earlier,later).WaitAsync(TimeSpan.FromSeconds(2));} catch(TimeoutException){regressions.Add("An already-admitted overlapping input did not wake the retained operation");}
        finally {finish.Cancel();try{await Task.WhenAll(earlier,later);}catch(OperationCanceledException){}}
        var chain=new WorkQueue();var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busyPath=chain.AdmitAsync(pair,new([rename with {Kind=ChangeKind.Changed,Path="a",OldPath=null}],"start",null),(_,_)=>gate.Task,null,default);
        var moves=Enumerable.Repeat(rename with {Path="b",OldPath="a"},32).Concat(Enumerable.Repeat(rename with {Path="c",OldPath="b"},32)).ToArray();
        var leg=chain.AdmitAsync(pair,new(moves,"finish",null),(_,_)=>Task.CompletedTask,null,default);
        Check.Require(!leg.IsCompleted,"Matching departures bypassed the held origin");
        gate.SetResult();await Task.WhenAll(busyPath,leg).WaitAsync(TimeSpan.FromSeconds(5));Check.Require(regressions.Count==0,string.Join("; ",regressions));
        var during=new WorkQueue();using var end=new CancellationTokenSource();
        var inside=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var again=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var count=0;var third=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var nextAttempt=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldDuring=during.AdmitAsync(pair,new([rename],"held",null),async (_,ct)=>{
            int call=Interlocked.Increment(ref count);
            if(call==2) {inside.TrySetResult();await again.Task.WaitAsync(ct);}
            if(call==3) {third.SetResult();await nextAttempt.Task.WaitAsync(ct);}
            if(call<4) throw new WorkQueue.Unsettled();
        },null,end.Token);
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var news=rename with {Kind=ChangeKind.Changed,OldPath=null};
        var one=during.AdmitAsync(pair,new([news],"one",null),(_,_)=>Task.CompletedTask,null,end.Token);again.SetResult();
        await third.Task.WaitAsync(TimeSpan.FromSeconds(5));var two=during.AdmitAsync(pair,new([news],"two",null),(_,_)=>Task.CompletedTask,null,end.Token);nextAttempt.SetResult();
        try {await Task.WhenAll(heldDuring,one,two).WaitAsync(TimeSpan.FromSeconds(2));}
        catch(TimeoutException){regressions.Add("Overlap received during second retry did not wake it again");}
        finally {end.Cancel();try{await Task.WhenAll(heldDuring,one,two);}catch(OperationCanceledException){}}
        var varied=new WorkQueue();var stopVaried=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingB=varied.AdmitAsync(pair,new([news with {Path="b"}],"gate",null),(_,_)=>stopVaried.Task,null,default);
        var legs=Enumerable.Range(0,32).Select(i=>rename with {OldPath=$"a{i}",Path="b"}).Concat(Enumerable.Range(0,32).Select(i=>rename with {OldPath="b",Path=$"c{i}"})).ToArray();
        var settled=varied.AdmitAsync(pair,new(legs,"done",null),(_,_)=>Task.CompletedTask,null,default);
        Check.Require(!settled.IsCompleted,"Varied departures bypassed their prerequisite");
        stopVaried.SetResult(); await Task.WhenAll(waitingB,settled).WaitAsync(TimeSpan.FromSeconds(5));
        foreach(var interleaved in new[]{false,true}) foreach(var splitDepartures in new[]{false,true}) foreach(var size in new[]{24,48,96})
        {
            var multi=new WorkQueue();var atB=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var origins=Enumerable.Range(0,size).Select(i=>news with {Path=$"a{i}"}).ToArray();
            var gates=origins.ToDictionary(x=>x.Path!,_=>new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            var prereqs=multi.AdmitAsync(pair,new(origins.Append(news with {Path="b"}).ToArray(),"prereqs",null),(x,_)=>x.Path=="b"?atB.Task:gates[x.Path!].Task,null,default);
            var arrivals=origins.Select((_,i)=>rename with {OldPath=$"a{i}",Path="b"}).ToArray();
            var updates=origins.Select((_,i)=>news with {Path="b",Id=$"update{i}"}).ToArray();
            var departures=origins.Select((_,i)=>rename with {OldPath="b",Path=$"c{i}"}).ToArray();
            var following=splitDepartures ? departures.Zip(updates).SelectMany(x=>new[]{x.First,x.Second with {Id="after-"+x.Second.Id}}).ToArray() : departures;
            var transfers=(interleaved?arrivals.Zip(updates).SelectMany(x=>new[]{x.First,x.Second}):arrivals.AsEnumerable()).Concat(following).ToArray();
            var order=new List<Change>();long allocation=GC.GetAllocatedBytesForCurrentThread();var start=System.Diagnostics.Stopwatch.GetTimestamp();
            var finishMoves=multi.AdmitAsync(pair,new(transfers,"done",null),(x,_)=>{lock(order)order.Add(x);return Task.CompletedTask;},null,default);
            var elapsed=System.Diagnostics.Stopwatch.GetElapsedTime(start);allocation=GC.GetAllocatedBytesForCurrentThread()-allocation;
            Console.WriteLine($"Move admission: interleaved={interleaved} splitDepartures={splitDepartures} size={size} bytes={allocation} elapsed={elapsed}");
            foreach(var origin in origins.Reverse()) gates[origin.Path!].SetResult();atB.SetResult();
            await Task.WhenAll(prereqs,finishMoves).WaitAsync(TimeSpan.FromSeconds(5));
            var expected=(interleaved?updates:Array.Empty<Change>()).Concat(splitDepartures?following.SkipLast(1):following).Concat(arrivals);
            if(splitDepartures) expected=expected.Append(following[^1]);
            Check.Require(order.SequenceEqual(expected),"Departures and their earlier prerequisites must precede postponed arrivals in receipt order");
        }
        var repairQueue=new WorkQueue();bool acquired=false,appliedHeld=false;
        var retainedPage=repairQueue.AdmitAsync(pair,new([first],"held",null),(_,_)=>{if(!acquired)throw new WorkQueue.Unsettled();appliedHeld=true;return Task.CompletedTask;},null,default);
        acquired=true;var prepared=new Row(child,null,version,null,[],null,null!);
        var rowWork=repairQueue.AdmitAsync(pair,prepared,[],_=>Task.CompletedTask,default);
        if(!appliedHeld){regressions.Add("Prepared Repair row did not wake the retained feed input");await repairQueue.RetryHeldAsync(null,default);}
        await Task.WhenAll(rowWork,retainedPage);
        Check.Require(regressions.Count==0,string.Join("; ",regressions));

    }
}
