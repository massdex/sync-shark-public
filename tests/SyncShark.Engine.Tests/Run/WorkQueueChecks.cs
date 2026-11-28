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

    // A held AutoSync input owns its path: Repair finishes behind it without retrying it, syncs a sibling, syncs its own difference at the end turn and does not consume the held input.
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
        int attempts=0;
        await queue.AdmitAsync(pair,new Row(held.Path,held,null,null,[],null,null!),[],_=>{Interlocked.Increment(ref attempts);throw new WorkQueue.Unsettled();},default);
        var beforeRepair=Volatile.Read(ref attempts);
        using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var events=new List<RunEvent>();
        try { await Repair.RunAsync(pair,ItemChecks.Settings,e=>{lock(events) events.Add(e);},paths,queue,pc,cloud,store,null,null,end.Token).WaitAsync(end.Token); }
        catch(OperationCanceledException) when(end.IsCancellationRequested) { throw new InvalidDataException("Repair joined held work at its path"); }
        Check.Require(Volatile.Read(ref attempts)==beforeRepair,"Repair retried the held AutoSync input before its walk");
        lock(events) Check.Require(events.Count(e=>e.Kind==RunEventKind.Wait && PathNames.Same(e.Path,"a"))==1 && !events.Any(e=>e.Kind==RunEventKind.Failed),"The declined row did not log exactly one Wait line and no Failed line");
        Check.Require(cloud.Items.TryGetValue("b",out var b) && b.Bytes.SequenceEqual(new byte[]{1}),"Repair did not sync the healthy sibling");
        Check.Require(cloud.Items.ContainsKey("a") && !store.OpenNotices.Any(n=>PathNames.Same(n.Path,"a")),"The declined row did not sync at the end of Repair, or a notice at its path stayed open");
        Check.Require(await queue.RetryHeldAsync(null,default),"Repair consumed or dropped the other owner's held input");
    }

    // X is still running when Repair meets the unsent rename x->y. Repair goes first without waiting or declining, and the rename still waits for X (0040).
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
        // z's upload releases X after Repair has already granted y.
        pc.Seed(ItemChecks.File(Side.Local,"z",1,"z"),[1]);
        cloud.AfterStage=()=>gate.TrySetResult();
        using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var events=new List<RunEvent>();
        try { await Repair.RunAsync(pair,ItemChecks.Settings,e=>{lock(events) events.Add(e);},paths,queue,pc,cloud,store,null,null,end.Token).WaitAsync(end.Token); }
        catch(OperationCanceledException) when(end.IsCancellationRequested) { throw new InvalidDataException("Repair kept waiting after its prerequisite parked"); }
        Check.Require(gate.Task.IsCompleted && cloud.Items.ContainsKey("z"),"The late park was not armed by the later row");
        lock(events) Check.Require(events.Count(e=>e.Kind==RunEventKind.Wait && PathNames.Same(e.Path,"y"))==0,"Repair waited or declined behind the unsent rename instead of going first");
        Check.Require(moved==0 && cloud.Items.ContainsKey("y") && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure && PathNames.Same(n.Path,"y")),"The rename ran under parked work, or Repair did not sync y and close its notice");
        released=true; await queue.RetryHeldAsync(null,end.Token);
        await Task.WhenAll(active,page).WaitAsync(end.Token);
        Check.Require(moved==1,"The renamed page did not run once after its prerequisite settled");
    }

    // A healthy Repair row at y goes first past an unsent rename blocked by held work; no decline. It syncs y and the rename runs once the held work settles.
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
        var events=new List<RunEvent>();
        try { await Repair.RunAsync(pair,ItemChecks.Settings,e=>{lock(events) events.Add(e);},paths,queue,pc,cloud,store,null,null,end.Token).WaitAsync(end.Token); }
        catch(OperationCanceledException) when(end.IsCancellationRequested) { throw new InvalidDataException("Repair joined an unsent rename blocked by held work"); }
        Check.Require(moved==0,"The blocked rename ran under held work");
        lock(events) Check.Require(events.Count(e=>e.Kind==RunEventKind.Wait && PathNames.Same(e.Path,"y"))==0,"Repair declined behind the unsent rename instead of going first");
        Check.Require(cloud.Items.ContainsKey("y") && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure && PathNames.Same(n.Path,"y")),"The row did not sync y and close its notice");
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
        var held=ItemChecks.File(Side.Dropbox,"y",1,"held"); bool released=false;
        await queue.AdmitAsync(pair,new Row(held.Path,null,held,null,[],null,null!),[],_=>released?Task.CompletedTask:throw new WorkQueue.Unsettled(),default);
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
    }

    // A granted Repair row at A/x goes first while an unrelated row still runs at B/x: the unsent rename A/x->B/x waits behind the Repair, not beside it.
    public static async Task RepairGoesAheadOfUnsentAsync()
    {
        var queue=new WorkQueue(); var pair=ItemChecks.Pair();
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var x=ItemChecks.File(Side.Local,"B/x",1,"x");
        var running=queue.AdmitAsync(pair,new Row(x.Path,x,null,null,[],null,null!),[],async _=>{await gate.Task;},default);
        int uRuns=0;
        var rename=new Change(Side.Local,ChangeKind.Renamed,"B/x","A/x","u",null,false,false);
        var page=queue.AdmitAsync(pair,new([rename],null,new(1,1)),(_,_)=>{Interlocked.Increment(ref uRuns);return Task.CompletedTask;},(_,_)=>Task.CompletedTask,default);
        int rRuns=0;
        var r=ItemChecks.File(Side.Local,"A/x",1,"r");
        var admitted=Admit(queue,pair,new Row(r.Path,r,null,null,[],null,null!),[],_=>{Interlocked.Increment(ref rRuns);return Task.CompletedTask;},default,false);
        Check.Require(await admitted.WaitAsync(TimeSpan.FromSeconds(10)) && rRuns==1,"Repair did not go first while an unrelated row still ran");
        Check.Require(uRuns==0,"The unsent rename ran while the Repair row held its path");
        gate.SetResult();
        await Task.WhenAll(running,page).WaitAsync(TimeSpan.FromSeconds(10));
        Check.Require(uRuns==1,"The unsent rename did not run exactly once after the Repair row and the running row settled");
    }

    // The behaviours around one gated Repair row: the end turn also puts unsent work behind it, a fault or a cancel does not reach its follower, and a move keeps arrival order.
    public static async Task RepairFollowerOrderAsync()
    {
        await RepairEndTurnOrderAsync();
        await RepairFaultKeepsFollowerAsync();
        await RepairCancelKeepsFollowerAsync();
        await RepairMoveKeepsArrivalOrderAsync();
    }

    // (a) The end turn (bypass) puts the unsent rename behind the row too, so it cannot run beside the gated Repair.
    private static async Task RepairEndTurnOrderAsync()
    {
        var queue=new WorkQueue(); var pair=ItemChecks.Pair();
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var x=ItemChecks.File(Side.Local,"B/x",1,"x");
        var running=queue.AdmitAsync(pair,new Row(x.Path,x,null,null,[],null,null!),[],async _=>{await gate.Task;},default);
        var rename=new Change(Side.Local,ChangeKind.Renamed,"B/x","A/x","u",null,false,false);
        var page=queue.AdmitAsync(pair,new([rename],null,new(1,1)),(_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,default);
        var rGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r=ItemChecks.File(Side.Local,"A/x",1,"r");
        var admitted=Admit(queue,pair,new Row(r.Path,r,null,null,[],null,null!),[],async _=>{await rGate.Task;},default,true);
        Check.Require(WaitsOnRepair(queue,rename),"The end turn did not put the unsent rename behind the Repair row");
        rGate.SetResult(); gate.SetResult();
        Check.Require(await admitted.WaitAsync(TimeSpan.FromSeconds(10)),"The end-turn Repair did not complete");
        await Task.WhenAll(running,page).WaitAsync(TimeSpan.FromSeconds(10));
    }

    // (b) A failed Repair row does not fault the AutoSync input behind it: that input still runs and publishes its page position.
    private static async Task RepairFaultKeepsFollowerAsync()
    {
        var queue=new WorkQueue(); var pair=ItemChecks.Pair();
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var x=ItemChecks.File(Side.Local,"B/x",1,"x");
        var running=queue.AdmitAsync(pair,new Row(x.Path,x,null,null,[],null,null!),[],async _=>{await gate.Task;},default);
        int uRuns=0; JournalPosition? settled=null;
        var rename=new Change(Side.Local,ChangeKind.Renamed,"B/x","A/x","u",null,false,false);
        var page=queue.AdmitAsync(pair,new([rename],null,new(1,7)),(_,_)=>{Interlocked.Increment(ref uRuns);return Task.CompletedTask;},(p,_)=>{settled=p;return Task.CompletedTask;},default);
        var r=ItemChecks.File(Side.Local,"A/x",1,"r");
        var failure=new InvalidOperationException("repair fault");
        var admitted=Admit(queue,pair,new Row(r.Path,r,null,null,[],null,null!),[],_=>throw failure,default,false);
        try { await admitted.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidDataException("A failed Repair row reported success"); }
        catch(InvalidOperationException e) when(ReferenceEquals(e,failure)) { }
        gate.SetResult();
        await Task.WhenAll(running,page).WaitAsync(TimeSpan.FromSeconds(10));
        Check.Require(uRuns==1,"The AutoSync input behind a failed Repair row did not run exactly once");
        Check.Require(settled==new JournalPosition(1,7),"The follower's page did not publish its position");
    }

    // (c) A cancelled Repair row does not cancel the AutoSync input behind it; its caller still gets the cancellation and the follower settles.
    private static async Task RepairCancelKeepsFollowerAsync()
    {
        var queue=new WorkQueue(); var pair=ItemChecks.Pair();
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var x=ItemChecks.File(Side.Local,"B/x",1,"x");
        var running=queue.AdmitAsync(pair,new Row(x.Path,x,null,null,[],null,null!),[],async _=>{await gate.Task;},default);
        int uRuns=0; JournalPosition? settled=null;
        var rename=new Change(Side.Local,ChangeKind.Renamed,"B/x","A/x","u",null,false,false);
        var page=queue.AdmitAsync(pair,new([rename],null,new(1,7)),(_,_)=>{Interlocked.Increment(ref uRuns);return Task.CompletedTask;},(p,_)=>{settled=p;return Task.CompletedTask;},default);
        var r=ItemChecks.File(Side.Local,"A/x",1,"r");
        using var repairStop=new CancellationTokenSource();
        var admitted=Admit(queue,pair,new Row(r.Path,r,null,null,[],null,null!),[],async ct=>{await Task.Delay(Timeout.Infinite,ct);},repairStop.Token,false);
        repairStop.Cancel(); // cancelled while the Repair apply waits on its own token, not the page's
        try { await admitted.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidDataException("A cancelled Repair row reported success"); }
        catch(OperationCanceledException) { }
        gate.SetResult();
        await Task.WhenAll(running,page).WaitAsync(TimeSpan.FromSeconds(10));
        Check.Require(uRuns==1,"The AutoSync input behind a cancelled Repair row did not run exactly once");
        Check.Require(settled==new JournalPosition(1,7),"The follower's page did not publish its position");
    }

    // (d) A Repair move C/x->B/x goes first while X runs at A/x (outside its footprint); a later add W at B/x still waits behind the earlier unsent rename U, and a real departure V follows them in receipt order (0040).
    private static async Task RepairMoveKeepsArrivalOrderAsync()
    {
        var queue=new WorkQueue(); var pair=ItemChecks.Pair();
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var x=ItemChecks.File(Side.Local,"A/x",1,"x");
        var running=queue.AdmitAsync(pair,new Row(x.Path,x,null,null,[],null,null!),[],async _=>{await gate.Task;},default);
        var order=new List<string>(); int uRuns=0;
        var uChange=new Change(Side.Local,ChangeKind.Renamed,"B/x","A/x","u",null,false,false);
        var uPage=queue.AdmitAsync(pair,new([uChange],null,new(1,1)),(_,_)=>{lock(order) order.Add("U");Interlocked.Increment(ref uRuns);return Task.CompletedTask;},(_,_)=>Task.CompletedTask,default);
        var rGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved=SyncedEntries.From(ItemChecks.File(Side.Local,"C/x",1,"c"),ItemChecks.File(Side.Dropbox,"C/x",1,"c"));
        var moved=ItemChecks.File(Side.Local,"B/x",1,"c");
        var cands=new[]{new RenameCandidate(Side.Local,moved,saved)};
        var admitted=Admit(queue,pair,null,cands,async _=>{await rGate.Task;},default,false);
        int wRuns=0;
        var wChange=new Change(Side.Local,ChangeKind.Changed,"B/x",null,"w",null,false,false);
        var wPage=queue.AdmitAsync(pair,new([wChange],null,new(1,2)),(_,_)=>{lock(order) order.Add("W");Interlocked.Increment(ref wRuns);return Task.CompletedTask;},(_,_)=>Task.CompletedTask,default);
        rGate.SetResult(); // release the Repair move while X still runs at A/x
        Check.Require(await admitted.WaitAsync(TimeSpan.FromSeconds(10)),"The Repair move did not complete");
        Check.Require(WaitsOn(queue,uChange,wChange),"The later add at B/x was not kept behind the earlier unsent rename");
        Check.Require(uRuns==0 && wRuns==0,"An unsent input ran while X still ran at A/x");
        gate.SetResult(); // release X
        await Task.WhenAll(running,uPage,wPage).WaitAsync(TimeSpan.FromSeconds(10));
        lock(order) Check.Require(order.SequenceEqual(new[]{"U","W"}) && uRuns==1 && wRuns==1,"U and W did not apply once each in receipt order");
        var vChange=new Change(Side.Local,ChangeKind.Renamed,"D/x","B/x","v",null,false,false);
        await queue.AdmitAsync(pair,new([vChange],null,new(1,3)),(_,_)=>{lock(order) order.Add("V");return Task.CompletedTask;},(_,_)=>Task.CompletedTask,default).WaitAsync(TimeSpan.FromSeconds(10));
        lock(order) Check.Require(order.SequenceEqual(new[]{"U","W","V"}),"A real departure at B/x did not follow the earlier arrivals in receipt order");
        var graph=Graph(queue);
        Check.Require(graph.ByKey.Count==0 && graph.Arrivals.Count==0 && graph.Postponed.Count==0 && graph.Removals.Count==0 && graph.Held.Count==0,"The ordering graph kept dead entries after the arrival chain settled");
    }

    // Call the queue's internal admission seam the way RepairRun does; the checks above observe only effects and the graph it leaves behind.
    private static Task<bool> Admit(WorkQueue queue,PairConfig pair,Row? row,IReadOnlyList<RenameCandidate> candidates,Func<CancellationToken,Task> apply,CancellationToken ct,bool bypass)
    {
        var method=typeof(WorkQueue).GetMethod("AdmitRepairAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WorkQueue.AdmitRepairAsync seam missing");
        return (Task<bool>)method.Invoke(queue,new object?[]{pair,row,candidates,apply,ct,bypass})!;
    }
    private static string TreeKey(string path)=>PathNames.TreeKey(PathNames.Key(path));
    private static object? WorkField(object input,string name)=>input.GetType().GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(input);
    private static IEnumerable<object> GraphItems(WorkQueue queue,string path)
    {
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var order=typeof(WorkQueue).GetField("graph",flags)!.GetValue(queue)!;
        var byKey=(System.Collections.IDictionary)order.GetType().GetField("ByKey",flags)!.GetValue(order)!;
        var key=TreeKey(path);
        return byKey.Contains(key)?((System.Collections.IEnumerable)byKey[key]!).Cast<object>():Enumerable.Empty<object>();
    }
    private static bool WaitsOnRepair(WorkQueue queue,Change change)
    {
        foreach(var item in GraphItems(queue,change.Path!))
            if(ReferenceEquals(WorkField(item,"Change"),change))
                foreach(var prior in (System.Collections.IEnumerable)WorkField(item,"Before")!)
                    if(WorkField(prior,"Repair") is true && WorkField(prior,"Started") is true) return true;
        return false;
    }
    private static bool WaitsOn(WorkQueue queue,Change earlier,Change later)
    {
        foreach(var item in GraphItems(queue,later.Path!))
            if(ReferenceEquals(WorkField(item,"Change"),later))
                foreach(var prior in (System.Collections.IEnumerable)WorkField(item,"Before")!)
                    if(ReferenceEquals(WorkField(prior,"Change"),earlier)) return true;
        return false;
    }
    private static (System.Collections.IDictionary ByKey,System.Collections.IDictionary Arrivals,System.Collections.IDictionary Postponed,System.Collections.IDictionary Removals,System.Collections.IDictionary Held) Graph(WorkQueue queue)
    {
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var order=typeof(WorkQueue).GetField("graph",flags)!.GetValue(queue)!;
        System.Collections.IDictionary Field(string name)=>(System.Collections.IDictionary)order.GetType().GetField(name,flags)!.GetValue(order)!;
        return (Field("ByKey"),Field("arrivals"),Field("postponed"),Field("removals"),Field("HeldByKey"));
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

    // One held item stops settlement while empty pages pile up behind it; a stop must drain them in linear cost and settle none.
    public static async Task StopDrainsDeepBacklogAsync()
    {
        const int count=1024;
        var queue=new WorkQueue(); var pair=ItemChecks.Pair();
        using var stop=new CancellationTokenSource();
        int settled=0;
        Task Settle(JournalPosition _, CancellationToken __) {Interlocked.Increment(ref settled);return Task.CompletedTask;}
        var change=new Change(Side.Local,ChangeKind.Changed,"held",null,null,null,false,false);
        var pages=new Task[count];
        pages[0]=queue.AdmitAsync(pair,new([change],null,new(1,1)),(_,_)=>throw new WorkQueue.Unsettled(),Settle,stop.Token);
        for(int i=1;i<count;++i) pages[i]=queue.AdmitAsync(pair,new([],null,new(1,(ulong)i+1)),(_,_)=>Task.CompletedTask,Settle,stop.Token);
        Check.Require(!pages[^1].IsCompleted && settled==0,"The backlog settled past a held item");
        var before=GC.GetTotalAllocatedBytes(true);
        stop.Cancel();
        try { await Task.WhenAll(pages).WaitAsync(TimeSpan.FromSeconds(30)); throw new InvalidDataException("A stopped backlog did not fail"); }
        catch(OperationCanceledException) { }
        var allocated=GC.GetTotalAllocatedBytes(true)-before;
        Check.Require(settled==0,"A stopped backlog settled a page past the held item");
        // The relayed exception cost about 690 MB at this depth; per-page fresh cancellations cost a few KB each.
        Check.Require(allocated<32L*1024*1024,$"A stopped backlog of {count} pages allocated {allocated:N0} bytes");
    }

    // A genuine failure at the head of a deep backlog: every page behind it fails with that same cause, in linear cost, and none settles.
    public static Task FaultDrainsDeepBacklogAsync()=>FaultDrains(false);
    // The same through overlapping items: each later page's item waits on the one before it, so the cause also travels every item's ordering signal.
    public static Task FaultDrainsOverlappingBacklogAsync()=>FaultDrains(true);
    private static async Task FaultDrains(bool overlap)
    {
        const int count=1024;
        var queue=new WorkQueue(); var pair=ItemChecks.Pair();
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root=new InvalidDataException("head failure");
        int settled=0;
        Task Settle(JournalPosition _, CancellationToken __) {Interlocked.Increment(ref settled);return Task.CompletedTask;}
        var change=new Change(Side.Local,ChangeKind.Changed,"same",null,null,null,false,false);
        var pages=new Task[count];
        pages[0]=queue.AdmitAsync(pair,new([change],null,new(1,1)),async (_,_)=>{await gate.Task;throw root;},Settle,default);
        for(int i=1;i<count;++i) pages[i]=queue.AdmitAsync(pair,new(overlap ? [change] : [],null,new(1,(ulong)i+1)),(_,_)=>Task.CompletedTask,Settle,default);
        Check.Require(!pages[^1].IsCompleted && settled==0,"The backlog settled past a failing item");
        var before=GC.GetTotalAllocatedBytes(true);
        gate.SetResult();
        var drained=Task.WhenAll(pages);
        if(await Task.WhenAny(drained,Task.Delay(TimeSpan.FromSeconds(30)))!=drained) throw new InvalidDataException("A failed backlog did not drain");
        var allocated=GC.GetTotalAllocatedBytes(true)-before;
        Check.Require(settled==0,"A failed backlog settled a page past the failing item");
        // A later page carries the cause directly (the first-failure record unwraps one level); never a chain of wrappers.
        Check.Require(pages.All(p=>p.Exception?.InnerException is {} e && (e==root || e.InnerException==root)),"A page behind the failure reported a different first cause");
        Check.Require(allocated<32L*1024*1024,$"A failed backlog of {count} pages allocated {allocated:N0} bytes");
    }

    // One failure seen by many callers in turn: each overlapping row admission behind it gets the original exception, and no throw grows the next one's stack.
    public static async Task SharedFaultReachesEachCallerAsync()
    {
        const int count=1024;
        var queue=new WorkQueue(); var pair=ItemChecks.Pair();
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root=new InvalidDataException("head failure");
        var entry=ItemChecks.File(Side.Local,"same",1,"pc"); var row=new Row(entry.Path,entry,null,null,[],null,null!);
        int ran=0;
        var callers=new Task[count];
        callers[0]=queue.AdmitAsync(pair,row,[],async _=>{await gate.Task;throw root;},default);
        for(int i=1;i<count;++i) callers[i]=queue.AdmitAsync(pair,row,[],_=>{Interlocked.Increment(ref ran);return Task.CompletedTask;},default);
        var before=GC.GetTotalAllocatedBytes(true);
        gate.SetResult();
        var drained=Task.WhenAll(callers);
        if(await Task.WhenAny(drained,Task.Delay(TimeSpan.FromSeconds(30)))!=drained) throw new InvalidDataException("Callers behind a failure did not drain");
        var allocated=GC.GetTotalAllocatedBytes(true)-before;
        Check.Require(ran==0,"An overlapping row ran past a failed one");
        Check.Require(callers.All(c=>c.Exception?.InnerException is InvalidDataException {Message:"head failure"}),"A caller behind the failure got a different exception");
        Check.Require(allocated<32L*1024*1024,$"{count} callers behind one failure allocated {allocated:N0} bytes");
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
        await FailedPublicationReleasesHeldInputAsync();
        await FailedStorePublicationReleasesHeldInputAsync();
        await TombstoneWaitingForProofNoticeAsync();
    }

    // A failed or cancelled scope publication still releases the input behind it, a held retry included; its caller still gets the error.
    private static async Task FailedPublicationReleasesHeldInputAsync()
    {
        var publish=typeof(WorkQueue).GetMethod("PublishAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WorkQueue.PublishAsync seam missing");
        var pair=ItemChecks.Pair(); using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach(var cancelled in new[]{false,true}) foreach(var held in new[]{false,true})
        {
            var queue=new WorkQueue(); var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var change=new Change(Side.Local,ChangeKind.Changed,"a",null,"id:a",null,false,false);
            int runs=0; bool ready=!held;
            Task Apply(Change _,CancellationToken __) { if(!ready) throw new WorkQueue.Unsettled(); Interlocked.Increment(ref runs); return Task.CompletedTask; }
            // Held: the input parks before the publication; otherwise it is admitted behind the running publication.
            var page=held ? queue.AdmitAsync(pair,new([change],null,new(1,1)),Apply,(_,_)=>Task.CompletedTask,end.Token) : null;
            var failure=new InvalidDataException("publication commit failed");
            var publication=(Task)publish.Invoke(queue,new object[]{new[]{"a"},(Func<Task>)(async ()=>{ await gate.Task; if(cancelled) throw new OperationCanceledException(); throw failure; }),end.Token})!;
            page??=queue.AdmitAsync(pair,new([change],null,new(1,1)),Apply,(_,_)=>Task.CompletedTask,end.Token);
            Task<bool>? retry=null;
            if(held) { ready=true; retry=queue.RetryHeldAsync(null,end.Token); }
            Check.Require(runs==0 && !page.IsCompleted,"An input ran before the publication it follows ended");
            gate.SetResult();
            try { await publication.WaitAsync(end.Token); throw new InvalidDataException("A failed publication reported success to its caller"); }
            catch(Exception e) when(e==failure || e is OperationCanceledException && !end.IsCancellationRequested) { }
            await page.WaitAsync(end.Token); if(retry!=null) await retry.WaitAsync(end.Token);
            Check.Require(runs==1,$"The input behind a failed publication (cancelled={cancelled}, held={held}) did not run exactly once");
        }
    }

    // T4 through the store: a scope publication whose store save fails or is cancelled before commit leaves the scope in force.
    // L15 R5 2A: "(a) and (b) run in both timing orders (P's fault before and after the dependant is claimed), through the real failing
    // store publication, with no scope update observed"; "the dependant re-reads CurrentScope (unchanged) at its run; A's retry runs once
    // and settles; both pages publish".
    private static async Task FailedStorePublicationReleasesHeldInputAsync()
    {
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var publish=typeof(WorkQueue).GetMethod("PublishAsync",flags) ?? throw new InvalidOperationException("WorkQueue.PublishAsync seam missing");
        var scopeNow=typeof(WorkQueue).GetProperty("ScopeNow",flags) ?? throw new InvalidOperationException("WorkQueue.ScopeNow seam missing");
        foreach(var cancelled in new[]{false,true}) foreach(var held in new[]{false,true}) foreach(var faultFirst in new[]{false,true})
        {
            var name=$"cancelled={cancelled}, held={held}, faultFirst={faultFirst}";
            using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var store=new InMemoryStateStore(); var pair=ItemChecks.Pair(); await store.SavePairAsync(pair,default);
            // As Host.OnPairChanged: the live scope changes only when the store publishes a committed pair.
            var before=new PathScope(pair,[pair],DriveType.Fixed); var current=before;
            store.PairChanged=p=>Volatile.Write(ref current,new PathScope(p,[p],DriveType.Fixed));
            var queue=new WorkQueue(); scopeNow.SetValue(queue,(Func<PathScope>)(()=>Volatile.Read(ref current)));
            var fallback=new PathScope(pair,[pair],DriveType.Fixed);
            var seen=new List<PathScope>(); var settled=new List<JournalPosition>(); var ready=!held;
            var change=new Change(Side.Local,ChangeKind.Changed,"a",null,"id:a",null,false,false);
            Task Apply(Change _,CancellationToken __) { if(!Volatile.Read(ref ready)) throw new WorkQueue.Unsettled(); lock(seen) seen.Add(queue.CurrentScope(fallback)); return Task.CompletedTask; }
            Task Settle(JournalPosition at,CancellationToken _) { lock(settled) settled.Add(at); return Task.CompletedTask; }
            // Held: A parks before the publication, so the publication becomes A's prerequisite.
            var first=held ? queue.AdmitAsync(pair,new([change],null,new(1,1)),Apply,Settle,end.Token) : Task.CompletedTask;
            Volatile.Write(ref ready,true);
            var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var command=new CancellationTokenSource(); if(cancelled) command.Cancel();
            Exception? thrown=null;
            var publication=(Task)publish.Invoke(queue,[new[]{"a"},(Func<Task>)(async ()=>{
                await gate.Task;
                // The store refuses before commit: a returned scope with an unknown owner, or the command's cancellation.
                try { await store.SavePairAsync(pair with {IgnoreRules=["*.tmp"]},command.Token,[("absent-owner",new Scope("a","a"))]); }
                catch(Exception e) { thrown=e; throw; }
            }),end.Token])!;
            Task Next()=>queue.AdmitAsync(pair,new([change],null,new(1,2)),Apply,Settle,end.Token);
            // Not fault first: the next page is admitted, so a held A's retry waits in Claim behind the publication.
            var next=faultFirst ? null : Next();
            lock(seen) Check.Require(seen.Count==0 && !publication.IsCompleted && (next==null || !next.IsCompleted),$"{name}: an input ran before the publication it follows ended");
            gate.SetResult();
            try { await publication.WaitAsync(end.Token); throw new InvalidDataException($"{name}: a failed store publication reported success to its caller"); }
            catch(Exception e) when(thrown!=null && (e==thrown || cancelled && e is OperationCanceledException && !end.IsCancellationRequested)) { }
            Check.Require(thrown is var t && (cancelled ? t is OperationCanceledException : t is InvalidDataException),$"{name}: the store did not refuse the publication");
            next??=Next();
            try { await Task.WhenAll(first,next).WaitAsync(end.Token); }
            catch(OperationCanceledException) when(end.IsCancellationRequested) { throw new InvalidDataException($"{name}: the input behind a failed store publication never settled"); }
            lock(seen) Check.Require(seen.Count==(held?2:1) && seen.All(s=>ReferenceEquals(s,before)),$"{name}: an input did not run exactly once under the unchanged live scope");
            lock(settled) Check.Require(settled.SequenceEqual(held ? new JournalPosition[]{new(1,1),new(1,2)} : new JournalPosition[]{new(1,2)}),$"{name}: a page behind the failed publication did not publish");
            Check.Require(ReferenceEquals(Volatile.Read(ref current),before) && (await store.PairAsync(pair.PairId,default))!.IgnoreRules.SequenceEqual(pair.IgnoreRules),
                $"{name}: a failed store publication changed the scope");
        }
    }

    // A Dropbox tombstone waiting for PC proof keeps the PC file and the cursor, and saves one Retry notice naming itself and its PC victim.
    private static async Task TombstoneWaitingForProofNoticeAsync()
    {
        using var store=new InMemoryStateStore();
        var pair=Executor.FolderDeleteChecks.BoundPair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); // an absence-authorized delete checks the saved Dropbox root first
        var pc=Executor.FolderDeleteChecks.Rooted(new FakeSide(Side.Local),pair); var cloud=Executor.FolderDeleteChecks.Rooted(new FakeSide(Side.Dropbox),pair);
        await store.SavePairAsync(pair,default);
        var file=ItemChecks.File(Side.Local,"f",1,"pc-f"); pc.Seed(file,[1]);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(file,ItemChecks.File(Side.Dropbox,"f",1,"db-f")),null,null,default);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        int waits=0; var retried=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The in-place retry's Wait line follows the first attempt's notice save.
        void Events(RunEvent e) { if(e.Kind==RunEventKind.Wait && e.Cause==TextKey.WaitItemRetry && PathNames.Same(e.Path,"f") && Interlocked.Increment(ref waits)==2) retried.TrySetResult(); }
        using var end=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var page=AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,Events,paths,queue,Side.Dropbox,new([new(Side.Dropbox,ChangeKind.Deleted,"f",null,null,null,false,false)],"after",null),pc,cloud,store,end.Token);
        await retried.Task.WaitAsync(end.Token);
        var notice=store.OpenNotices.SingleOrDefault(n=>n.Kind==NoticeKind.Failure && PathNames.Same(n.Path,"f"));
        Check.Require(notice is {Cause:TextKey.WaitItemRetry} && notice.Subject.Items.Any(i=>i.Side==Side.Dropbox && i.Id.Length==0 && PathNames.Same(i.Path,"f")) &&
            notice.Subject.Items.Any(i=>i.Side==Side.Local && i.Id==file.Id),"The waiting tombstone saved no Retry notice naming itself and its PC victim");
        Check.Require(pc.Items.ContainsKey("f") && pc.History.Count==0 && await store.CursorAsync(pair.PairId,default)==null && !page.IsCompleted,"The tombstone removed the PC file or advanced the cursor without proof");
        end.Cancel(); try { await page; } catch(OperationCanceledException) { }
    }
}
