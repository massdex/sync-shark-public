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
        var failures=new List<string>();
        foreach(var test in new Func<Task>[]{RecoveryAsync,PartialAsync,OutsideAsync,TombstoneAsync,ForeignTombstoneAsync,ExcludedAsync,UnreadableAsync,CostAsync})
        {
            try {await test();} catch(Exception e) {failures.Add(test.Method.Name+": "+e.Message);}
        }
        Check.Require(failures.Count==0,string.Join("; ",failures));
    }
    internal static PairConfig BoundPair()
    {
        var p=ItemChecks.Pair(); return p with {Roots=new(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(1,1,null),new("account","bound-namespace","home","root"))};
    }
    private static async Task RecoveryAsync()
    {
        var pair=BoundPair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
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
        var bound=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(last.Path),default);
        Check.Require(bound!=null && bound.LocalId==pc.Items[last.Path].Entry.Id && bound.DropboxId==db.Items[last.Path].Entry.Id && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack),"Missing exact recovered binding or still-open notice");
        Check.Require(retried && consumedLast && pc.Items[last.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[last.Path].Bytes.SequenceEqual(new byte[]{2}),"Retry consumed the child without restoring the last reported bytes on both sides");
    }

    private static Task PartialAsync()=>Replay(0);
    private static Task OutsideAsync()=>Replay(1);
    private static Task TombstoneAsync()=>Replay(2);
    private static Task ForeignTombstoneAsync()=>Replay(3);
    private static Task ExcludedAsync()=>Replay(4);
    private static async Task Replay(int scenario)
    {
        var pair=BoundPair(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        const string path="p/item"; var version=ItemChecks.File(Side.Local,path,2,"old"); pc.History.Add((version,[2]));
        if(scenario==4) pair=pair with {Exclusions=[new(Side.Local,"p",false)]};
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,path,new([new(Side.Local,path,version.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
        Notice current()=>store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack);
        bool released=false,foreign=false;var edit=new Change(Side.Local,ChangeKind.Changed,path,null,version.Id,version,true,false){ConsumeHeld=()=>released=true};
        Change[] evidence=scenario==2?[edit with {Kind=ChangeKind.Deleted,Observed=null}]:scenario==3?[edit,edit with {Kind=ChangeKind.Deleted,Observed=null,Id="different",ConsumeHeld=()=>foreign=true}]:[edit];
        var scope=scenario==1?new Scope("else","else"):new Scope("p","p");
        Task<bool> ReplayNotice()=>FolderDelete.RetryHeldAsync(pair,scope,current(),pc,db,store,_=>evidence,(_,_,_,_)=>throw new InvalidOperationException(),default);
        if(scenario==0){db.PublishFailures[path]=new ItemFailedException(path,TextKey.FailLocalInUse,null);Check.Require(!await ReplayNotice() && pc.Items.ContainsKey(path) && !released,"First restored leg or original evidence lost");db.PublishFailures.Clear();}
        var result=await ReplayNotice();
        if(scenario==1) Check.Require(!result && !released && current().Path==path,"Outside scope incorrectly settled");
        else Check.Require(result && released && !foreign && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack) && (scenario==2 || db.Items[path].Bytes.SequenceEqual(new byte[]{2})) && (scenario!=4 || !pc.Items.ContainsKey(path)),"Wrong replay result, notice custody, or affected endpoint");
    }
    private static Task UnreadableAsync()=>Removal(true);
    private static Task CostAsync()=>Removal(false);
    private static async Task Removal(bool partial)
    {
        var pair=ItemChecks.Pair(); if(partial) pair=pair with {Exclusions=[new(Side.Local,"p/kept",true)]};
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);
        foreach(var name in new[]{"a","b","c","d"}){var v=ItemChecks.File(Side.Local,"p/"+name,1,name);pc.Seed(v,[1]);if(!partial) await store.CommitAsync(pair.PairId,null,SyncedEntries.From(v,v with {Id="remote-"+name}),null,null,default);}
        if(partial)pc.ReadFailures["p/d"]=new ItemFailedException("p/d",TextKey.ReadLocalFailed,null);
        var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(partial?pc.History.Count==0:pc.Requests[nameof(ISide.ReadOneAsync)]<4 && db.Requests[nameof(ISide.ReadOneAsync)]<4,"Sibling removed before unreadable result, or recursive removal probed every saved child");
    }
}
