using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Host startup and read-only commands never create an absent store.</summary>
public static class HostChecks
{
    public static async Task RunAsync()
    {
        var opens = new List<bool>();
        var host = new Host((create,_) => {opens.Add(create);return Task.FromResult<IStore?>(null);},
            (_,_,_,_)=>throw new InvalidOperationException(),ItemChecks.Settings,null!,TimeProvider.System,
            _=>Task.FromResult("connection"),(_,_)=>Task.CompletedTask);
        await host.StartAsync(default);
        await foreach(var _ in host.PairsAsync(default)) throw new InvalidDataException("Invented pair");
        Check.Require(opens.Count>0 && !opens.Contains(true),"Read-only command requested store creation");
        await host.PauseAsync(null,default); await host.ResumeAsync("pair",default);
        Check.Require(host.IsPaused("pair"),"Pair resume undid global pause");
        await host.PauseAsync("other",default); await host.ResumeAsync(null,default);
        Check.Require(!host.IsPaused("pair") && host.IsPaused("other"),"Global resume lost a pair pause");
        var before=opens.Count;
        try { await host.SetExclusionAsync("pair",new(Side.Local,"../outside",false),false,true,default); throw new InvalidDataException("Accepted an escaping exclusion"); }
        catch(EngineRequestException e) when(e.Key==TextKey.ExclusionPathInvalid) { }
        Check.Require(before==opens.Count,"Invalid exclusion reached storage");
        host.SetStartupMessage("switch refused",true);
        await host.StartAsync(default); Check.Require(await host.StatusAsync(null,default)=="switch refused" && before==opens.Count,"Switch refusal opened the store");
        var stop=host.Stopping; await host.StopAsync(default); Check.Require(stop.IsCancellationRequested,"Host did not notify the pipe owner of stop");
        var errors=new List<string>();
        foreach(var check in new Func<Task>[]{IndependentBind,MemberRewind,ForeignCancellation,DisconnectedAdapter,ExplicitBind,PendingOuterScope,RemovingMember,ReleaseFailure,FailedFirstRead})
            try { await check(); } catch(Exception e) {errors.Add($"{check.Method.Name}: {e}");}
        Check.Require(errors.Count==0,string.Join("; ",errors));
    }

