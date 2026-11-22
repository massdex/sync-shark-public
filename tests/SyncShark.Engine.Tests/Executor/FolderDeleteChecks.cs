using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
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
        foreach(var test in new Func<Task>[]{MovedOutEvidenceAsync,LocatedFileRetryAsync,ExcludedPhysicalAsync,IgnoredPhysicalAsync,NestedPhysicalAsync,AncestorAsync,ArmedSiblingsAsync,ImmediateBoundaryAsync,RecoveryAsync,PartialAsync,OutsideAsync,TombstoneAsync,ForeignTombstoneAsync,ExcludedAsync,UnreadableAsync,CostAsync,NamespaceAsync,MappedAsync,FolderRetryAsync,ReceiptOrderAsync,LateMergeAsync,CrashAfterFirstAsync,LostReplyAsync,NewerAsync,KeptTreeAsync,CoveredAsync,ConflictAsync,ReceiptCoverageAsync,ConflictingLiveAsync,SubstituteAsync,StrandedAsync,SubFolderAsync,QuietAsync})
        {
            try {await test();} catch(Exception e) {failures.Add(test.Method.Name+": "+e.Message);}
        }
        Check.Require(failures.Count==0,string.Join("; ",failures));
    }
    private static async Task AncestorAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var cloud=new FakeSide(Side.Dropbox);
        var parent=ItemChecks.File(Side.Dropbox,"p",0,"parent",true);var child=parent with {Path="p/c",Id="child"};
        var a=SyncedEntries.From(parent with {Id="pc-p"},parent);var b=SyncedEntries.From(child with {Id="pc-c"},child);await store.CommitAsync(pair.PairId,null,a,null,null,default);await store.CommitAsync(pair.PairId,null,b,null,null,default);
        var source=Observe.On(cloud,(op,args)=>op==nameof(ISide.ReadByIdAsync) && (string?)args[0]==parent.Id?Task.FromResult<(IdentityLocation,bool)?>((new("ns","outside",parent with {Skip=nameof(TextKey.SkipUnreadable)}),false)):null);
        var paths=new PathScope(pair,[pair],DriveType.Fixed);Check.Require(await FolderDelete.HighestMissingAsync(pair,paths,b,source,store,default)==b && await FolderDelete.HighestMissingAsync(pair,paths,b,cloud,store,default)==a && cloud.Mutations.Count==0,"A skipped or out-of-scope ancestor widened the selected footprint");
    }
    private static Task ExcludedPhysicalAsync()=>PhysicalFolderAsync(excluded:true);
    private static Task IgnoredPhysicalAsync()=>PhysicalFolderAsync(ignored:true);
    private static Task NestedPhysicalAsync()=>PhysicalFolderAsync(nested:true);
    private static async Task PhysicalFolderAsync(bool excluded=false,bool ignored=false,bool nested=false)
    {
        const string root="p", childPath="p/child";
        var pair=ItemChecks.Pair() with {Exclusions=excluded?[new(Side.Local,childPath,true)]:[],IgnoreRules=ignored?["FileName:child"]:[]};
        var inner=pair with {PairId="inner",LocalRoot=Path.Combine(pair.LocalRoot,"p","child"),DropboxRoot=pair.DropboxRoot+"/p/child"};
        var paths=new PathScope(pair,nested?[pair,inner]:[pair],DriveType.Fixed);string name=excluded?"Excluded":ignored?"Ignored":"Nested";
        foreach(bool available in new[]{false,true})
        {
            using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
            var parent=ItemChecks.File(Side.Local,root,0,"parent",true);var saved=SyncedEntries.From(parent,parent with {Id="remote"});pc.Seed(parent,[]);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
            var child=ItemChecks.File(Side.Local,childPath,1,"child",nested);pc.Seed(child,nested?[]:[1]);
            if(nested)await store.CommitAsync(pair.PairId,null,SyncedEntries.From(child,child with {Id="remote-child"}),null,null,default);
            if(!ignored)pc.Seed(ItemChecks.File(Side.Local,"p/free",2,"free"),[2]);
            var mark=new JournalPosition(1,42);pc.Pages.Enqueue(new([],null,mark));db.Pages.Enqueue(new([],"end",null));
            var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,parent,saved,Side.Local,ActionKind.DeleteLocal,pc,db,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>Task.CompletedTask,default,coversThrough:available?f=>f.Local==mark && f.Dropbox=="end":null);
            if(!available){Check.Require(result.Outcome==RemovalOutcome.Retained && pc.Mutations.Count==0 && pc.History.Count==0 && pc.Items.ContainsKey(root) && pc.Items.ContainsKey(childPath),name+" tree mutated before acquisition owner was available");continue;}
            Check.Require(db.Items.Count==0 && store.OpenNotices.Count==0,name+" physical removal touched peer or raised an unexpected notice");
            if(ignored)Check.Require(result.Outcome==RemovalOutcome.Removed && pc.Items.Count==0 && pc.History.Any(x=>x.Entry.Id==child.Id && x.Bytes.SequenceEqual(new byte[]{1})),"Ignored bytes did not accompany the recycled folder");
            else Check.Require(result.Outcome==RemovalOutcome.Retained && pc.Items.Count==2 && pc.Items[root].Entry==parent && pc.Items[childPath].Entry==child && !pc.Items.ContainsKey("p/free") && pc.History.Single() is var moved && moved.Entry.Id=="free" && moved.Bytes.SequenceEqual(new byte[]{2}),name+" boundary lost the survivor or failed to recycle only the free sibling");
        }
    }
    private static async Task ArmedSiblingsAsync()
    {
        var pair=ItemChecks.Pair() with {Exclusions=[new(Side.Local,"p/kept",true)]};using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);foreach(var name in new[]{"a","b","kept"})pc.Seed(ItemChecks.File(Side.Local,"p/"+name,1,name),[1]);
        async Task<RemovalResult> Remove(object?[] args){var victim=(RemovalSelection)args[1]!; Check.Require(await store.ArmedAsync(pair.PairId,default)==null,"Second sibling blocked by first successful recycle note");await store.ArmAsync(pair.PairId,victim,null,default);return await pc.DeleteAsync((Entry)args[0]!,victim,default);}
        var side=Observe.On(pc,(op,args)=>op==nameof(ISide.DeleteAsync)?Remove(args):null);
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,null,Side.Local,ActionKind.DeleteLocal,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require(pc.Mutations.SequenceEqual(new[]{"a","b"}) && pc.Items.ContainsKey("p/kept") && await store.ArmedAsync(pair.PairId,default)==null,"Successful sibling removal did not settle its exact note");
    }
    private static async Task ImmediateBoundaryAsync()
    {
        foreach(bool hasFirst in new[]{false,true}){
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Dropbox,"p",0,"folder",true);db.Seed(root,[]);var saved=SyncedEntries.From(root with {Id="pc"},root);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var v=ItemChecks.File(Side.Dropbox,"p/late",1,"child");db.Seed(v,[1]);db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([1]));bool ack=false,compared=false;
        var edit=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false){ConsumeHeld=()=>ack=true};var cloud=Observe.On(DeleteSide(db),(op,_)=>op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>(("after",null)):null);
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Dropbox,ActionKind.DeleteDropbox,pc,cloud,store,_=>hasFirst?[edit]:[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>{compared=true;return Task.CompletedTask;},default,coversThrough:_=>false);
        Check.Require(!ack && !compared && result.Outcome!=RemovalOutcome.Removed && pc.Items.Count==0,"Immediate removal settled before reading through its post-effect boundary");}
    }
    internal static Func<(JournalPosition? Local,string? Dropbox),bool> Admitted(FakeSide pc,FakeSide db)
    {
        var end=new JournalPosition(1,42);pc.Pages.Enqueue(new([],null,end));db.Pages.Enqueue(new([],"complete",null));return fence=>fence.Local==end && fence.Dropbox=="complete";
    }
    internal static ISide DeleteSide(FakeSide cloud)=>Observe.On(cloud,(name,args)=>name==nameof(ISide.DeleteAsync) && args.Length==4?cloud.DeleteAsync((Entry)args[0]!,null,(CancellationToken)args[3]!):null);
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
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,b,binding,Side.Dropbox,ActionKind.DeleteDropbox,pc,DeleteSide(db),store,_=>reports,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,coversThrough:Admitted(pc,db));
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
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
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
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),selected,null,Side.Dropbox,ActionKind.DeleteDropbox,pc,DeleteSide(db),store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
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
        pc.Recoverable[(last.Id,last.Revision,last.ContentHash)]=new Recovery.File(last,new MemoryStream([2]));
        var local=Observe.On(pc,(op,a)=>op==nameof(ISide.DeleteAsync)?Remove():op==nameof(ISide.RecoverAsync) && a[1] is Entry {ContentHash:""} candidate && candidate.Id==last.Id?Task.FromResult<Recovery?>(new Recovery.File(last,new MemoryStream([2]))):!unbound && op==nameof(ISide.ReadByIdAsync) && (string?)a[0]==last.Id && pc.History.Count!=0?Task.FromResult<(IdentityLocation,bool)?>( (new("volume","bin",last with {ContentHash=""}),false)):null);bool ack=false;
        var stale=new Change(Side.Local,ChangeKind.Changed,first.Path,null,first.Id,first,true,false){ConsumeHeld=()=>ack=true};
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),folder,null,Side.Local,unbound?ActionKind.MoveLocal:ActionKind.DeleteLocal,local,db,store,_=>unbound?[]:[stale],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require((ack||unbound) && pc.Items.TryGetValue(last.Path,out var localCopy) && localCopy.Bytes.SequenceEqual(new byte[]{2}) && db.Items.TryGetValue(last.Path,out var remoteCopy) && remoteCopy.Bytes.SequenceEqual(new byte[]{2}),"Verified newer recycled edit or unbound late merge child was lost");
    }

    private static async Task CrashAfterFirstAsync()
    {
        var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var folder=ItemChecks.File(Side.Dropbox,"p",0,"parent",true);db.Seed(folder,[]);
        var v=ItemChecks.File(Side.Dropbox,"p/item",3,"original");db.Seed(v,[3]);db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([3]));
        var c=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);var stop=new OperationCanceledException();
        async Task<RemovalResult> Crash(object?[] a){await pc.PublishAsync((IStaged)a[0]!,null,null,default);throw stop;}
        var broken=Observe.On(pc,(op,args)=>op==nameof(ISide.PublishAsync)?Crash(args):null);
        try{await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),folder,null,Side.Dropbox,ActionKind.DeleteDropbox,broken,DeleteSide(db),store,_=>[c],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));}catch(OperationCanceledException ex) when(ex==stop){}
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
        foreach(var outcome in new EffectOutcome?[]{EffectOutcome.Applied,EffectOutcome.Refused,EffectOutcome.Pending,null}){
            var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Dropbox,"p",0,"root",true);var v=ItemChecks.File(Side.Dropbox,"p/f",2,"child");db.Seed(root,[]);db.Seed(v,[2]);bool called=false,ack=false;bool applied=outcome==EffectOutcome.Applied;
            var c=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false){ConsumeHeld=()=>ack=true};
            var res=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,null,Side.Dropbox,ActionKind.ReplaceKind,pc,db,store,_=>[c],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,resolveConflict:(scope,side,version,ct)=>{called=scope.DropboxPath==v.Path && side==Side.Dropbox && version==v && db.Items.ContainsKey(v.Path);if(outcome==null)throw new ItemFailedException(v.Path,TextKey.FailChanged,null);return Task.FromResult(outcome.Value);});
            Check.Require(applied || store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Subject.Items.Any(i=>i.Id==v.Id && i.Path==v.Path)),"Unsettled replacement handoff left no exact child PutBack");
            Check.Require(called && ack==applied && (res.Outcome!=RemovalOutcome.Removed && db.Items.ContainsKey(v.Path) && db.History.Count==0) && pc.Items.Count==0,"Replacement discarded conflict outcome or restored over the replacement");}
    }
    private static async Task CoveredAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var row=new Row(saved.LocalKey,root,null,saved,[],null,RowContext.Neutral with {CompleteInventory=true,ProtectedContents=false,Destination="p"});
        async IAsyncEnumerable<Listed> Large(){await Task.CompletedTask;for(int i=0;i<960000;i++)yield return new("p/"+i,root with {Path="p/"+i,IsFolder=false});}
        var source=Observe.On(pc,(op,args)=>op==nameof(ISide.ReadAllAsync)?throw new InvalidOperationException("Reopened covered 960000-item listing: "+Large().GetType().Name):null);
        var res=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,source,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,covered:row,coversThrough:Admitted(pc,db));
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

    /// <summary>Exercise whole-item restore against native identity and bytes; all paths belong to this temporary fixture.</summary>
    public static async Task WholeSubstituteAsync()
    {
        var home=Directory.CreateTempSubdirectory();var drive=Enumerable.Range('Q',10).Reverse().Select(n=>((char)n)+":").First(d=>!Directory.Exists(d+Path.DirectorySeparatorChar));bool mounted=false;
        async Task Mount(string target){var start=new System.Diagnostics.ProcessStartInfo("subst.exe"){UseShellExecute=false};start.ArgumentList.Add(drive);start.ArgumentList.Add(target);using var process=System.Diagnostics.Process.Start(start)!;await process.WaitForExitAsync();Check.Require(process.ExitCode==0,"Temporary test volume mapping failed");}
        try
        {
            await Mount(home.FullName);mounted=true;var volumeRoot=drive+Path.DirectorySeparatorChar;var root=Directory.CreateDirectory(Path.Combine(volumeRoot,"root")).FullName;var bin=Directory.CreateDirectory(Path.Combine(volumeRoot,ResourceNames.RootBinFolder)).FullName;
            var (volume,driveType)=DriveIdentity.Read(root);var pair=BoundPair();pair=pair with {LocalRoot=root,Roots=pair.Roots! with {Local=volume,LocalRoot=root}};
            using var store=new InMemoryStateStore();var local=LocalSide.Factory()(pair,()=>new(pair,[pair],driveType),ItemChecks.Settings,store);var cloud=new FakeSide(Side.Dropbox);
            foreach(bool folder in new[]{true,false})
            {
                var path=folder?"tree":"item";var original=Path.Combine(root,path);var receipt=Path.Combine(bin,path);
                if(folder){Directory.CreateDirectory(original);await File.WriteAllBytesAsync(Path.Combine(original,"quiet"),[7]);}else await File.WriteAllBytesAsync(original,[7]);
                var version=(await local.ReadOneAsync(path,default))!;if(!folder)version=version with {ContentHash=await local.ContentHashAsync(version,default)};
                if(folder) Directory.Move(original,receipt);else File.Move(original,receipt);
                var selected=SyncedEntries.From(version with {Id="selected"},version with {Id="peer"});await store.CommitAsync(pair.PairId,null,selected,null,null,default);
                if(!folder)await File.WriteAllBytesAsync(original,[9]);
                var locator=new IdentityLocation(volume.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),receipt,version);
                var endpoint=Observe.On(local,(op,args)=>op==nameof(ISide.ReadByIdAsync) && (string?)args[0]==version.Id?Task.FromResult<(IdentityLocation,bool)?>((locator,false)):null);
                bool restored=await FolderDelete.PutBackSubstituteAsync(pair,new(path,path),locator,endpoint,cloud,store,(_,_,_,_)=>throw new InvalidOperationException("Wrong population owner"),default);
                if(!folder){Check.Require(!restored && File.Exists(receipt) && File.ReadAllBytes(original).SequenceEqual(new byte[]{9}),"Occupied name changed or binned version released");File.Move(original,Path.Combine(home.FullName,"successor"));restored=await FolderDelete.RetryHeldAsync(pair,new(path,path),store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack),endpoint,cloud,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),default);}
                Check.Require(restored && (await local.ReadOneAsync(path,default))?.Id==version.Id && File.ReadAllBytes(folder?Path.Combine(original,"quiet"):original).SequenceEqual(new byte[]{7}) && !File.Exists(receipt) && !Directory.Exists(receipt) && cloud.Items.Count==0 && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(path),default)==selected,"Whole-item restore lost identity, contents or selected binding");
            }
        }
        finally {if(mounted)await Mount("/D");home.Delete(true);}
    }
    private static async Task MovedOutEvidenceAsync()
    {
        foreach(bool affectedOnly in new[]{false,true}){
        var pair=ItemChecks.Pair();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var folder=ItemChecks.File(Side.Local,"old",0,"root",true);pc.Seed(folder,[]);var saved=SyncedEntries.From(folder,folder with {Id="peer"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        bool ack=false,compared=false;var move=new Change(Side.Local,ChangeKind.Renamed,"elsewhere",affectedOnly?null:"old/child","child",null,false,false){AffectedPath=affectedOnly?"old/child":null,ConsumeHeld=()=>ack=true};
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),folder,saved,Side.Local,ActionKind.DeleteLocal,pc,db,store,_=>[move],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>{compared=true;return Task.CompletedTask;},default,coversThrough:Admitted(pc,db));
        Check.Require(result.Outcome==RemovalOutcome.Retained && !ack && !compared && store.Records.Values.Single()==saved,"Held move out of the removal scope was treated as settled");}
    }
    private static async Task LocatedFileRetryAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        var file=ItemChecks.File(Side.Local,"p",7,"substitute");var selected=SyncedEntries.From(file with {Id="selected"},file with {Id="peer"});await store.CommitAsync(pair.PairId,null,selected,null,null,default);
        var locator=new IdentityLocation("volume","receipt",file);var report=new Change(Side.Local,ChangeKind.Changed,file.Path,null,file.Id,file,true,false);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.ClashNameTaken,file.Path,new([new(Side.Local,file.Path,file.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
        using var proof=new MemoryStream([7]);int reads=0,restores=0;
        var local=Observe.On(pc,(op,args)=>{
            if(op==nameof(ISide.ReadByIdAsync) && Equals(args[0],file.Id))return Task.FromResult<(IdentityLocation,bool)?>((locator,false));
            if(op==nameof(ISide.RecoverAsync)){reads++;return Task.FromResult<Recovery?>(new Recovery.File(file,proof));}
            if(op==nameof(ISide.RestoreRecycledAsync)){Check.Require(!proof.CanRead,"Located-file Retry still held the proof stream at rename");Check.Require(Equals(args[0],locator) && Equals(args[1],file),"Rename did not receive the verified version");restores++;pc.Seed(file,[7]);return Task.FromResult(new RemovalResult(RemovalOutcome.Published,file,null));}return null;});
        var ok=await FolderDelete.RetryHeldAsync(pair,new(file.Path,file.Path),store.OpenNotices.Single(),local,db,store,_=>[report],(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(ok && reads==1 && restores==1 && pc.Items[file.Path].Bytes.SequenceEqual(new byte[]{7}) && store.OpenNotices.Count==0 && store.Records.Values.Single()==selected,"Located-file Retry failed to release proof before its single exact restore");
    }
    private static Task SubstituteAsync()=>Substitute(false);
    private static Task StrandedAsync()=>Substitute(true);
    private static Task SubFolderAsync()=>Substitute(false,true);
    private static async Task Substitute(bool occupied,bool folder=false)
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var selected=ItemChecks.File(Side.Local,"p",0,"original",true);pc.Seed(selected,[]);var saved=SyncedEntries.From(selected,selected with {Id="peer"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var sub=ItemChecks.File(Side.Local,"p",7,"substitute",folder);var receipt=new IdentityLocation("volume","native-receipt",sub);bool seen=false;
        var side=Observe.On(pc,(op,args)=>{
            if(op==nameof(ISide.DeleteAsync)){pc.Items.Clear();if(occupied)pc.Seed(selected,[]);return Task.FromResult(new RemovalResult(RemovalOutcome.Retained,null,TextKey.RecoveryWrongRestored){RecycledVictim=receipt});}
            if(op==nameof(ISide.ReadByIdAsync) && (string?)args[0]==selected.Id && pc.Items.ContainsKey("p"))return Task.FromResult<(IdentityLocation,bool)?>((new("volume","p",selected),true));
            if(op==nameof(ISide.ReadByIdAsync) && (string?)args[0]==sub.Id)return Task.FromResult<(IdentityLocation,bool)?>((receipt,false));
            if(op==nameof(ISide.RestoreRecycledAsync)){seen=Equals(args[0],receipt) && Equals(args[1],sub) && (string?)args[2]==sub.Path && !pc.Items.ContainsKey(sub.Path);if(!seen)throw new InvalidOperationException("Invalid physical restore request");pc.Seed(sub,folder?[]:[7]);return Task.FromResult(new RemovalResult(RemovalOutcome.Published,sub,null));}return null;});
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),selected,saved,Side.Local,ActionKind.ReplaceKind,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require(result.Outcome!=RemovalOutcome.Removed && (occupied?pc.Items["p"].Entry==selected && store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Subject.Items.Any(i=>i.Id==sub.Id)):seen && pc.Items.TryGetValue("p",out var at) && (folder?at.Entry.IsFolder:at.Bytes.SequenceEqual(new byte[]{7})) && db.Items.Count==0) && store.Records.Values.Single()==saved,folder?"Identified substitute folder was not restored at the free original name":"Verified substitute receipt was lost or the original binding was retired");
        if(occupied){pc.Items.Clear();var ok=await FolderDelete.RetryHeldAsync(pair,new("p","p"),store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack),side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,default);Check.Require(ok && pc.Items["p"].Bytes.SequenceEqual(new byte[]{7}) && db.Items.Count==0 && store.Records.Values.Single()==saved,"Exact substitute Retry lost its bin subject without a queued change");}
    }
    private static async Task QuietAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);var child=ItemChecks.File(Side.Local,"p/child",3,"child");pc.Seed(child,[3]);pc.ReadFailures[child.Path]=new ItemFailedException(child.Path,TextKey.ReadLocalFailed,null);int pre=0,post=0;
        async IAsyncEnumerable<Listed> Receipt(){await Task.CompletedTask;post++;throw new InvalidOperationException("Read all recycled content");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        async Task<RemovalResult> Remove(object?[] a)=>(await pc.DeleteAsync(root,(RemovalSelection?)a[1],default)) with {Recycled=Receipt()};
        var side=Observe.On(pc,(op,args)=>{if(op==nameof(ISide.ReadAllAsync))pre++;return op==nameof(ISide.DeleteAsync)?Remove(args):null;});
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,null,Side.Local,ActionKind.DeleteLocal,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require(pre==1 && post==0 && result.Outcome==RemovalOutcome.Retained && pc.History.Count==0 && pc.Items[child.Path].Bytes.SequenceEqual(new byte[]{3}),"Unbound unreadable child recycled without eligibility proof");
    }
}
