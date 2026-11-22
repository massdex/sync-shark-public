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
        var defects=new List<string>();
        try { await ItemSync.RequireRemovalAsync(Task.FromResult(new RemovalResult(RemovalOutcome.Pending,null,null))); defects.Add("MF2 pending removal settled"); }
        catch(InventoryIncompleteException) { }
        long reads=pc.Requests[nameof(ISide.ReadOneAsync)];
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,a.Path,null,a.Id,a,true,false,10,JournalId:4),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default); } catch(NotImplementedException) { }
        Check.Require(pc.Requests[nameof(ISide.ReadOneAsync)]>reads,"Wrong journal was accepted as an echo");
        var original=ItemChecks.File(Side.Local,"dir",0,"old-folder",true); var peer=ItemChecks.File(Side.Dropbox,"dir",0,"peer-folder",true);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(original,peer),null,null,default);
        pc.Seed(original with {Id="replacement"},[]); db.Seed(peer,[]);
        var readsBefore=pc.Requests[nameof(ISide.ReadByIdAsync)];
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Deleted,"dir",null,original.Id,null,false,true),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,_=>[]); } catch(NotImplementedException) { }
        if(pc.Requests[nameof(ISide.ReadByIdAsync)]==readsBefore || pc.Items["dir"].Entry.Id!="replacement") defects.Add("R2-1 old folder identity was not checked independently of its replacement");
        var move=new RenameCandidate(Side.Local,a with {Path="new-file"},SyncedEntries.From(a,b));
        db.ReadFailures[b.Path]=new ItemFailedException(b.Path,TextKey.ReadDropboxFailed,null);
        long oldReads=db.Requests[nameof(ISide.ReadOneAsync)];
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Row("new-file",move.Moved,null,null,[],null,RowContext.Neutral),[move],pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default); } catch(NotImplementedException) { } catch(ItemFailedException) { }
        if(db.Requests[nameof(ISide.ReadOneAsync)]==oldReads) defects.Add("R2-3 move omitted the original counterpart read");
        db.ReadFailures.Clear();
        Check.Require(defects.Count==0,string.Join("; ",defects));

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
        await store.CloseNoticeAsync(pair.PairId,store.OpenNotices.Single(),default);
        pc.ReadFailures.Clear(); pc.Items.Remove(moved.Path); var changed=old with {Modified=old.Modified.AddSeconds(1)};
        pc.Seed(changed,[2]); db.Seed(cloud,[1]); pc.ContentFailures[old.Path]=new ItemFailedException(old.Path,TextKey.ReadLocalFailed,null);
        settled=null;
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,old.Path,null,old.Id,changed,true,false),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,failed:(_,ok)=>settled=ok); } catch(ItemFailedException) { }
        Check.Require(pc.Requests[nameof(ISide.ContentHashAsync)]==1 && settled!=null && store.OpenNotices.Count==1 && store.OpenNotices.Single().Cause==TextKey.ReadLocalFailed && store.Records.Values.Contains(binding) && pc.Items[old.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[old.Path].Bytes.SequenceEqual(new byte[]{1}),"MF1 unreadable digest lost binding, changed content, or did not record one failure");
    }

}
