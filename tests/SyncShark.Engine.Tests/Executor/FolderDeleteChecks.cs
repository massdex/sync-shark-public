using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks evidence custody across a folder removal.</summary>
public static class FolderDeleteChecks
{
    /// <summary>A failed publication keeps the newest reported revision available to Retry.</summary>
    public static async Task RunAsync()
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var a=ItemChecks.File(Side.Local,"parent",0,"a",true); var b=ItemChecks.File(Side.Dropbox,a.Path,0,"b",true);
        var binding=SyncedEntries.From(a,b); await store.CommitAsync(pair.PairId,null,binding,null,null,default); db.Seed(b,[]);
        var first=ItemChecks.File(Side.Dropbox,"parent/child",1,"child"); var last=ItemChecks.File(Side.Dropbox,first.Path,2,first.Id);
        db.Seed(last,[2]); db.Recoverable[(last.Id,last.Revision,last.ContentHash)]=new Recovery.File(last,new MemoryStream([2]));
        pc.PublishFailures[last.Path]=new ItemFailedException(last.Path,TextKey.FailLocalInUse,null);
        bool consumedLast=false; var prior=new Change(Side.Dropbox,ChangeKind.Changed,first.Path,null,first.Id,first,false,false);
        var reports=new[]{prior,prior with {Observed=last,ConsumeHeld=()=>consumedLast=true}};
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,binding,Side.Local,pc,db,store,_=>reports,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(!consumedLast && store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Path==last.Path),"Failed put-back lost its newest held revision or its Retry notice");
        var notice=store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack); pc.PublishFailures.Clear();
        db.Recoverable[(last.Id,last.Revision,last.ContentHash)]=new Recovery.File(last,new MemoryStream([2]));
        Check.Require(!await FolderDelete.RetryHeldAsync(pair,new("parent","parent"),notice,pc,db,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),default),"Empty replay discharged the required revision");
        bool retried=await FolderDelete.RetryHeldAsync(pair,new("parent","parent"),notice,pc,db,store,_=>reports,(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(retried && consumedLast && pc.Items[last.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[last.Path].Bytes.SequenceEqual(new byte[]{2}),"Retry consumed the child without restoring the last reported bytes on both sides");
    }
}
