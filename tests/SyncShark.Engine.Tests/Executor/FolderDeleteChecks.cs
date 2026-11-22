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
        foreach(var test in new Func<Task>[]{RecoveryAsync,PartialAsync,OutsideAsync,TombstoneAsync,ForeignTombstoneAsync,ExcludedAsync,UnreadableAsync,CostAsync,NamespaceAsync,MappedAsync,FolderRetryAsync,ReceiptOrderAsync,LateMergeAsync,CrashAfterFirstAsync,LostReplyAsync,NewerAsync,KeptTreeAsync,CoveredAsync,ConflictAsync,ReceiptCoverageAsync,ConflictingLiveAsync,SubstituteAsync,QuietAsync})
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

    public class Observe : System.Reflection.DispatchProxy
    {
        private ISide target=null!;private Func<string,object?[],object?> intercept=null!;
        internal static ISide On(ISide target,Func<string,object?[],object?> intercept)
        {
            var side=Create<ISide,Observe>();var proxy=(Observe)(object)side;proxy.target=target;proxy.intercept=intercept;return side;
        }
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            var result=intercept(method!.Name,args??[]);if(result!=null)return result;
            try{return method.Invoke(target,args);}catch(System.Reflection.TargetInvocationException ex) when(ex.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}
        }
    }
    private static async Task NamespaceAsync()
    {
        var pair=BoundPair();var db=new FakeSide(Side.Dropbox);var local=new FakeSide(Side.Local);using var store=new InMemoryStateStore();var v=ItemChecks.File(Side.Dropbox,"p/item",1,"rev-id");
        var remote=Observe.On(db,(op,args)=>op==nameof(ISide.RecoverAsync)?Task.FromResult<Recovery?>(((IdentityLocation)args[0]!).Domain==pair.Roots!.Dropbox.RootNamespaceId?new Recovery.File(v,new MemoryStream([1])):null):null);
        var report=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,v.Path,new([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);
        bool ok=await FolderDelete.RetryHeldAsync(pair,new("p","p"),store.OpenNotices.Single(),local,remote,store,_=>[report],(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(ok && local.Items[v.Path].Bytes.SequenceEqual(new byte[]{1}) && db.Items[v.Path].Bytes.SequenceEqual(new byte[]{1}),"Historical recovery used connection instead of bound namespace");
    }
    private static async Task MappedAsync()
    {
        var pair=BoundPair() with {Exclusions=[new(Side.Local,"different/kept",true)]};var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var selected=ItemChecks.File(Side.Dropbox,"p",0,"root",true);var child=ItemChecks.File(Side.Dropbox,"p/item",1,"db-child");var other=ItemChecks.File(Side.Local,"different/kept",1,"pc-child");db.Seed(selected,[]);db.Seed(child,[1]);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(other,child),null,null,default);
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),selected,null,Side.Dropbox,ActionKind.DeleteDropbox,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(db.Items.ContainsKey(child.Path) && db.History.Count==0,"Mapped excluded child was removed");
    }
    private static async Task FolderRetryAsync()
    {
        var pair=BoundPair();var db=new FakeSide(Side.Dropbox);var pc=new FakeSide(Side.Local);using var store=new InMemoryStateStore();var v=ItemChecks.File(Side.Dropbox,"p/folder",0,"folder",true);db.Seed(v,[]);db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.Folder(v,db.ReadAllAsync(v.Path,new(false,true),default));
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,v.Path,new([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);
        var reviewed=store.OpenNotices.Single();var next=reviewed with {Subject=new([new(Side.Dropbox,v.Path,"successor")],Side.Dropbox)};
        var c=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);
        var ok=await FolderDelete.RetryHeldAsync(pair,new("p","p"),reviewed,pc,db,store,_=>[c],(_,_,_,ct)=>store.SaveNoticeAsync(pair.PairId,next,ct),default);
        Check.Require(!ok && store.OpenNotices.Single().Matches(next),"Folder Retry reported success while reviewed notice was superseded");
    }

    private static Task ReceiptOrderAsync()=>ReceiptAsync(false);
    private static Task LateMergeAsync()=>ReceiptAsync(true);
    private static async Task ReceiptAsync(bool unbound)
    {
        var pair=ItemChecks.Pair();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var folder=ItemChecks.File(Side.Local,"p",0,"root",true);var first=ItemChecks.File(Side.Local,"p/child",1,"selected");var last=ItemChecks.File(Side.Local,first.Path,2,first.Id);pc.Seed(folder,[]);
        if(!unbound){pc.Seed(first,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(first,first with {Id="peer"}),null,null,default);}
        async IAsyncEnumerable<Listed> Receipt(){await Task.CompletedTask;yield return new(last.Path,last);}
        async Task<RemovalResult> Remove(){pc.Items[last.Path]=(last,[2]);var result=await pc.DeleteAsync(folder,null,default);return result with {Recycled=Receipt()};}
        var local=Observe.On(pc,(op,_)=>op==nameof(ISide.DeleteAsync)?Remove():null);bool ack=false;
        var stale=new Change(Side.Local,ChangeKind.Changed,first.Path,null,first.Id,first,true,false){ConsumeHeld=()=>ack=true};
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),folder,null,Side.Local,unbound?ActionKind.MoveLocal:ActionKind.DeleteLocal,local,db,store,_=>unbound?[]:[stale],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require((ack||unbound) && pc.Items.TryGetValue(last.Path,out var localCopy) && localCopy.Bytes.SequenceEqual(new byte[]{2}) && db.Items.TryGetValue(last.Path,out var remoteCopy) && remoteCopy.Bytes.SequenceEqual(new byte[]{2}),"Verified newer recycled edit or unbound late merge child was lost");
    }

    private static async Task CrashAfterFirstAsync()
    {
        var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var folder=ItemChecks.File(Side.Dropbox,"p",0,"parent",true);db.Seed(folder,[]);
        var v=ItemChecks.File(Side.Dropbox,"p/item",3,"original");db.Seed(v,[3]);db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([3]));
        var c=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);var stop=new OperationCanceledException();
        async Task<RemovalResult> Crash(object?[] a){await pc.PublishAsync((IStaged)a[0]!,null,null,default);throw stop;}
        var broken=Observe.On(pc,(op,args)=>op==nameof(ISide.PublishAsync)?Crash(args):null);
        try{await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),folder,null,Side.Dropbox,ActionKind.DeleteDropbox,broken,db,store,_=>[c],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);}catch(OperationCanceledException ex) when(ex==stop){}
        var notice=store.OpenNotices.SingleOrDefault(n=>n.Kind==NoticeKind.PutBack);Check.Require(notice!=null && notice.Subject.Items.Any(m=>m.Side==Side.Local && m.Id==pc.Items[v.Path].Entry.Id),"Restart has no attributable first publication identity");
        Check.Require(await FolderDelete.RetryHeldAsync(pair,new("p","p"),notice!,pc,db,store,_=>[c],(_,_,_,_)=>Task.CompletedTask,default) && db.Items[v.Path].Bytes.SequenceEqual(new byte[]{3}) && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack),"Restart rejected the already-published first leg");
    }

    private static Task LostReplyAsync()=>Safety(false);
    private static Task NewerAsync()=>Safety(true);
    private static async Task Safety(bool newer)
    {
        var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var v=ItemChecks.File(Side.Dropbox,"p/item",3,"old");
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,v.Path,new([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([3]));
        var change=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);
        Task<bool> Retry(ISide cloud)=>FolderDelete.RetryHeldAsync(pair,new("p","p"),store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack),pc,cloud,store,_=>[change],(_,_,_,_)=>Task.CompletedTask,default);
        if(newer){pc.Seed(ItemChecks.File(Side.Local,v.Path,5,"new"),[5]);Check.Require(!await Retry(db) && pc.Items[v.Path].Bytes.SequenceEqual(new byte[]{5}) && db.Items.TryGetValue(v.Path,out var kept) && kept.Bytes.SequenceEqual(new byte[]{3}) && store.Records.Count==0,"Recovery did not preserve both versions outside history");return;}
        async Task<RemovalResult> Lost(object?[] a){await db.PublishAsync((IStaged)a[0]!,null,null,default);throw new OperationCanceledException();}
        try{await Retry(Observe.On(db,(op,args)=>op==nameof(ISide.PublishAsync)?Lost(args):null));}catch(OperationCanceledException){}
        Check.Require(await Retry(db) && pc.Items[v.Path].Bytes.SequenceEqual(new byte[]{3}) && db.Items[v.Path].Bytes.SequenceEqual(new byte[]{3}) && store.Records.Values.Single().DropboxId==db.Items[v.Path].Entry.Id && db.Requests[nameof(ISide.PublishAsync)]==1,"Unknown Dropbox publication stranded or resent an equal version");
    }
    private static async Task KeptTreeAsync()
    {
        var pair=BoundPair() with {Exclusions=[new(Side.Local,"p",false)]};using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"p",0,"old",true);var child=ItemChecks.File(Side.Local,"p/quiet/file",4,"child");var empty=ItemChecks.File(Side.Local,"p/empty",0,"empty",true);
        pc.History.Add((root,[]));pc.History.Add((child,[4]));async IAsyncEnumerable<Listed> Contents(){await Task.CompletedTask;yield return new(empty.Path,empty);yield return new(child.Path,child);}
        pc.Recoverable[(root.Id,root.Revision,root.ContentHash)]=new Recovery.Folder(root,Contents());var c=new Change(Side.Local,ChangeKind.Changed,root.Path,null,root.Id,root,false,false);await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,root.Path,new([new(Side.Local,root.Path,root.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
        bool delegated=false;
        var ok=await FolderDelete.RetryHeldAsync(pair,new("p","p"),store.OpenNotices.Single(),pc,db,store,_=>[c],(range,side,verified,ct)=>{delegated=range==new Scope("p","p") && side==Side.Local && verified.Root==root;return Task.CompletedTask;},default);
        Check.Require(ok && delegated && pc.Items.Count==0 && db.Items.Count==0,"Kept-only recovery wrote outside the add-only population owner");
    }

    private static async Task ConflictAsync()
    {
        foreach(var outcome in new[]{EffectOutcome.Applied,EffectOutcome.Refused,EffectOutcome.Pending}){
            var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Dropbox,"p",0,"root",true);var v=ItemChecks.File(Side.Dropbox,"p/f",2,"child");db.Seed(root,[]);db.Seed(v,[2]);bool called=false,ack=false;bool applied=outcome==EffectOutcome.Applied;
            var c=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false){ConsumeHeld=()=>ack=true};
            var res=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,null,Side.Dropbox,ActionKind.ReplaceKind,pc,db,store,_=>[c],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,resolveConflict:(scope,side,version,ct)=>{called=scope.DropboxPath==v.Path && side==Side.Dropbox && version==v && db.Items.ContainsKey(v.Path);return Task.FromResult(outcome);});
            Check.Require(called && ack==applied && (res.Outcome!=RemovalOutcome.Removed && db.Items.ContainsKey(v.Path) && db.History.Count==0) && pc.Items.Count==0,"Replacement discarded conflict outcome or restored over the replacement");}
    }
    private static async Task CoveredAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var row=new Row(saved.LocalKey,root,null,saved,[],null,RowContext.Neutral with {CompleteInventory=true,ProtectedContents=false,Destination="p"});
        async IAsyncEnumerable<Listed> Large(){await Task.CompletedTask;for(int i=0;i<960000;i++)yield return new("p/"+i,root with {Path="p/"+i,IsFolder=false});}
        var source=Observe.On(pc,(op,args)=>op==nameof(ISide.ReadAllAsync)?throw new InvalidOperationException("Reopened covered 960000-item listing: "+Large().GetType().Name):null);
        var res=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,source,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,covered:row);
        Check.Require(res.Outcome==RemovalOutcome.Removed,"Covered folder did not settle");
    }

    private static async Task ReceiptCoverageAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var covered=new Row(saved.LocalKey,root,null,saved,[],null,RowContext.Neutral with {CompleteInventory=true,ProtectedContents=false,Destination="p"});
        int visits=0;bool removed=false,proved=false;var mark=new JournalPosition(7,42);
        async IAsyncEnumerable<Listed> Receipt(){await Task.CompletedTask;for(int i=0;i<960000;i++){visits++;if(visits>1)throw new InvalidOperationException("Repeated the covered receipt walk");yield return new("p/old",null);}}
        async Task<RemovalResult> Remove(object?[] args){var result=await pc.DeleteAsync(root,(RemovalSelection?)args[1],default);removed=true;return result with {Recycled=Receipt()};}
        var a=Observe.On(pc,(op,args)=>op==nameof(ISide.DeleteAsync)?Remove(args):op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>((null,removed?mark:null)):null);
        var b=Observe.On(db,(op,args)=>op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>((removed?"after":"before",null)):null);
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,a,b,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,covered:covered,coversThrough:fence=>proved=removed && fence.Local==mark && fence.Dropbox=="after");
        Check.Require(proved && visits==0 && result.Outcome==RemovalOutcome.Removed,"Used stale coverage or enumerated proven unchanged receipt");
    }
    private static async Task ConflictingLiveAsync()
    {
        var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var original=ItemChecks.File(Side.Dropbox,"p/f",1,"old");pc.Seed(ItemChecks.File(Side.Local,original.Path,2,"pc"),[2]);db.Seed(ItemChecks.File(Side.Dropbox,original.Path,3,"db"),[3]);db.Recoverable[(original.Id,original.Revision,original.ContentHash)]=new Recovery.File(original,new MemoryStream([1]));
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,original.Path,new([new(Side.Dropbox,original.Path,original.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);
        bool called=false,ack=false;var c=new Change(Side.Dropbox,ChangeKind.Changed,original.Path,null,original.Id,original,false,false){ConsumeHeld=()=>ack=true};
        var ok=await FolderDelete.RetryHeldAsync(pair,new("p","p"),store.OpenNotices.Single(),pc,db,store,_=>[c],(_,_,_,_)=>Task.CompletedTask,default,(scope,side,v,ct)=>{called=v==original && side==Side.Dropbox && scope.LocalPath==original.Path;return Task.FromResult(EffectOutcome.Pending);});
        Check.Require(called && !ok && !ack && pc.Items[original.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[original.Path].Bytes.SequenceEqual(new byte[]{3}) && store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack),"Both occupied paths bypassed ordinary conflict recovery or discharged pending source");
    }

    private static async Task SubstituteAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var selected=ItemChecks.File(Side.Local,"p",0,"original",true);pc.Seed(selected,[]);var saved=SyncedEntries.From(selected,selected with {Id="peer"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var sub=ItemChecks.File(Side.Local,"p",7,"substitute");var receipt=new IdentityLocation("volume","native-receipt",sub);bool seen=false;
        var side=Observe.On(pc,(op,args)=>{
            if(op==nameof(ISide.DeleteAsync)){pc.Items.Clear();return Task.FromResult(new RemovalResult(RemovalOutcome.Retained,null,TextKey.RecoveryWrongRestored){RecycledVictim=receipt});}
            if(op==nameof(ISide.RecoverAsync)){seen=Equals(args[0],receipt);return Task.FromResult<Recovery?>(seen?new Recovery.File(sub,new MemoryStream([7])):null);}return null;});
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),selected,saved,Side.Local,ActionKind.ReplaceKind,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(seen && result.Outcome!=RemovalOutcome.Removed && pc.Items["p"].Bytes.SequenceEqual(new byte[]{7}) && store.Records.Values.Single()==saved,"Verified substitute receipt was lost or the original binding was retired");
    }
    private static async Task QuietAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);int pre=0,post=0;
        async IAsyncEnumerable<Listed> Receipt(){await Task.CompletedTask;post++;throw new InvalidOperationException("Read all recycled content");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        async Task<RemovalResult> Remove(object?[] a)=>(await pc.DeleteAsync(root,(RemovalSelection?)a[1],default)) with {Recycled=Receipt()};
        var side=Observe.On(pc,(op,args)=>{if(op==nameof(ISide.ReadAllAsync))pre++;return op==nameof(ISide.DeleteAsync)?Remove(args):null;});
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,null,Side.Local,ActionKind.DeleteLocal,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(pre==0 && post==0,"Quiet delete repeated an unneeded subtree acquisition");
    }
}
