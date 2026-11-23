using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>An exact Dropbox echo consumes no provider reads and no record writes.</summary>
public static class ItemSyncChecks
{
    public static async Task RunAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"file",1,"pc"); var b=ItemChecks.File(Side.Dropbox,"file",1,"db");
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
        await store.CommitAsync(pair.PairId,SyncedEntries.From(a,b),SyncedEntries.From(a,b),new(3,10),null,default);
        var commits=store.Requests[nameof(IStore.CommitAsync)];
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Changed,b.Path,null,b.Id,b,false,false),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,a.Path,null,a.Id,a,true,false,10,JournalId:3),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(pc.Requests[nameof(ISide.ReadOneAsync)]==0 && db.Requests[nameof(ISide.ReadOneAsync)]==0 && store.Requests[nameof(IStore.CommitAsync)]==commits,"Matching feed entry was re-read or re-committed");
        await MoveBeforeRecheckAsync();
        var defects=new List<string>();
        await RemoteReplacementAsync(defects);
        await UncertainDeleteAsync(defects);
        await ComparedNoticesAsync(defects);
        await RetriesChecks.MoveObservationAsync(defects);
        if(typeof(ItemSync).GetMethod("CounterpartAsync")==null || typeof(ItemSync).GetMethods(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public).Single(m=>m.Name=="ReportFailureAsync").IsPrivate) defects.Add("R3-8 shared callers cannot bind the mapper and exact failure owner");
        try { await ItemSync.RequireRemovalAsync(Task.FromResult(new RemovalResult(RemovalOutcome.Pending,null,null))); defects.Add("MF2 pending removal settled"); }
        catch(InventoryIncompleteException) { }
        pc.Seed(a,[1]); db.Seed(b,[1]);
        long reads=pc.Requests[nameof(ISide.ReadOneAsync)];
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,a.Path,null,a.Id,a,true,false,10,JournalId:4),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default); } catch(NotImplementedException) { }
        Check.Require(pc.Requests[nameof(ISide.ReadOneAsync)]>reads,"Wrong journal was accepted as an echo");
        var waitingRow=new Row("held",a,b,null,[],null,RowContext.Neutral);
        bool retained=false;
        try { ItemSync.FinishDecision(new(ActionKind.None,waitingRow,global::Massdex.SyncShark.Engine.Planner.NoneReason.Unfinished),(key,reproducible)=>retained=key==waitingRow.Key && !reproducible); } catch(InventoryIncompleteException) { }
        if(!retained) defects.Add("R2-4 unfinished planner input did not notify the retained-work route");
        var original=ItemChecks.File(Side.Local,"dir",0,"old-folder",true); var peer=ItemChecks.File(Side.Dropbox,"dir",0,"peer-folder",true);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(original,peer),null,null,default);
        pc.Seed(original with {Id="replacement"},[]); db.Seed(peer,[]);
        db.Items[peer.Path]=(peer with {Id="new-cloud-folder"},[]);
        var cloudProbes=db.Requests[nameof(ISide.ReadByIdAsync)];
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,peer.Path,null,null,null,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]); } catch(NotImplementedException) { } catch(InventoryIncompleteException) { }
        if(db.Requests[nameof(ISide.ReadByIdAsync)]==cloudProbes || db.Items[peer.Path].Entry.Id!="new-cloud-folder") defects.Add("R3-1 path tombstone did not select the original Dropbox folder by id");
        db.Items.Remove(b.Path);db.Seed(b with {Path="cloud-new"},[1]);
        cloudProbes=db.Requests[nameof(ISide.ReadByIdAsync)];
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,b.Path,null,null,null,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
        if(db.Requests[nameof(ISide.ReadByIdAsync)]==cloudProbes || pc.Requests[nameof(ISide.DeleteAsync)]!=0 || pc.Items.ContainsKey(a.Path) || !pc.Items.TryGetValue("cloud-new",out var landed) || landed.Entry.Id!=a.Id || !landed.Bytes.SequenceEqual(new byte[]{1})) defects.Add("R3-3 old-name feed tombstone bypassed native move identity");
        Check.Require(defects.Count==0,string.Join("; ",defects));

    }
    // Each side advertises its current finite page. The next fence becomes visible only after projection.
    private static Func<(JournalPosition? Local,string? Dropbox),bool> RemovalCoverage(FakeSide pc,FakeSide db,Side victimSide,Entry victim,List<Change>? projected=null,Change[]? held=null,bool postProof=true)
    {
        var beforeLocal=new ChangePage([],null,new(3,10));var beforeCloud=new ChangePage([],"before-removal",null);
        var afterLocal=new ChangePage(victimSide==Side.Local?[..(held??[]),new Change(Side.Local,ChangeKind.Deleted,victim.Path,null,victim.Id,null,false,true,12,JournalId:3)]:[],null,new(3,13));
        var afterCloud=new ChangePage(victimSide==Side.Dropbox?[new Change(Side.Dropbox,ChangeKind.Deleted,victim.Path,null,victim.Id,null,false,false)]:[],"after-removal",null);
        pc.Pages.Enqueue(beforeLocal);db.Pages.Enqueue(beforeCloud);
        return fence=>
        {
            bool removed=(victimSide==Side.Local?pc:db).History.Any(v=>v.Entry==victim);
            var journal=pc.ReadChangesAsync(null,null,default).GetAwaiter().GetResult();
            var cloud=db.ReadChangesAsync(null,null,default).GetAwaiter().GetResult();
            if(journal!=(removed?afterLocal:beforeLocal) || cloud!=(removed?afterCloud:beforeCloud) || fence.Local!=journal.Position || fence.Dropbox!=cloud.Cursor) return false;
            if(!removed) { pc.Pages.Enqueue(afterLocal);db.Pages.Enqueue(afterCloud); }
            else if(!postProof) return false;
            projected?.AddRange(journal.Changes);projected?.AddRange(cloud.Changes);
            return true;
        };
    }

    /// <summary>Real classifier, effects and removal owners. A tombstone belongs to the old identity, not to its successor.</summary>
    public static async Task TombstoneRecoveryAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var prior=ItemChecks.File(Side.Local,"local-parent",0,"prior",true);var oldPeer=ItemChecks.File(Side.Dropbox,"local-parent",0,"old-peer",true);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(prior,oldPeer),null,null,default);
        pc.Seed(prior with {Id="successor"},[]);db.Seed(oldPeer,[]);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,prior.Path,null,prior.Id,null,false,true),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],coversThrough:RemovalCoverage(pc,db,Side.Dropbox,oldPeer));
        Check.Require(pc.Items[prior.Path].Entry.Id=="successor" && !db.Items.ContainsKey(oldPeer.Path) && db.Mutations.Contains(oldPeer.Id),"Local replacement hid the original selected folder removal");
        var originalFile=ItemChecks.File(Side.Local,"reused",1,"12");var peerFile=ItemChecks.File(Side.Dropbox,"reused",1,"old-cloud");var replacement=ItemChecks.File(Side.Local,originalFile.Path,2,"13");
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(originalFile,peerFile),null,null,default);pc.Seed(replacement,[2]);db.Seed(peerFile,[1]);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,originalFile.Path,null,originalFile.Id,null,false,false,11,JournalId:3),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
        Check.Require(!db.Items.ContainsKey(peerFile.Path) && db.Mutations.Contains(peerFile.Id) && pc.Items[replacement.Path].Entry.Id==replacement.Id,"Recovered delete did not settle only the originally linked file");
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,replacement.Path,null,replacement.Id,replacement,true,false,12,JournalId:3),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
        Check.Require(db.Items[replacement.Path].Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Any(r=>r.LocalId==replacement.Id && r.DropboxId!=peerFile.Id),"Replacement did not sync as its own ordered change");
        var remoteDeletes=db.Requests[nameof(ISide.DeleteAsync)];
        var directory=ItemChecks.File(Side.Local,"dir",0,"local-folder",true); var cloudDir=ItemChecks.File(Side.Dropbox,"dir",0,"old-folder",true);
        pc.Seed(directory,[]);db.Seed(cloudDir with {Id="new-folder"},[]);
        var saved=SyncedEntries.From(directory,cloudDir); await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var child=ItemChecks.File(Side.Local,"dir/item",2,"child");pc.Seed(child,[2]);
        var held=new Change(Side.Local,ChangeKind.Changed,child.Path,null,child.Id,child,true,false);
        var ack=false; held=held with {ConsumeHeld=()=>{Check.Require(pc.Items[child.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[child.Path].Bytes.SequenceEqual(new byte[]{2}),"Held child acknowledged before recovery");ack=true;}};
        var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();var projected=new List<Change>();
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,cloudDir.Path,null,null,null,false,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,_=>projected,coversThrough:RemovalCoverage(pc,db,Side.Local,directory,projected,[held]));
        Check.Require(ack && pc.History.Any(h=>h.Entry.Id==directory.Id) && db.Items[cloudDir.Path].Entry.Id=="new-folder" && db.Requests[nameof(ISide.DeleteAsync)]==remoteDeletes,"Old parent or its successor was mistaken for the other identity");
    }

    public static async Task EmptyFolderHistoryAsync()
    {
        foreach(var (mark,cause) in new (bool?,TextKey?)[]{(false,null),(true,null),(null,null),(null,TextKey.ReadLocalFailed)})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var folder=ItemChecks.File(Side.Local,"empty",0,"folder",true);pc.Seed(folder,[]);db.DeletedHistory[folder.Path]=new DeletionEvidence.DeletedPath(folder.Path,folder.Path);
            var row=new Row(PathNames.Key(folder.Path),folder,null,null,[],null,RowContext.Neutral with {FirstRun=true,CompleteInventory=true,DeletionEligible=true,ProtectedContents=mark,UnreadableCause=cause,Destination=folder.Path});
            bool retained=false;
            try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row,[],pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],(_,_)=>retained=true,coversThrough:RemovalCoverage(pc,db,Side.Local,folder)); }
            catch(InventoryIncompleteException) when(retained && mark==null) { }
            catch(ChangedUnderneathException) when(retained && cause!=null) { }
            if(mark==false) Check.Require(db.Requests[nameof(ISide.DeletedAsync)]==1 && pc.History.Count(h=>h.Entry==folder)==1 && !pc.Items.ContainsKey(folder.Path) && !db.Items.ContainsKey(folder.Path) && pc.Requests[nameof(ISide.ReadChangesAsync)]==2 && db.Requests[nameof(ISide.ReadChangesAsync)]==2,"Empty folder removal needs both exact fences and its history receipt");
            else Check.Require(pc.Items[folder.Path].Entry==folder && pc.History.Count==0 && db.Requests[nameof(ISide.DeletedAsync)]==0 && (mark==true?db.Items[folder.Path].Entry.IsFolder:cause!=null?store.OpenNotices.Any(n=>n.Cause==cause):retained),"Protected, missing or unreadable evidence must not select folder removal");
        }
    }

    public static async Task FeedMoveAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"old",1,"pc"); var b=ItemChecks.File(Side.Dropbox,"old",1,"db");var moved=b with {Path="new"};
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);pc.Seed(a,[1]);db.Seed(moved,[1]);
        var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();
        foreach(var change in new[]{new Change(Side.Dropbox,ChangeKind.Deleted,b.Path,null,null,null,false,false),new Change(Side.Dropbox,ChangeKind.Changed,moved.Path,null,moved.Id,moved,false,false)})
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held);
        Check.Require(pc.Requests[nameof(ISide.DeleteAsync)]==0 && pc.Requests[nameof(ISide.MoveAsync)]==1 && pc.Items["new"].Entry.Id==a.Id && !pc.Items.ContainsKey("old") && store.Records.Values.Single().DropboxPath=="new","Feed relocation became delete/copy or lost the original binding");
        var original=ItemChecks.File(Side.Local,"A",1,"old-pc");var oldCloud=ItemChecks.File(Side.Dropbox,"A",1,"old-db");var actual=original with {Path="B"};var replaced=oldCloud with {Id="successor-db"};
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(original,oldCloud),null,null,default);pc.Seed(actual,[1]);db.Seed(replaced,[1]);
        foreach(var eventInput in new[]{new Change(Side.Dropbox,ChangeKind.Changed,"A",null,replaced.Id,replaced,false,false),new Change(Side.Local,ChangeKind.Renamed,"B","A",actual.Id,actual,false,false)})
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,eventInput,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held);
        Check.Require(db.Items.TryGetValue("A",out var replacementAtA) && replacementAtA.Entry.Id==replaced.Id && pc.Items["B"].Entry.Id==original.Id && store.Records.Values.Any(x=>x.LocalPath=="B" && x.LocalId==original.Id && x.DropboxPath=="B") && store.Records.Values.Any(x=>x.DropboxPath=="A" && x.DropboxId==replaced.Id && x.LocalPath=="A"),"Dropbox replacement and relocated Local original must each reach their own settled binding");
        var moving=ItemChecks.File(Side.Local,"move-source",1,"moving");var mate=ItemChecks.File(Side.Dropbox,moving.Path,1,"mate");
        var arrival=moving with {Path="occupied"};var incumbent=ItemChecks.File(Side.Dropbox,arrival.Path,2,"incumbent");var partner=ItemChecks.File(Side.Local,"partner",2,"partner");
        pc.Seed(arrival,[1]);db.Seed(mate,[1]);db.Seed(incumbent,[2]);pc.Seed(partner,[2]);var owner=SyncedEntries.From(partner,incumbent);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(moving,mate),null,null,default);await store.CommitAsync(pair.PairId,null,owner,null,null,default);
        var rename=new Change(Side.Local,ChangeKind.Renamed,arrival.Path,moving.Path,arrival.Id,arrival,false,false);
        Task Move(CancellationToken ct)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,rename,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),ct,queue.Held);
        using(var stop=new CancellationTokenSource())
        using(await new ItemLocks().AcquireAsync([(Side.Local,ItemLocks.Physical(pair,Side.Local,partner.Path),false)],default))
        {
            var attempt=Move(stop.Token);bool blocked=!attempt.IsCompleted;stop.Cancel();
            try {await attempt;} catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
            Check.Require(blocked && db.Items.ContainsKey(mate.Path) && pc.Items.ContainsKey(arrival.Path),"Composed move ignored contention on incumbent partner");
        }
        bool refused=false;
        try {await Move(default);} catch(ChangedUnderneathException e) when(PathNames.Same(e.Path,arrival.Path)) {refused=true;}
        Check.Require(!refused && store.Records.Values.Contains(owner) && store.Records.Values.Any(x=>x.LocalId==moving.Id && x.DropboxId==mate.Id && x.LocalPath!=arrival.Path),"Released partner did not permit labelled arrival");
        var departing=ItemChecks.File(Side.Local,"from",1,"departing");var absent=ItemChecks.File(Side.Dropbox,"from",1,"absent");
        var split=SyncedEntries.From(departing,absent);await store.CommitAsync(pair.PairId,null,split,null,null,default);
        pc.Seed(departing with {Path="to"},[1]);bool splitHeld=false;
        try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Renamed,"to","from",departing.Id,null,false,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held,(_,_)=>splitHeld=true);} catch(InventoryIncompleteException) when(splitHeld) { }
        Check.Require(!splitHeld && !store.Records.Values.Contains(split) && store.OpenNotices.Any(n=>n.Cause==TextKey.InfoMovedDeleted),"Completed move/delete split was retained as a relink");
        var start=ItemChecks.File(Side.Local,"origin",1,"arriving");var sourcePeer=ItemChecks.File(Side.Dropbox,start.Path,1,"arriving-peer");var landing=start with {Path="vacant"};
        var oldPair=SyncedEntries.From(start,sourcePeer);var gone=SyncedEntries.From(landing with {Id="gone-local"},sourcePeer with {Path=landing.Path,Id="gone-remote"});
        pc.Seed(landing,[1]);db.Seed(sourcePeer,[1]);await store.CommitAsync(pair.PairId,null,oldPair,null,null,default);await store.CommitAsync(pair.PairId,null,gone,null,null,default);
        var arrivalInput=new Change(Side.Local,ChangeKind.Renamed,landing.Path,start.Path,start.Id,landing,false,false);bool retained=false;
        Task Arrive()=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,arrivalInput,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held,(_,_)=>retained=true);
        try {await Arrive();} catch(InventoryIncompleteException) when(retained) { }
        Check.Require(retained && !store.Records.Values.Contains(gone) && store.Records.Values.Contains(oldPair) && db.Items.ContainsKey(start.Path),"Vacating the gone incumbent acknowledged an unfinished arrival");
        await Arrive();Check.Require(!db.Items.ContainsKey(start.Path) && db.Items.TryGetValue(landing.Path,out var arrived) && arrived.Entry.Id==sourcePeer.Id && store.Records.Values.Any(x=>x.LocalId==start.Id && x.DropboxId==sourcePeer.Id && x.LocalPath==landing.Path && x.DropboxPath==landing.Path),"Reobserved arrival did not move the original peer");
        // Until the move owner settles, even exact identities at physical paths are not a logical binding.
        var priorPc=ItemChecks.File(Side.Local,"old-parent/child",1,"mapped-pc");var priorDb=ItemChecks.File(Side.Dropbox,"old-parent/child",1,"mapped-db");
        var link=SyncedEntries.From(priorPc,priorDb);var finalPc=ItemChecks.File(Side.Local,"new-parent/child",2,priorPc.Id);var finalDb=priorDb with {Path="new-parent/child"};
        var oldPcOccupant=ItemChecks.File(Side.Local,priorPc.Path,3,"other-pc");var oldDbOccupant=ItemChecks.File(Side.Dropbox,priorDb.Path,4,"other-db");
        var waypoint=finalDb with {Path=PathNames.Child(PathNames.CaseRenamePath("new-parent",ItemChecks.Settings.CaseRenameSuffix),"child")};
        pc.Seed(finalPc,[2]);db.Seed(waypoint,[1]);pc.Seed(oldPcOccupant,[3]);db.Seed(oldDbOccupant,[4]);await store.CommitAsync(pair.PairId,null,link,null,null,default);
        bool held=false;
        try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Row(link.LocalKey,finalPc,waypoint,link,[],null,RowContext.Neutral with {CompleteInventory=true}),[],pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held,(_,_)=>held=true);} catch(InventoryIncompleteException) when(held) { }
        Check.Require(held && store.Records.Values.Contains(link) && db.Items[waypoint.Path].Bytes.SequenceEqual(new byte[]{1}),"An uncovered hop must not write or record the waypoint");
        await db.MoveAsync(waypoint,finalDb.Path,default);
        Task<EffectOutcome?> ApplyPrepared(bool addOnly)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Row(link.LocalKey,finalPc,finalDb,link,[],null,RowContext.Neutral),[],pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held,addOnly:addOnly);
        var filtered=await ApplyPrepared(true);var applied=await ApplyPrepared(false);
        Check.Require(filtered is null && applied==EffectOutcome.Applied,"Prepared update must return null when filtered and Applied when executed");
        Check.Require(db.Items[finalDb.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[oldDbOccupant.Path].Entry==oldDbOccupant && pc.Items[oldPcOccupant.Path].Entry==oldPcOccupant && store.Records.Values.Any(x=>x.LocalId==finalPc.Id && x.LocalPath==finalPc.Path && x.DropboxPath==finalDb.Path) && !store.Records.Values.Contains(link),"Mapped edit must publish at the verified receiving path, retire the original binding and preserve both old occupants");
    }

    public static async Task ReplacementHeldChildAsync()
    {
        var pair=ItemChecks.Pair() with {FirstRun=FirstRunPolicy.LocalWins};var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var file=ItemChecks.File(Side.Local,"item",1,"pc");var dir=ItemChecks.File(Side.Dropbox,"item",0,"folder",true);var child=ItemChecks.File(Side.Dropbox,"item/child",2,"edit");
        pc.Seed(file,[1]);db.Seed(dir,[]);db.Seed(child,[2]);var claim=new Change(Side.Dropbox,ChangeKind.Changed,child.Path,null,child.Id,child,false,false);var ack=false;claim=claim with {ConsumeHeld=()=>ack=true};
        var row=new Row(PathNames.Key(file.Path),file,dir,null,[],null,RowContext.Neutral with {FirstRun=true,CompleteInventory=true,ProtectedContents=false});
        try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row,[],pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,s=>{if(!PathNames.Same(s.DropboxPath,dir.Path))return [];return [claim];});} catch(ItemFailedException) { } catch(InventoryIncompleteException) { } catch(ChangedUnderneathException) { }
        var exactFailure=store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Path==child.Path && n.Subject.Items.Any(m=>m.Side==Side.Dropbox && m.Id==child.Id && m.Path==child.Path));
        var settledConflict=store.Records.Values.Any(r=>r.DropboxId==child.Id && r.DropboxContentHash==child.ContentHash);
        Check.Require((ack?settledConflict:exactFailure) && db.Items.TryGetValue(child.Path,out var kept) && kept.Bytes.SequenceEqual(new byte[]{2}) && pc.Items[file.Path].Bytes.SequenceEqual(new byte[]{1}),"Replacement neither settled the held version nor retained its exact recovery notice");
    }

    private static async Task RemoteReplacementAsync(List<string> defects)
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"A",1,"original");var old=ItemChecks.File(Side.Dropbox,"A",1,"bound");var b=a with {Path="B"};var replacement=old with {Id="replacement"};
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,old),null,null,default);pc.Seed(b,[1]);db.Seed(replacement,[1]);
        try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Changed,"A",null,replacement.Id,replacement,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);} catch(NotImplementedException) { } catch(InventoryIncompleteException) { }
        if(pc.Requests[nameof(ISide.ReadByIdAsync)]==0 || !pc.Items.TryGetValue("B",out var source) || source.Entry.Id!=a.Id || !db.Items.TryGetValue("A",out var kept) || kept.Entry.Id!=replacement.Id || db.Mutations.Contains(replacement.Id)) defects.Add("Dropbox replacement at A was treated as the old victim without locating Local original B");
    }

    private static async Task UncertainDeleteAsync(List<string> defects)
    {
        foreach(bool postProof in new[]{true,false})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var old=ItemChecks.File(Side.Local,"dir",0,"12",true);var peer=ItemChecks.File(Side.Dropbox,"dir",0,"peer",true);var replacement=old with {Id="13"};
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default);pc.Seed(replacement,[]);db.Seed(peer,[]);
            var input=new Change(Side.Local,ChangeKind.Deleted,old.Path,null,old.Id,null,false,false,11,JournalId:3);
            bool retained=false;
            var coverage=RemovalCoverage(pc,db,Side.Dropbox,peer,postProof:postProof);
            async Task Apply(Change change)
            {
                retained=false;
                try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],(_,_)=>retained=true,coversThrough:coverage);}
                catch(InventoryIncompleteException) when(retained) { }
            }
            pc.Seed(old with {Path="elsewhere"},[]);await Apply(input);
            if(!retained || db.Mutations.Contains(peer.Id)) defects.Add("Uncertain delete of an identity still present elsewhere must not remove its peer");
            pc.Items.Remove("elsewhere");await Apply(input with {Kind=ChangeKind.Changed});
            if(!retained || db.Mutations.Contains(peer.Id)) defects.Add("Missing identity after Changed must not become a deletion");
            await Apply(input);
            if(db.Items.ContainsKey(peer.Path) || db.History.Count(v=>v.Entry==peer)!=1) defects.Add("Confirmed absence did not remove the selected old peer exactly once");
            if(retained==postProof || pc.Requests[nameof(ISide.ReadChangesAsync)]!=2 || db.Requests[nameof(ISide.ReadChangesAsync)]!=2) defects.Add("Selected removal must read each pre/post fence and retain on insufficient post-proof");
            if(pc.Items[old.Path].Entry!=replacement || pc.Mutations.Contains(replacement.Id)) defects.Add("Selected removal touched the local successor");
        }
    }

    private static async Task ComparedNoticesAsync(List<string> defects)
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();
        var pc=ItemChecks.File(Side.Local,"item",1,"pc");var db=ItemChecks.File(Side.Dropbox,"item",1,"db");
        var replay=new Change(Side.Dropbox,ChangeKind.Changed,db.Path,null,db.Id,db,false,false);
        var warning=new Notice(0,NoticeKind.PutBack,TextKey.FailLocalInUse,db.Path,new([new(Side.Dropbox,db.Path,db.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false,true);
        await store.SaveNoticeAsync(pair.PairId,warning,default);
        await store.SaveNoticeAsync(pair.PairId,warning with {Kind=NoticeKind.Failure,Subject=new([new(Side.Local,pc.Path,pc.Id)],Side.Local)},default);
        var before=store.OpenNotices.ToArray();var exact=before.Single(n=>n.Kind==NoticeKind.PutBack);
        await ItemSync.CloseComparedNoticesAsync(pair.PairId,replay,db,pc,before,store,default);
        if(store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure) || store.OpenNotices.SingleOrDefault(n=>n.Kind==NoticeKind.PutBack)?.Matches(exact)!=true)
            defects.Add("Same-ID replay must preserve the exact PutBack and close the matched ordinary Failure");
    }

    public static async Task ReplayPutBackAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"item",1,"pc");var b=ItemChecks.File(Side.Dropbox,"item",1,"db");pc.Seed(a,[1]);db.Seed(b,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
        var replay=new Change(Side.Dropbox,ChangeKind.Changed,b.Path,null,b.Id,b,false,false);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailLocalInUse,b.Path,new([new(Side.Dropbox,b.Path,b.Id)],Side.Dropbox),DateTimeOffset.UtcNow,false,true),default);
        var exact=store.OpenNotices.Single();await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,replay,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(store.OpenNotices.SingleOrDefault()?.Matches(exact)==true,"R4 replay cleared an exact PutBack warning on identity alone");
        var old=ItemChecks.File(Side.Local,"edit",1,"pc-edit");var peer=ItemChecks.File(Side.Dropbox,"edit",1,"db-edit");var edit=ItemChecks.File(Side.Local,old.Path,2,old.Id);
        pc.Seed(edit,[2]);db.Seed(peer,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default);
        var localWarning=exact with {Id=0,Path=old.Path,Subject=new([new(Side.Local,old.Path,old.Id)],Side.Local)};
        await store.SaveNoticeAsync(pair.PairId,localWarning,default);var selected=store.OpenNotices.Single(n=>n.Path==old.Path);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,edit.Path,null,edit.Id,edit,true,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(store.OpenNotices.All(n=>n.Id!=selected.Id) && store.OpenNotices.Any(n=>n.Matches(exact)) && db.Items[peer.Path].Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Any(x=>x.LocalId==edit.Id && x.DropboxContentHash==edit.ContentHash),"Changed bound version must settle its exact PutBack and sync the new bytes");
        foreach(var side in new[]{Side.Local,Side.Dropbox})
        {
            var origin="before-"+side;var destination="after-"+side;
            var left=ItemChecks.File(Side.Local,origin,1,"left-"+side);var right=ItemChecks.File(Side.Dropbox,origin,1,"right-"+side);
            var moved=ItemChecks.File(side,destination,2,side==Side.Local?left.Id:right.Id);
            (side==Side.Local?pc:db).Seed(moved,[2]);(side==Side.Local?db:pc).Seed(side==Side.Local?right:left,[1]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(left,right),null,null,default);
            await store.SaveNoticeAsync(pair.PairId,localWarning with {Path=origin,Subject=new([new(side,origin,moved.Id)],side)},default);
            var priorWarning=store.OpenNotices.Single(n=>n.Path==origin);var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();
            var fence=new ChangePage([],null,new(3,10));var cursor=new ChangePage([],"moved",null);
            pc.Pages.Enqueue(fence);db.Pages.Enqueue(cursor);
            var change=new Change(side,side==Side.Local?ChangeKind.Renamed:ChangeKind.Changed,destination,side==Side.Local?origin:null,moved.Id,moved,true,false);
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,queue.Held,coversThrough:at=>pc.ReadChangesAsync(null,null,default).GetAwaiter().GetResult().Position==at.Local && db.ReadChangesAsync(null,null,default).GetAwaiter().GetResult().Cursor==at.Dropbox && at==(fence.Position,cursor.Cursor));
            Check.Require(store.OpenNotices.All(n=>n.Id!=priorWarning.Id) && store.OpenNotices.Any(n=>n.Matches(exact)) && pc.Items[destination].Bytes.SequenceEqual(new byte[]{2}) && db.Items[destination].Bytes.SequenceEqual(new byte[]{2}),"Moved edit must sync at its destination and close only its old-name PutBack: "+side);
        }
        var fresh=ItemChecks.File(Side.Local,"unbound",3,"unbound-pc");pc.Seed(fresh,[3]);
        await store.SaveNoticeAsync(pair.PairId,localWarning with {Path=fresh.Path,Subject=new([new(Side.Local,fresh.Path,fresh.Id)],Side.Local)},default);var unbound=store.OpenNotices.Single(n=>n.Path==fresh.Path);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,fresh.Path,null,fresh.Id,fresh,true,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(store.OpenNotices.Any(n=>n.Matches(unbound)) && db.Items[fresh.Path].Bytes.SequenceEqual(new byte[]{3}),"An unbound same-ID input may sync but must keep the exact PutBack");
    }

    public static async Task PlannedFailureAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var folder=ItemChecks.File(Side.Local,"parent",0,"pc-parent",true);pc.Seed(folder,[]);
        var context=RowContext.Neutral with {FirstRun=true,CompleteInventory=true,DeletionEligible=true,ProtectedContents=true,UnreadableCause=TextKey.ReadLocalFailed,Destination=folder.Path};
        var row=new Row(PathNames.Key(folder.Path),folder,null,null,[],null,context);Notice? original=null;bool retained=false;
        try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row,[],pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],(key,ok)=>{retained=!ok && key==row.Key && original!=null;throw new global::Massdex.SyncShark.Engine.Run.WorkQueue.Unsettled();},failedNotice:n=>original=n);} catch(global::Massdex.SyncShark.Engine.Run.WorkQueue.Unsettled) { }
        var stored=store.OpenNotices.Single();Check.Require(retained && original!=null && stored.Matches(original with {Id=stored.Id}) && pc.Items.ContainsKey(folder.Path),"R4 planned failure did not hand Repair the exact saved envelope before retaining input");
    }

    private static async Task MoveBeforeRecheckAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var cloud=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var a=ItemChecks.File(Side.Local,"A",1,"pc-id"); var remote=ItemChecks.File(Side.Dropbox,"A",1,"cloud-id");
        pc.Seed(a,[1]); cloud.Seed(remote,[1]); var binding=SyncedEntries.From(a,remote);
        await store.CommitAsync(pair.PairId,null,binding,null,null,default);
        var observed=await pc.ReadOneAsync("A",default) ?? throw new InvalidDataException("Expected A at acquisition");
        var input=new Change(Side.Local,ChangeKind.Changed,"A",null,observed.Id,observed,true,false,11,JournalId:3);
        await pc.MoveAsync(observed,"B",default);
        bool retained=false;
        Task Apply(Func<Scope,IEnumerable<Change>> held)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,input,pc,cloud,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,held,(key,settled)=>retained=key==binding.LocalKey && !settled);
        try {await Apply(_=>[]);} catch(InventoryIncompleteException) when(retained) { }
        Check.Require(retained && !cloud.Mutations.Any() && store.Records.Values.Single()==binding,"Missing old name was settled without a matching rename");
        // Successful grouped application is asserted unconditionally with the real owners in RetainedMovePrefixAsync.

    }

    /// <summary>Both original inputs finish, and only the original cloud identity moves before the prefix advances.</summary>
    public static async Task RetainedMovePrefixAsync()
    {
        foreach(var reuse in new[]{false,true}) await CheckMove(reuse);
        static async Task CheckMove(bool reuse)
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var a=ItemChecks.File(Side.Local,"A",1,"local");var peer=ItemChecks.File(Side.Dropbox,"A",1,"remote");var b=a with {Path="B"};
            pc.Seed(b,[1]);db.Seed(peer,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,peer),null,null,default);
            var fresh=ItemChecks.File(Side.Local,"A",2,"successor");if(reuse) pc.Seed(fresh,[2]);
            var write=new Change(Side.Local,ChangeKind.Changed,"A",null,a.Id,a,true,false,11,JournalId:3);var rename=new Change(Side.Local,ChangeKind.Renamed,"B","A",a.Id,b,false,false,12,JournalId:3);
            var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();var page=new ChangePage([write,rename],null,new(3,13));
            if(reuse)
            {
                var before=pc.Requests[nameof(ISide.SettleJournalAsync)];
                await global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Local,page,pc,db,store,default);
                Check.Require(pc.Requests[nameof(ISide.SettleJournalAsync)]==before+2,"AutoSync did not settle both original journal inputs at reused A");
            }
            else
            {
                List<ulong> completed=[];List<JournalPosition> prefix=[];
                await queue.AdmitAsync(pair,page,async (change,ct)=>
                {
                    await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),ct,queue.Held,(_,reproduced)=>{if(!reproduced)throw new global::Massdex.SyncShark.Engine.Run.WorkQueue.Unsettled();});
                    Check.Require(db.Items.TryGetValue("B",out var moved) && moved.Entry.Id==peer.Id && store.Records.Values.SingleOrDefault() is {LocalPath:"B",DropboxPath:"B"},"Retained write and rename must settle both prefixes with exactly one peer move and the original binding at B");
                    completed.Add(change.Usn!.Value);
                },(position,_)=>{prefix.Add(position);return Task.CompletedTask;},default);
                Check.Require(completed.SequenceEqual(new ulong[]{11,12}) && prefix.SequenceEqual(new[]{new JournalPosition(3,12),new JournalPosition(3,13)}),"Retained write and rename must settle both prefixes with exactly one peer move and the original binding at B");
            }
            Check.Require(db.Requests[nameof(ISide.MoveAsync)]==1 && db.Requests[nameof(ISide.DeleteAsync)]==0 && db.Requests[nameof(ISide.PublishAsync)]==0 && !db.Items.ContainsKey("A") && db.Items.TryGetValue("B",out var result) && result.Entry.Id==peer.Id && store.Records.Values.SingleOrDefault() is {LocalPath:"B",DropboxPath:"B"} link && link.LocalId==a.Id && link.DropboxId==peer.Id,"Retained write and rename must settle both prefixes with exactly one peer move and the original binding at B");
            var settledBefore=pc.Requests[nameof(ISide.SettleJournalAsync)];
            if(reuse)
            {
                await global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Local,new([new(Side.Local,ChangeKind.Changed,"A",null,fresh.Id,fresh,true,false,13,JournalId:3)],null,new(3,14)),pc,db,store,default);
                Check.Require(pc.Items["A"].Entry==fresh && db.Items.TryGetValue("A",out var copy) && copy.Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Any(x=>x.LocalId==fresh.Id && x.DropboxId==copy.Entry.Id),"Reused A did not settle as its own item after the peer moved to B");
            }
            pc.Items.Remove("B");
            await global::Massdex.SyncShark.Engine.Run.AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Local,new([new(Side.Local,ChangeKind.Deleted,"B",null,a.Id,null,false,true,14,JournalId:3)],null,new(3,15)),pc,db,store,default);
            Check.Require(!db.Items.ContainsKey("B") && db.History.Count(x=>x.Entry.Id==peer.Id)==1 && store.Records.Values.All(x=>x.LocalId!=a.Id) && pc.Requests[nameof(ISide.SettleJournalAsync)]==settledBefore+(reuse?2:1),"Genuine deletion after the rename must retire B and settle its journal prefix");
        }
    }

    public static async Task EditedPeerAsync()
    {
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var old=ItemChecks.File(Side.Local,"item",1,"old");var peer=ItemChecks.File(Side.Dropbox,"item",1,"peer");var next=ItemChecks.File(Side.Local,"item",2,"replacement");
            pc.Seed(next,[2]);db.Seed(peer,[1]);var saved=SyncedEntries.From(old,peer);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
            var removal=new Change(Side.Local,ChangeKind.Deleted,old.Path,null,old.Id,null,false,true,1,JournalId:1);
            Task Apply(Change change)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
            db.PublishFailures[peer.Path]=new ItemFailedException(peer.Path,TextKey.FailChanged,null);
            try {await Apply(removal);} catch(ItemFailedException) { }
            Check.Require(db.Items.TryGetValue(peer.Path,out var original) && original.Entry==peer && original.Bytes.SequenceEqual(new byte[]{1}) && store.Records.Values.Contains(saved) && db.Requests[nameof(ISide.DeleteAsync)]==0,"Split safe save deleted the peer before replacement publication");
            db.PublishFailures.Clear();await Apply(new(Side.Local,ChangeKind.Renamed,next.Path,"temp",next.Id,next,true,false,2,JournalId:1));
            Check.Require(db.Items[peer.Path].Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Single().LocalId==next.Id && db.Requests[nameof(ISide.DeleteAsync)]==0,"Split safe save failed to publish the successor");
        }
        foreach(byte version in new byte[]{2,3})
        {
            var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
            var old=ItemChecks.File(Side.Local,"item",1,"old");var peer=ItemChecks.File(Side.Dropbox,"item",1,"peer");var successor=ItemChecks.File(Side.Local,"item",2,"new");var edit=ItemChecks.File(Side.Dropbox,"item",version,peer.Id);
            pc.Seed(successor,[2]);db.Seed(edit,[version]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default);
            try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,old.Path,null,old.Id,null,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);} catch(ChangedUnderneathException) { }
            var links=store.Records.Values.ToArray();
            if(version==2)
                Check.Require(!store.OpenNotices.Any(n=>n.Cause==TextKey.InfoKeptBoth) && links.Length==1 && links[0].LocalId==successor.Id && links[0].DropboxId==peer.Id && pc.Items.Count==1 && db.Items.Count==1 && pc.Items[old.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[peer.Path].Bytes.SequenceEqual(new byte[]{2}),"Equal successor and edited peer must adopt one binding without conflict copies");
            else Check.Require(links.Length==2 && links.Any(x=>x.LocalId==successor.Id && db.Items[x.DropboxPath].Bytes.SequenceEqual(new byte[]{2})) && links.Any(x=>x.DropboxId==peer.Id && pc.Items[x.LocalPath].Bytes.SequenceEqual(new byte[]{3})) && pc.Items[old.Path].Entry==successor && db.Items[peer.Path].Entry==edit,"Retry must link both edited peer and successor versions, not return after the by-ID read");
        }
        await global::Massdex.SyncShark.Engine.Tests.Run.RepairChecks.WithRepairStore(async (store,pair,ct)=>
        {
            var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
            var localDebt=await store.ReadRepairAsync(pair.PairId,Side.Local,ct);var cloudDebt=await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct);
            Check.Require(await store.FinishRepairAsync(pair.PairId,(localDebt.Revision,cloudDebt.Revision),"{}",ct),"Meeting folders fixture did not finish first Repair");
            var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
            var old=ItemChecks.File(Side.Local,"item",1,"old");var peer=ItemChecks.File(Side.Dropbox,"item",1,"peer");
            pc.Seed(ItemChecks.File(Side.Local,"item",0,"new",true),[]);db.Seed(ItemChecks.File(Side.Dropbox,"item",0,peer.Id,true),[]);
            pc.Seed(ItemChecks.File(Side.Local,"item/both",4,"left-version"),[4]);db.Seed(ItemChecks.File(Side.Dropbox,"item/both",5,"right-version"),[5]);
            pc.Seed(ItemChecks.File(Side.Local,"item/left",2,"left"),[2]);db.Seed(ItemChecks.File(Side.Dropbox,"item/right",3,"right"),[3]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,ct);
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,old.Path,null,old.Id,null,false,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),ct,_=>[]);
            bool Linked(SyncedEntry x,byte value)=>pc.Items.TryGetValue(x.LocalPath,out var a) && a.Entry.Id==x.LocalId && a.Bytes.SequenceEqual(new[]{value}) && db.Items.TryGetValue(x.DropboxPath,out var b) && b.Entry.Id==x.DropboxId && b.Bytes.SequenceEqual(new[]{value});
            var left=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("item/both"),ct);var right=await store.FindAsync(pair.PairId,Side.Dropbox,PathNames.Key("item/both"),ct);
            Check.Require(left is {LocalPath:"item/both",LocalId:"left-version"} && Linked(left,4) && right is {DropboxPath:"item/both",DropboxId:"right-version"} && Linked(right,5),"Meeting folders did not preserve and link both original child versions");
            Check.Require(pc.Items.TryGetValue("item/right",out var r) && r.Bytes.SequenceEqual(new byte[]{3}) && db.Items.TryGetValue("item/left",out var l) && l.Bytes.SequenceEqual(new byte[]{2}) && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(old.Path),ct) is {IsFolder:true,LocalId:"new"} root && root.DropboxId==peer.Id,"Meeting successor folders must merge both disjoint children before replacing the file binding");
        });
    }

    /// <summary>Real planner and notice owner required at integration; never count their stubs as success.</summary>
    public static async Task UnreadableAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var old=ItemChecks.File(Side.Local,"old",1,"identity"); var moved=old with {Path="new"};
        var cloud=ItemChecks.File(Side.Dropbox,"old",1,"cloud"); var binding=SyncedEntries.From(old,cloud);
        await store.CommitAsync(pair.PairId,null,binding,null,null,default); pc.Seed(moved,[1]);
        pc.ReadFailures[old.Path]=new ItemFailedException(old.Path,TextKey.ReadLocalFailed,null);
        bool? settled=null;
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Renamed,moved.Path,old.Path,moved.Id,moved,false,false),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,failed:(_,ok)=>settled=ok); } catch(ItemFailedException) { }
        Check.Require(settled==false && store.OpenNotices.Count==1 && store.OpenNotices.Single().Cause==TextKey.ReadLocalFailed && store.Records.Values.Contains(binding),"MF1 unreadable rename probe bypassed notice or lost binding");
        Check.Require(store.OpenNotices.Single().Path==old.Path,"Move did not acquire the old endpoint");
        await store.CloseNoticeAsync(pair.PairId,store.OpenNotices.Single(),default);
        pc.ReadFailures.Clear(); pc.Seed(old with {Skip=nameof(TextKey.SkipUnreadable)},[1]);
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Renamed,moved.Path,old.Path,moved.Id,moved,false,false),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default); } catch(ItemFailedException) { }
        Check.Require(store.OpenNotices.Count==1 && store.OpenNotices.Single().Path==old.Path && store.OpenNotices.Single().Cause==TextKey.ReadLocalFailed,"Unreadable old rename member was used as identity evidence");
        await store.CloseNoticeAsync(pair.PairId,store.OpenNotices.Single(),default); pc.Items.Remove(old.Path);
        pc.ReadFailures.Clear(); pc.Items.Remove(moved.Path); var changed=old with {Modified=old.Modified.AddSeconds(1)};
        pc.Seed(changed,[2]); db.Seed(cloud,[1]); pc.ContentFailures[old.Path]=new ItemFailedException(old.Path,TextKey.ReadLocalFailed,null);
        settled=null;
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,old.Path,null,old.Id,changed,true,false),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,failed:(_,ok)=>settled=ok); } catch(ItemFailedException) { }
        Check.Require(pc.Requests[nameof(ISide.ContentHashAsync)]==1 && settled!=null && store.OpenNotices.Count==1 && store.OpenNotices.Single().Cause==TextKey.ReadLocalFailed && store.Records.Values.Contains(binding) && pc.Items[old.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[old.Path].Bytes.SequenceEqual(new byte[]{1}),"MF1 unreadable digest lost binding, changed content, or did not record one failure");
    }

}