    // Run at integration with the real planner, job checker and ordinary retry. No emulated callback qualifies as a pass.
    public static async Task RetryAtHostAsync()
    {
        using var store=new InMemoryStateStore();var p=Bound("selected",@"T:\Selected",1);await store.SavePairAsync(p,default);
        var pc=new FakeSide(Side.Local){Root=(p.Roots!.Local,null,null,DriveType.Fixed)};
        var cloud=new FakeSide(Side.Dropbox){Root=(null,p.Roots.Dropbox,null,null)};
        var entered=Signal();var release=Signal();
        var proxy=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var probe=(JournalProbe)(object)proxy;
        probe.Local=cloud;probe.Job=async ct=>{entered.TrySetResult();await release.Task.WaitAsync(ct);return new JobReply.Running();};
        var parent=ItemChecks.File(Side.Local,"local-folder",0,"local-folder-id",true);
        var remote=ItemChecks.File(Side.Dropbox,"cloud-folder",0,"cloud-folder-id",true);
        await store.CommitAsync(p.PairId,null,SyncedEntries.From(parent,remote),null,null,default);
        var notice=new Notice(1,NoticeKind.Failure,TextKey.FailChanged,"local-folder/item",new([new(Side.Dropbox,"other-leg","other-id"),new(Side.Local,"local-folder/item","item-id")],Side.Local),DateTimeOffset.UnixEpoch,false);
        await store.SaveNoticeAsync(p.PairId,notice,default);await store.SaveJobAsync(new(p.PairId,"files/delete_batch","job",[new("cloud-folder-id","cloud-folder",null)]),default);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_)=>(pc,proxy),ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try
        {
            Notice? selected=null;await foreach(var n in host.NoticesAsync(p.PairId,default)) selected=n;
            var retry=host.ResolveAsync(p.PairId,selected!.Id,selected.Reviewed!,ResolutionChoice.Retry,null,default);
            if(await Task.WhenAny(entered.Task,retry).WaitAsync(TimeSpan.FromSeconds(5))==retry) await retry;
            Check.Require(entered.Task.IsCompleted && !retry.IsCompleted,"Host skipped or failed to await the accepted job check");
            release.TrySetResult();await retry;Check.Require(probe.JobReads==1,"Manual Retry did not check the accepted job exactly once");
            Check.Require(pc.Requests[nameof(ISide.ReadByIdAsync)]==0 && store.OpenNotices.Any(n=>n.Id==selected.Id),"Ordinary Retry ran for a child of a still-running accepted folder job");
            var free=notice with {Path="disjoint",Subject=new([new(Side.Local,"disjoint","disjoint-id")],Side.Local)};
            await store.SaveNoticeAsync(p.PairId,free,default);
            await foreach(var n in host.NoticesAsync(p.PairId,default)) if(n.Path==free.Path) selected=n;
            var reads=pc.Requests[nameof(ISide.ReadByIdAsync)];
            await host.ResolveAsync(p.PairId,selected!.Id,selected.Reviewed!,ResolutionChoice.Retry,null,default);
            Check.Require(pc.Requests[nameof(ISide.ReadByIdAsync)]>reads && probe.JobReads==1,"Disjoint ordinary Retry never reobserved its selected identity");
        }
        finally{release.TrySetResult();await host.StopAsync(default);}
    }
    // The retained feed callback still cannot observe its ID; only exact-version PutBack can restore these bytes.
    public static async Task PutBackAtHostAsync()
    {
        var pair=Bound("putback",@"T:\PutBack",1);using var store=new InMemoryStateStore();
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"putback",ItemChecks.Settings)){}
        var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var pc=(JournalProbe)(object)local;
        pc.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};pc.Quiet=true;
        var remote=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var db=(JournalProbe)(object)remote;
        db.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};db.Quiet=true;
        var v=ItemChecks.File(Side.Dropbox,"item",3,"original");var admitted=Signal();
        db.Feed=async (cursor,token)=>{if(cursor==null)return new([new(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false)],"one",null);
            admitted.TrySetResult();await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();};
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));var ct=stop.Token;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_)=>(local,remote),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try{
            await host.StartAsync(ct);await admitted.Task.WaitAsync(ct);
            Check.Require(await store.CursorAsync(pair.PairId,ct)==null && db.Local.Requests[nameof(ISide.ReadOneAsync)]>0,"Missing feed identity did not leave the page unsettled");
            db.Local.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([3]));
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,v.Path,new([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),ct);
            Notice? selected=null;await foreach(var n in host.NoticesAsync(pair.PairId,ct))if(n.Kind==NoticeKind.PutBack)selected=n;
            await host.ResolveAsync(pair.PairId,selected!.Id,selected.Reviewed!,ResolutionChoice.Retry,null,ct);
            Check.Require(!store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack) && pc.Local.Items.TryGetValue(v.Path,out var a) && a.Bytes.SequenceEqual(new byte[]{3}) && db.Local.Items.TryGetValue(v.Path,out var b) && b.Bytes.SequenceEqual(new byte[]{3}),"Matching held callback swallowed the exact PutBack restore");
        }finally{stop.Cancel();await host.StopAsync(default);}
    }
    // Two failed reads for one account, then the next successful page without restarting the Host.
    public static async Task FailedMemberReadAsync()
    {
        using var store=new InMemoryStateStore();var pair=Bound("retry",@"T:\Retry",1);
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"retry",ItemChecks.Settings)){}
        var ended=Signal();logs.Closed=()=>ended.TrySetResult();var seen=Signal();int tries=0;
        var pc=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var lp=(JournalProbe)(object)pc;lp.Quiet=true;lp.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        var cloud=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var rp=(JournalProbe)(object)cloud;rp.Quiet=true;rp.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        var item=ItemChecks.File(Side.Local,"resumed",3,"item-id");
        lp.Feed=async (_,token)=>{
            switch(Interlocked.Increment(ref tries)){
            case 1: case 2:throw new ItemFailedException(string.Empty,TextKey.SignInReturnRetry,null);
            case 3:lp.Local.Seed(item,[3]);return new([new(Side.Local,ChangeKind.Changed,item.Path,null,item.Id,item,true,false)],null,new(1,20));
            default:await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();}};
        lp.OnSettle=pos=>{if(pos.NextUsn==20)seen.TrySetResult();return pos;};
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_)=>(pc,cloud),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try{
            await host.StartAsync(stop.Token);await Task.WhenAny(seen.Task,ended.Task).WaitAsync(stop.Token);
            Check.Require(seen.Task.IsCompleted && !ended.Task.IsCompleted && rp.Local.Items.TryGetValue(item.Path,out var copy) && copy.Bytes.SequenceEqual(new byte[]{3}) && store.OpenNotices.Count(n=>n.Kind==NoticeKind.Failure)==1 && store.OpenNotices.Any(n=>n.Cause==TextKey.SignInReturnRetry),"Failed member read ended live sync instead of retrying with one sign-in notice");
        }finally{stop.Cancel();await host.StopAsync(default);}
    }
    private static async Task FailedFirstRead()
    {
        using var store=new InMemoryStateStore();var pair=Bound("first",@"T:\First",1);await store.SavePairAsync(pair,default);
        var published=new TaskCompletionSource<(bool Admitted,int Reads,int SignInNotices)>(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;
        var pc=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var probe=(JournalProbe)(object)pc;probe.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
        // Observe the admission decision before the live runner can write another Failure in the same notice slot.
        probe.Published=()=>published.TrySetResult((!probe.Paused,Volatile.Read(ref calls),store.OpenNotices.Count(n=>n.Kind==NoticeKind.Failure && n.Cause==TextKey.SignInReturnRetry)));
        var cloud=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var remote=(JournalProbe)(object)cloud;remote.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        remote.ReadRoot=()=>{if(Interlocked.Increment(ref calls)<3)throw new ItemFailedException(string.Empty,TextKey.SignInReturnRetry,null);};
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_)=>(pc,cloud),ItemChecks.Settings,new MemoryLogs(),TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try{await host.StartAsync(default);var at=await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(at.Admitted,"First-read retry: member was not admitted");
            Check.Require(at.Reads==3,$"First-read retry: expected three root reads, observed {at.Reads}");
            Check.Require(at.SignInNotices==1,$"First-read retry: expected one sign-in notice, observed {at.SignInNotices}");
        }finally{await host.StopAsync(default);}
    }
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PairConfig Bound(string id,string path,ulong serial)
    {
        var p=ItemChecks.Pair() with {PairId=id,LocalRoot=path,DropboxRoot="/"+id};
        return p with {Roots=new(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(serial,2,null),new("account","root","home",id))};
    }
    private sealed class MemoryLogs : ILogFiles
    {
        internal Action? Closed;
        private sealed class Writer(Action closed):StringWriter {protected override void Dispose(bool disposing){if(disposing)closed();base.Dispose(disposing);}}
        public TextWriter Open(string path,int bufferCharacters)=>Closed is {} closed ? new Writer(closed) : new StringWriter();
        public string? Resolve(string folder,string pairId,string runId)=>null;
        public ILogFiles.ReadStatus Read(string path,Action<string,bool> record,int bufferBytes,CancellationToken ct,out bool trailing){trailing=false;return ILogFiles.ReadStatus.NotFound;}
        public void Prune(string folder,TimeSpan olderThan,DateTimeOffset now) { }
    }
    public static async Task TrimAtHostAsync()
    {
        var logs=new MemoryLogs();using(var check=new RunLog(logs,"probe",ItemChecks.Settings)){} // W22 stub => NOT IMPLEMENTED, not an artificial successful startup.
        using var db=new InMemoryStateStore();var pending=Bound("pending",@"T:\Pending",1);var ready=Bound("ready",@"T:\Ready",1);
        var jobPair=Bound("job",@"U:\Job",2);var free=Bound("free",@"V:\Free",3);
        foreach(var p in new[]{pending,ready,jobPair,free}) {await db.SavePairAsync(p,default);await db.FinishRepairAsync(p.PairId,(0,0),"{}",default);}
        await db.SaveJobAsync(new(jobPair.PairId,"files/delete_batch","job",[new("id","item",null)]),default);
        var endpoints=new System.Collections.Concurrent.ConcurrentDictionary<string,(JournalProbe Pc,JournalProbe Cloud)>();var readAtTrim=new System.Collections.Concurrent.ConcurrentDictionary<string,bool>();
        var reservations=new HashSet<string>();var bind=Signal();var started=Signal();var checking=Signal();var observed=Signal();var freeTrim=Signal();var readyTrim=Signal();var jobTrim=Signal();var finishTrim=Signal();CancellationToken trimToken=default;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (pair,_,_,_,ct)=>{
            if(pair.PairId==pending.PairId){started.TrySetResult();await bind.Task.WaitAsync(ct);}
            var pc=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var lp=(JournalProbe)(object)pc;lp.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Removable)};lp.Quiet=true;
            var remote=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var rp=(JournalProbe)(object)remote;rp.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};rp.Quiet=true;
            var pcItem=ItemChecks.File(Side.Local,"local-change",1,"pc-id");var dbItem=ItemChecks.File(Side.Dropbox,"remote-change",1,"db-id");
            lp.Local.Seed(pcItem,[1]);rp.Local.Seed(dbItem,[1]);
            lp.Local.Pages.Enqueue(new([new(Side.Local,ChangeKind.Changed,pcItem.Path,null,pcItem.Id,pcItem,false,false)],null,new(1,4)));
            rp.Local.Pages.Enqueue(new([new(Side.Dropbox,ChangeKind.Changed,dbItem.Path,null,dbItem.Id,dbItem,false,false)],"through",null));
            endpoints[Path.GetPathRoot(pair.LocalRoot)!]=(lp,rp);
            if(pair.PairId==jobPair.PairId)rp.Job=async token=>{checking.TrySetResult();await observed.Task.WaitAsync(token);return new JobReply.Running();};
            return(pc,remote);
        },ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,async (root,_,ct)=>{
            readAtTrim[root]=endpoints.TryGetValue(root,out var ends) && ends.Pc.Local.Requests[nameof(ISide.ReadChangesAsync)]>0 && ends.Cloud.Local.Requests[nameof(ISide.ReadChangesAsync)]>0;
            if(root==Path.GetPathRoot(free.LocalRoot))freeTrim.TrySetResult();
            if(root==Path.GetPathRoot(ready.LocalRoot)){trimToken=ct;readyTrim.TrySetResult();await finishTrim.Task.WaitAsync(ct);}
            if(root==Path.GetPathRoot(jobPair.LocalRoot))jobTrim.TrySetResult();
        },p=>reservations.Add(p.PairId),(p,ct)=>{ct.ThrowIfCancellationRequested();reservations.Remove(p.PairId);return Task.CompletedTask;});
        try{
            await host.StartAsync(default);await Task.WhenAll(started.Task,checking.Task,freeTrim.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(!jobTrim.Task.IsCompleted && !readyTrim.Task.IsCompleted,"Trim preceded recovery or a pending first bind");
            Check.Require(await host.RemovePairAsync(pending.PairId,default)==null,"Pending member not removed");
            Check.Require(!reservations.Contains(pending.PairId) && reservations.Contains(ready.PairId),"Removing an unopened member did not retire just its reservation");
            await readyTrim.Task.WaitAsync(TimeSpan.FromSeconds(5));await host.PauseAsync(ready.PairId,default).WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(!trimToken.IsCancellationRequested,"Pair pause canceled volume trim");finishTrim.TrySetResult();
            observed.TrySetResult();await jobTrim.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(new[]{free,ready,jobPair}.All(p=>readAtTrim.GetValueOrDefault(Path.GetPathRoot(p.LocalRoot)!)),"Trim ran without actually reading both finite sources");
        }finally{bind.TrySetResult();observed.TrySetResult();finishTrim.TrySetResult();await host.StopAsync(default);}
    }
    // Hold the store lookup for the parent's publication, after the child has drained.
    public class PublicationStore : System.Reflection.DispatchProxy
    {
        internal InMemoryStateStore Store=null!;
        internal Func<Task>? OnFind,OnSave;
        internal Action<string>? Lookup;
        internal Action? Finished;
        internal Func<Task>? BeforeFinish;
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method!.Name==nameof(IStore.FindAsync)){Lookup?.Invoke((string)args![2]!);if(OnFind!=null)return Find();}
            if(method.Name==nameof(IStore.SavePairAsync) && OnSave!=null) return Save();
            if(method.Name==nameof(IStore.FinishRepairAsync) && Finished!=null) return Finish();
            return method.Invoke(Store,args);
            async Task<bool> Finish(){if(BeforeFinish!=null)await BeforeFinish();var ok=await (Task<bool>)method!.Invoke(Store,args)!;if(ok)Finished!();return ok;}
            async Task Save(){await OnSave!();await (Task)method!.Invoke(Store,args)!;}
            async Task<SyncedEntry?> Find(){await OnFind!();return await Store.FindAsync((string)args![0]!, (Side)args[1]!, (string)args[2]!, (CancellationToken)args[3]!);}
        }
    }
    // Exercise both completion orders; an ordinary later record is not a reset or an acknowledgement.
    public static async Task RepairReplaysJournalAsync()
    {
        foreach(var late in new[]{false,true}) await Run(late);
        static async Task Run(bool late)
        {
            using var store=new InMemoryStateStore();var pair=Bound("journal",@"T:\Journal",1);
            await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
            var ledger=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var tap=(PublicationStore)(object)ledger;tap.Store=store;
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));var ct=stop.Token;
            var waiting=Signal();var finished=Signal();if(late)tap.BeforeFinish=()=>waiting.Task.WaitAsync(ct);tap.Finished=()=>finished.TrySetResult();
            var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var pc=(JournalProbe)(object)local;pc.Quiet=true;pc.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};
            var remote=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var db=(JournalProbe)(object)remote;db.Quiet=true;db.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
            var cloudReading=Signal();var acknowledged=Signal();var version=ItemChecks.File(Side.Local,"item",3,"original");int reads=0;
            db.Feed=async (_,token)=>{cloudReading.TrySetResult();try{await Task.Delay(Timeout.Infinite,token);}finally{if(!late)await finished.Task.WaitAsync(ct);}throw new InvalidOperationException();};
            pc.OnSettle=pos=>{
                if(pos.NextUsn==32){if(db.Local.Items.TryGetValue(version.Path,out var file) && file.Bytes.SequenceEqual(new byte[]{3}))acknowledged.TrySetResult();
                    else acknowledged.TrySetException(new InvalidDataException("Journal acknowledgement preceded the replayed effect"));}
                return pc.ReadAhead==30 && pos.NextUsn>11 ? new(pos.JournalId,11) : pos;};
            pc.Feed=async (_,token)=>{
                switch(Interlocked.Increment(ref reads)){
                case 1: await cloudReading.Task.WaitAsync(token);return new([],null,new(1,11));
                case 2: pc.Settled=11;pc.ReadAhead=30;await store.OweRepairAsync(pair.PairId,Side.Local,"journal",token);return new([],null,new(1,30)){ContinuityLost=true};
                case 3 when late:
                    waiting.TrySetResult();await finished.Task.WaitAsync(token);
                    var other=ItemChecks.File(Side.Local,"other",4,"other-id");pc.Local.Seed(other,[4]);
                    return new([new(Side.Local,ChangeKind.Changed,other.Path,null,other.Id,other,true,false){Usn=30,JournalId=1}],null,new(1,31));
                case 3: case 4 when late: if(!finished.Task.IsCompleted || pc.ReadAhead!=11 || pc.Paused){var error=new InvalidDataException("Repair returned to read-ahead instead of the held journal record");acknowledged.TrySetException(error);throw error;}
                    pc.Local.Seed(version,[3]);return new([new(Side.Local,ChangeKind.Changed,version.Path,null,version.Id,version,true,false){Usn=11,JournalId=1}],null,new(1,32));
                default: await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();}};
            var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"journal",ItemChecks.Settings)){}
            var host=new Host((_,_)=>Task.FromResult<IStore?>(ledger),(_,_,_,_)=>(local,remote),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
            try{
                await host.StartAsync(ct);
                await acknowledged.Task.WaitAsync(ct);
            }finally{stop.Cancel();await host.StopAsync(default);}
        }
    }
    /// <summary>Confirmed exclusion reports the actual apply diagnosis even when its preview was healthy.</summary>
    public static async Task ExclusionCauseAsync()
    {
        using var store=new InMemoryStateStore();var pair=Bound("exclude",@"T:\Exclude",1);await store.SavePairAsync(pair,default);
        var local=new FakeSide(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};var cloud=new FakeSide(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};
        var selection=new Exclusion(Side.Local,"item",false);local.Seed(ItemChecks.File(Side.Local,selection.Path,1,"pc-file"),[1]);
        var db=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var gate=(PublicationStore)(object)db;gate.Store=store;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_)=>(local,cloud),ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try {
            var preview=await host.SetExclusionAsync(pair.PairId,selection,false,false,default);
            Check.Require(preview.Outcome==null && preview.Cause==null && (await store.PairAsync(pair.PairId,default))!.Exclusions.Count==0,"Preview changed the exclusion or reported an apply outcome");
            gate.OnSave=()=>{local.ReadFailures[selection.Path]=new ItemFailedException(selection.Path,TextKey.ReadLocalFailed,null);return Task.CompletedTask;};
            var applied=await host.SetExclusionAsync(pair.PairId,selection,false,true,default);
            Check.Require(applied.Outcome==ExclusionOutcome.Pending && applied.Cause==TextKey.ReadLocalFailed && local.Items.ContainsKey(selection.Path),"Host dropped the observed apply cause or inferred it from the healthy preview");
        } finally {await host.StopAsync(default);}
    }
    /// <summary>Later feed input cannot retry a retained item through a configuration publication in progress.</summary>
    public static async Task PublicationRetryAsync()
    {
        using var store=new InMemoryStateStore();var pair=Bound("scope",@"T:\Scope",1);await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,0),"{}",default);
        var logs=new MemoryLogs();using(var preflight=new RunLog(logs,"scope",ItemChecks.Settings)){}
        var db=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var gate=(PublicationStore)(object)db;gate.Store=store;
        var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var lp=(JournalProbe)(object)local;lp.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Fixed)};lp.Quiet=true;
        var cloud=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var cp=(JournalProbe)(object)cloud;cp.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};cp.Quiet=true;
        var second=Signal();var admitted=Signal();var saving=Signal();var commit=Signal();var resumed=Signal();var firstAdmitted=Signal();
        var path="held";var input=new Change(Side.Dropbox,ChangeKind.Changed,path,null,"missing",null,false,false);
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(10));var ct=cancel.Token;int lookups=0;
        gate.Lookup=key=>{if(key==PathNames.DropboxKey(path) && Interlocked.Increment(ref lookups)>2 && commit.Task.IsCompleted)resumed.TrySetResult();};
        cp.Local.Pages.Enqueue(new([input],"one",null)); // The first held input is finite; its successor must arrive live.
        cp.Feed=async (cursor,token)=>{
            if(cursor==null)throw new InvalidDataException("Live reader restarted from settlement instead of the acquired finite cursor");
            if(cursor=="one"){firstAdmitted.TrySetResult();await second.Task.WaitAsync(token);return new([input],"two",null);}
            admitted.TrySetResult();await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();};
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_)=>(local,cloud),ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try{
            await host.StartAsync(ct);await firstAdmitted.Task.WaitAsync(ct);
            Check.Require(Volatile.Read(ref lookups)>=2,"Feed failed to retain and retry the original missing identity");
            gate.OnSave=()=>{saving.TrySetResult();return commit.Task.WaitAsync(ct);};
            var publication=host.SetIgnoresAsync(pair.PairId,[],ct);await saving.Task.WaitAsync(ct);int before=Volatile.Read(ref lookups);
            second.SetResult();await admitted.Task.WaitAsync(ct);
            Check.Require(Volatile.Read(ref lookups)==before,"Retained input re-entered before the scope publication finished");
            commit.SetResult();await publication;await resumed.Task.WaitAsync(ct);
        }finally{second.TrySetResult();commit.TrySetResult();cancel.Cancel();await host.StopAsync(default);}
    }
    private static async Task RemovingMember()
    {
        using var db=new InMemoryStateStore();var parent=Bound("parent",@"T:\Parent",1) with {Enabled=false};
        var child=Bound("child",Path.Combine(parent.LocalRoot,"child"),1);
        await db.SavePairAsync(parent,default);await db.SavePairAsync(child,default);
        await db.CommitAsync(parent.PairId,null,SyncedEntries.From(ItemChecks.File(Side.Local,"child",0,"pc-child",true),ItemChecks.File(Side.Dropbox,"child",0,"db-child",true)),null,null,default);
        var wrapped=System.Reflection.DispatchProxy.Create<IStore,PublicationStore>();var gate=(PublicationStore)(object)wrapped;gate.Store=db;
        int opens=0;var opened=Signal();var entered=Signal();var release=Signal();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(wrapped),(p,_,_,_)=>{
            if(p.PairId==child.PairId){Interlocked.Increment(ref opens);opened.TrySetResult();}
            return(new FakeSide(Side.Local){Root=(p.Roots!.Local,null,null,DriveType.Fixed)},new FakeSide(Side.Dropbox){Root=(null,p.Roots.Dropbox,null,null)});
        },ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try {
            await host.StartAsync(default);await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gate.OnFind=()=>{entered.TrySetResult();return release.Task;};var remove=host.RemovePairAsync(child.PairId,default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var bind=host.BindAsync(child.PairId,default);var resume=host.ResumeAsync(child.PairId,default);
            release.SetResult();await remove;try{await bind;}catch(EngineRequestException){} await resume;
            Check.Require(opens==1 && await db.PairAsync(child.PairId,default)==null && await db.PairAsync(parent.PairId,default)!=null,"Removal admitted a child adapter while publishing the parent scope");
        } finally {release.TrySetResult();await host.StopAsync(default);}
    }
    private static async Task ReleaseFailure()
    {
        using var db=new InMemoryStateStore();var pair=Bound("pending",@"T:\Pending",1);await db.SavePairAsync(pair,default);var entered=Signal();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (_,_,_,_,ct)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();},ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,reserve:_=>{},releaseReservation:(_,_)=>throw new IOException("journal release"));
        try {await host.StartAsync(default);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));await host.PauseAsync(pair.PairId,default);
            try{await host.RemovePairAsync(pair.PairId,default);throw new InvalidDataException("Journal release failure was hidden");}catch(IOException){}
            Check.Require(!host.IsPaused(pair.PairId) && await db.PairAsync(pair.PairId,default)==null,"Committed removal left the failed journal member in host state");
        } finally {await host.StopAsync(default);}
    }
    private static async Task ExplicitBind()
    {
        using var db=new InMemoryStateStore();var pair=ItemChecks.Pair();await db.SavePairAsync(pair,default);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (_,_,_,_,ct)=>{entered.TrySetResult();await release.Task.WaitAsync(ct);return(new FakeSide(Side.Local),new FakeSide(Side.Dropbox));},ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        var bind=host.BindAsync(pair.PairId,default);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var all=host.PairsAsync(default).GetAsyncEnumerator(); var next=all.MoveNextAsync().AsTask();
        try { Check.Require(await next.WaitAsync(TimeSpan.FromSeconds(2)),"Missing pair"); }
        catch(TimeoutException e){throw new InvalidDataException("Explicit Bind blocked the CLI",e);}
        finally {release.TrySetResult();try{await bind;}catch(EngineRequestException){} await next; await all.DisposeAsync(); await host.StopAsync(default);}
    }
    private static async Task PendingOuterScope()
    {
        using var db=new InMemoryStateStore(); var p=ItemChecks.Pair();
        var roots=new RootIdentity(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(5,6,null),new("account","root","home","folder"));
        var outer=p with {Roots=roots,Enabled=false};await db.SavePairAsync(outer,default);
        await db.CommitAsync(outer.PairId,null,SyncedEntries.From(ItemChecks.File(Side.Local,"child",0,"pc-child",true),ItemChecks.File(Side.Dropbox,"child",0,"db-child",true)),null,null,default);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<PathScope>? live=null;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (pair,_,scope,_,ct)=>{
            var side=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var probe=(JournalProbe)(object)side;
            probe.Local=new(Side.Local){Root=(roots.Local,null,null,DriveType.Fixed)};
            if(pair.PairId==outer.PairId) {live=scope; entered.TrySetResult();await release.Task.WaitAsync(ct);probe.Published=()=>published.TrySetResult();}
            return(side,new FakeSide(Side.Dropbox){Root=(null,roots.Dropbox,null,null)});
        },ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try
        {
            await host.StartAsync(default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.AddPairAsync(p with {PairId="inner",LocalRoot=Path.Combine(p.LocalRoot,"child"),DropboxRoot=p.DropboxRoot+"/child"},default);
            release.TrySetResult(); await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(live!().Excludes("child/file","child/file",false),"Late first bind overwrote the published nested scope");
        }
        finally {release.TrySetResult(); await host.StopAsync(default);}
    }
    private static async Task IndependentBind()
    {
        using var db=new InMemoryStateStore();
        var slow=ItemChecks.Pair() with {PairId="slow"}; var other=slow with {PairId="other"};
        var roots=new RootIdentity(other.LocalRoot,other.DropboxRoot,other.ConnectionId,new(1,2,null),new("acct","root","home","folder"));
        other=other with {Roots=roots};
        await db.SavePairAsync(slow,default); await db.SavePairAsync(other,default);
        using var lifetime=new CancellationTokenSource();
        var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pairToken=default;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (p,_,_,_,ct)=>{
            if(p.PairId==slow.PairId) {pairToken=ct; entered.SetResult(); await held.Task.WaitAsync(ct);}
            var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>(); var probe=(JournalProbe)(object)local;
            probe.Local=new(Side.Local){Root=(roots.Local,null,null,DriveType.Fixed)};
            if(p.PairId==other.PairId) probe.Published=()=>{if(!probe.Paused) observed.TrySetResult();};
            return (local,new FakeSide(Side.Dropbox){Root=(null,roots.Dropbox,null,null)});
        },ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        var startup=host.StartAsync(lifetime.Token);
        try
        {
            await Task.WhenAll(entered.Task,observed.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            await using var pairs=host.PairsAsync(default).GetAsyncEnumerator();
            Check.Require(await pairs.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),"Pending first bind monopolized the CLI");
            await pairs.DisposeAsync();
            await host.PauseAsync(slow.PairId,default).WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(pairToken.IsCancellationRequested && !lifetime.IsCancellationRequested,"Pause did not cancel just the pending member");
        }
        catch(TimeoutException e) {throw new InvalidDataException("One pending factory delayed host readiness, the other pair, or the CLI",e);}
        finally { held.TrySetResult(); lifetime.Cancel(); try { await startup; } catch(OperationCanceledException) { } await host.StopAsync(default); }

    }
    private static async Task ForeignCancellation()
    {
        using var db=new InMemoryStateStore(); var p=ItemChecks.Pair(); var bad=p with {PairId="broken"};
        await db.SavePairAsync(bad,default);
        var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_,_)=>{
            attempted.TrySetResult(); throw new OperationCanceledException();
        },ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try {await host.StartAsync(default);} catch(OperationCanceledException e){throw new InvalidDataException("Provider cancellation escaped pair admission",e);}
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)); await host.PauseAsync(bad.PairId,default);
        Check.Require(db.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure),"A failed first bind has no notice");
        await host.ResumeAsync(bad.PairId,default); await host.StopAsync(default);
    }
    private static async Task DisconnectedAdapter()
    {
        using var db=new InMemoryStateStore(); var p=ItemChecks.Pair();
        var roots=new RootIdentity(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(1,3,null),new("account","root","home","folder"));
        p=p with {Roots=roots,Enabled=false};await db.SavePairAsync(p,default);
        bool connected=true;int staleReads=0,denials=0;
        var opened=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(pair,_,_,_)=>{
            if(!connected){Interlocked.Increment(ref denials);observed.TrySetResult();throw new EngineRequestException(TextKey.AccountMissing);}
            var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>(); var pc=(JournalProbe)(object)local;
            pc.Local=new(Side.Local){Root=(roots.Local,null,null,DriveType.Fixed)};pc.Published=()=>opened.TrySetResult();
            var cloud=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var remote=(JournalProbe)(object)cloud;
            remote.Local=new(Side.Dropbox){Root=(null,roots.Dropbox,null,null)};
            remote.ReadRoot=()=>{if(!connected){Interlocked.Increment(ref staleReads);observed.TrySetResult();}};
            return(local,cloud);
        },ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>{connected=false;return Task.CompletedTask;});
        try
        {
            await host.StartAsync(default);await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.DisconnectAsync(p.ConnectionId,default);await host.ResumeAsync(p.PairId,default);
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try {await host.BindAsync(p.PairId,default);}catch(EngineRequestException){}
            Check.Require(staleReads==0 && denials>=2,"Disconnected slot reached its cached Dropbox adapter instead of checking membership on Resume and Bind");
        } finally {await host.StopAsync(default);}
    }
    public class JournalProbe : System.Reflection.DispatchProxy
    {
        internal FakeSide Local=null!;
        internal bool Paused;
        internal int ReadAhead,Settled,Rewinds;
        internal Action? Published;
        internal bool FailRoot;
        internal Action? ReadRoot;
        internal Func<CancellationToken,Task<JobReply>>? Job;
        internal int JobReads;
        internal Func<string?,CancellationToken,Task<ChangePage>>? Feed;
        internal bool Quiet;
        internal Func<JournalPosition,JournalPosition>? OnSettle;
        private JournalPosition? committed;
        private static async Task<ChangePage> WaitForStop(CancellationToken ct){await Task.Delay(Timeout.Infinite,ct);return new([],null,null);}
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method!.Name==nameof(ISide.ReadJobAsync) && Job!=null){Interlocked.Increment(ref JobReads);return Job((CancellationToken)args![1]!);}
            if(Quiet && method.Name==nameof(ISide.SettleJournalAsync)){var offered=(JournalPosition)args![0]!;committed=OnSettle?.Invoke(offered)??offered;}
            if(Quiet && method.Name==nameof(ISide.SettledJournalAsync)) return Task.FromResult(committed);
            if(Quiet && method.Name==nameof(ISide.ReadChangesAsync) && args?.Length==3) return Feed?.Invoke((string?)args[0],(CancellationToken)args[2]!) ?? WaitForStop((CancellationToken)args[2]!);
            if(method.Name==nameof(ISide.RootAsync)) ReadRoot?.Invoke();
            if(method.Name==nameof(ISide.RootAsync) && FailRoot) throw new IOException("unavailable root");
            if(method.Name==nameof(ISide.SetMembershipAsync))
            {
                bool paused=(bool)args![1]!; if(paused!=Paused){ReadAhead=Settled;Rewinds++;}
                Paused=paused; Published?.Invoke(); return Task.CompletedTask;
            }
            return method.Invoke(Local,args);
        }
    }
    private static async Task MemberRewind()
    {
        using var db=new InMemoryStateStore(); var p=ItemChecks.Pair();
        var root=new RootIdentity(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(5,6,null),new("account","root","home","folder"));
        p=p with {Roots=root}; var second=p with {PairId="sibling"};
        await db.SavePairAsync(p,default); await db.SavePairAsync(second,default);
        var ready=new CountdownEvent(2);
        var probes=new System.Collections.Concurrent.ConcurrentDictionary<string,ISide>();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(pair,_,_,_)=>{
            // Reopening an adapter still addresses the same journal member and its settled position.
            var local=probes.GetOrAdd(pair.PairId,id=>{
                var side=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>(); var probe=(JournalProbe)(object)side;
                probe.Local=new(Side.Local){Root=(root.Local,null,null,DriveType.Fixed)};
                int once=0;probe.Published=()=>{if(Interlocked.Exchange(ref once,1)==0)ready.Signal();};
                return side;
            });
            return(local,new FakeSide(Side.Dropbox){Root=(null,root.Dropbox,null,null)});
        },ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        await host.StartAsync(default); Check.Require(ready.Wait(TimeSpan.FromSeconds(5)),"Members not published");
        var a=(JournalProbe)(object)probes[p.PairId]; var b=(JournalProbe)(object)probes[second.PairId]; a.Settled=4;a.ReadAhead=7;b.Settled=3;b.ReadAhead=9;
        await host.BindAsync(p.PairId,default);
        Check.Require(a.ReadAhead==4 && !a.Paused && a.Rewinds>=2 && b.ReadAhead==9,"Bind lost read-ahead or rewound an unrelated journal member");
        await host.StopAsync(default); ready.Dispose();
    }

}
