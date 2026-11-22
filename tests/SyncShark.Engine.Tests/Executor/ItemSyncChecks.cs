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
        try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,b.Path,null,null,null,false,false),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);} catch(NotImplementedException) { }
        if(db.Requests[nameof(ISide.ReadByIdAsync)]==cloudProbes || pc.Requests[nameof(ISide.DeleteAsync)]!=0 || !pc.Items.ContainsKey(a.Path)) defects.Add("R3-3 old-name feed tombstone bypassed native move identity");
        Check.Require(defects.Count==0,string.Join("; ",defects));

    }
    /// <summary>Real classifier, effects and removal owners. A tombstone belongs to the old identity, not to its successor.</summary>
    public static async Task TombstoneRecoveryAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var prior=ItemChecks.File(Side.Local,"local-parent",0,"prior",true);var oldPeer=ItemChecks.File(Side.Dropbox,"local-parent",0,"old-peer",true);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(prior,oldPeer),null,null,default);
        pc.Seed(prior with {Id="successor"},[]);db.Seed(oldPeer,[]);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,prior.Path,null,prior.Id,null,false,true),pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
        Check.Require(pc.Items[prior.Path].Entry.Id=="successor" && !db.Items.ContainsKey(oldPeer.Path) && db.Mutations.Contains(oldPeer.Id),"Local replacement hid the original selected folder removal");
        var remoteDeletes=db.Requests[nameof(ISide.DeleteAsync)];
        var directory=ItemChecks.File(Side.Local,"dir",0,"local-folder",true); var cloudDir=ItemChecks.File(Side.Dropbox,"dir",0,"old-folder",true);
        pc.Seed(directory,[]);db.Seed(cloudDir with {Id="new-folder"},[]);
        var saved=SyncedEntries.From(directory,cloudDir); await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var child=ItemChecks.File(Side.Local,"dir/item",2,"child");pc.Seed(child,[2]);
        var held=new Change(Side.Local,ChangeKind.Changed,child.Path,null,child.Id,child,true,false);
        var ack=false; held=held with {ConsumeHeld=()=>{Check.Require(pc.Items[child.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[child.Path].Bytes.SequenceEqual(new byte[]{2}),"Held child acknowledged before recovery");ack=true;}};
        var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Dropbox,ChangeKind.Deleted,cloudDir.Path,null,null,null,false,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default,_=>[held]);
        Check.Require(ack && pc.History.Any(h=>h.Entry.Id==directory.Id) && db.Items[cloudDir.Path].Entry.Id=="new-folder" && db.Requests[nameof(ISide.DeleteAsync)]==remoteDeletes,"Old parent or its successor was mistaken for the other identity");
    }

    public static async Task EmptyFolderHistoryAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var folder=ItemChecks.File(Side.Local,"empty",0,"folder",true);pc.Seed(folder,[]);db.DeletedHistory[folder.Path]=new DeletionEvidence.DeletedPath(folder.Path,folder.Path);
        var row=new Row(PathNames.Key(folder.Path),folder,null,null,[],null,RowContext.Neutral with {FirstRun=true,CompleteInventory=true,DeletionEligible=true,ProtectedContents=false,Destination=folder.Path});
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row,[],pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[]);
        Check.Require(db.Requests[nameof(ISide.DeletedAsync)]==1 && pc.History.Any(h=>h.Entry.Id==folder.Id) && !db.Items.ContainsKey(folder.Path),"R3-4 marked empty folder did not consume its tombstone and complete the classified removal");
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

    private static async Task UncertainDeleteAsync(List<string> defects)
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var old=ItemChecks.File(Side.Local,"dir",0,"12",true);var peer=ItemChecks.File(Side.Dropbox,"dir",0,"peer",true);var replacement=old with {Id="13"};
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default);pc.Seed(replacement,[]);db.Seed(peer,[]);
        var input=new Change(Side.Local,ChangeKind.Deleted,old.Path,null,old.Id,null,false,false,11,JournalId:3);
        bool retained=false;
        async Task Apply(Change change)
        {
            retained=false;
            try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,pc,db,store,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,_=>[],(_,_)=>retained=true);}
            catch(InventoryIncompleteException) when(retained) { }
            catch(NotImplementedException) { } // Only the W16 disposition is checked here, not completion of the folder effect.
        }
        pc.Seed(old with {Path="elsewhere"},[]);await Apply(input);
        if(!retained) defects.Add("Uncertain delete of an identity still present elsewhere must not remove its peer");
        pc.Items.Remove("elsewhere");await Apply(input with {Kind=ChangeKind.Changed});
        if(!retained) defects.Add("Missing identity after Changed must not become a deletion");
        var before=pc.Requests[nameof(ISide.ReadByIdAsync)];await Apply(input);
        if(retained || pc.Requests[nameof(ISide.ReadByIdAsync)]==before || pc.Items[old.Path].Entry!=replacement || pc.Mutations.Contains(replacement.Id))
            defects.Add("Retry retained an uncertain Local Deleted after the original ID was absent and a replacement owned its path");
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
        var rename=new Change(Side.Local,ChangeKind.Renamed,"B","A",a.Id,pc.Items["B"].Entry,false,false,12,JournalId:3);
        retained=false; long before=pc.Requests[nameof(ISide.ReadByIdAsync)];
        try {await Apply(_=>[rename]);} catch(NotImplementedException) { } catch(InventoryIncompleteException) when(retained) { }
        Check.Require(pc.Requests[nameof(ISide.ReadByIdAsync)]>before && !retained && cloud.Requests[nameof(ISide.DeleteAsync)]==0 && pc.Items["B"].Entry.Id==a.Id,"R4 retained Changed(A) never located the original identity at B before the rename");
        if(cloud.Items.ContainsKey("B")) Check.Require(cloud.Items["B"].Entry.Id==remote.Id && store.Records.Values.Single().LocalPath=="B","Move did not preserve peer identity and binding");
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
