using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class AutoSyncChecks
{
    public static async Task RunAsync()
    {
        const string root="parent", path="parent/item";
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct=timeout.Token;
        var pair=ItemChecks.Pair(); pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var paths=new PathScope(pair,[pair],DriveType.Fixed); var queue=new WorkQueue();
        await store.SavePairAsync(pair,ct); await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,pc,db,store,ct);
        var old=ItemChecks.File(Side.Local,root,0,"pc-parent",true); var peer=ItemChecks.File(Side.Dropbox,root,0,"remote-parent",true);
        db.Seed(peer,[]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,ct);
        var first=ItemChecks.File(Side.Dropbox,path,1,"child"); var last=ItemChecks.File(Side.Dropbox,path,2,"child"); db.Seed(last,[2]);
        foreach(var item in new[]{first,last}) db.Recoverable[(item.Id,item.Revision,item.ContentHash)]=new Recovery.File(item,new MemoryStream(new byte[]{item==first?(byte)1:(byte)2}));
        pc.PublishFailures[path]=new ItemFailedException(path,TextKey.FailLocalInUse,null);
        Change Report(Entry v)=>new(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prerequisite=queue.AdmitAsync(pair,new([new(Side.Local,ChangeKind.Changed,root,null,old.Id,old,false,false)],null,null),(_,token)=>release.Task.WaitAsync(token),null,ct);
        var removal=Apply(Side.Local,new([new(Side.Local,ChangeKind.Deleted,root,null,old.Id,null,false,true,1,JournalId:1)],null,new(1,2)));
        var children=Apply(Side.Dropbox,new([Report(first),new(Side.Dropbox,ChangeKind.Deleted,path,null,null,null,false,true),Report(last)],"after",null));
        release.SetResult(); await prerequisite;
        Notice? pending=null;
        while(pending==null)
        {
            if(removal.IsFaulted) await removal;
            if(children.IsCompleted) {await children;throw new InvalidDataException("Children escaped the removal before its put-back retry");}
            await foreach(var notice in store.NoticesAtAsync(pair.PairId,PathNames.Key(path),ct)) if(notice.Kind==NoticeKind.PutBack) pending=notice;
            if(pending==null) await Task.Delay(10,ct);
        }
        Check.Require(await store.CursorAsync(pair.PairId,ct)==null && queue.Held(new(root,root)).Any(c=>c.Observed==last),"Failed recovery lost the exact final version or advanced the cursor");
        pc.PublishFailures.Remove(path);
        await new Retries().RetryAsync(pair,ItemChecks.Settings,_=>{},paths,pending,pc,db,store,queue.Held,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),ct);
        await Task.WhenAll(removal,children).WaitAsync(ct);
        Check.Require(await store.CursorAsync(pair.PairId,ct)=="after" && !queue.Held(new(root,root)).Any() && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack),"Recovery left held work or failed to settle its cursor");
        Check.Require(pc.Items[path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[path].Bytes.SequenceEqual(new byte[]{2}),"Recovery lost the revision after the path-only tombstone");
        Task Apply(Side side,ChangePage page)=>AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,side,page,pc,db,store,ct);
    }
}
