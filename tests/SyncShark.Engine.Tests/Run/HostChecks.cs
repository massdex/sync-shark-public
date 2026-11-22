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
        Check.Require((await host.StartAsync(default)).Count==0,"Empty machine acquired a volume");
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
        Check.Require(await host.StatusAsync(null,default)=="switch refused" && (await host.StartAsync(default)).Count==0 && before==opens.Count,"Switch refusal opened the store");
        var stop=host.Stopping; await host.StopAsync(default); Check.Require(stop.IsCancellationRequested,"Host did not notify the pipe owner of stop");
        var errors=new List<string>();
        foreach(var check in new Func<Task>[]{IndependentBind,MemberRewind,ForeignCancellation,DisconnectedAdapter,ExplicitBind,PendingOuterScope})
            try { await check(); } catch(Exception e) {errors.Add($"{check.Method.Name}: {e.Message}");}
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
        var notice=new Notice(1,NoticeKind.Failure,TextKey.FailChanged,"local-folder/item",new([new(Side.Local,"local-folder/item","item-id")],Side.Local),DateTimeOffset.UnixEpoch,false);
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
        }
        finally{release.TrySetResult();await host.StopAsync(default);}
    }
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PairConfig Bound(string id,string path,ulong serial)
    {
        var p=ItemChecks.Pair() with {PairId=id,LocalRoot=path,DropboxRoot="/"+id};
        return p with {Roots=new(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(serial,2,null),new("account","root","home",id))};
    }
    private sealed class MemoryLogs : ILogFiles
    {
        public TextWriter Open(string path,int bufferCharacters)=>new StringWriter();
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
        var bind=Signal();var started=Signal();var checking=Signal();var observed=Signal();var freeTrim=Signal();var readyTrim=Signal();var jobTrim=Signal();var finishTrim=Signal();CancellationToken trimToken=default;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (pair,_,_,_,ct)=>{
            if(pair.PairId==pending.PairId){started.TrySetResult();await bind.Task.WaitAsync(ct);}
            var pc=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var lp=(JournalProbe)(object)pc;lp.Local=new(Side.Local){Root=(pair.Roots!.Local,null,null,DriveType.Removable)};lp.Quiet=true;
            var remote=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>();var rp=(JournalProbe)(object)remote;rp.Local=new(Side.Dropbox){Root=(null,pair.Roots.Dropbox,null,null)};rp.Quiet=true;
            if(pair.PairId==jobPair.PairId)rp.Job=async token=>{checking.TrySetResult();await observed.Task.WaitAsync(token);return new JobReply.Running();};
            return(pc,remote);
        },ItemChecks.Settings,logs,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask,async (root,_,ct)=>{
            if(root==Path.GetPathRoot(free.LocalRoot))freeTrim.TrySetResult();
            if(root==Path.GetPathRoot(ready.LocalRoot)){trimToken=ct;readyTrim.TrySetResult();await finishTrim.Task.WaitAsync(ct);}
            if(root==Path.GetPathRoot(jobPair.LocalRoot))jobTrim.TrySetResult();
        });
        try{
            await host.StartAsync(default);await Task.WhenAll(started.Task,checking.Task,freeTrim.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(!jobTrim.Task.IsCompleted && !readyTrim.Task.IsCompleted,"Trim preceded recovery or a pending first bind");
            Check.Require(await host.RemovePairAsync(pending.PairId,default)==null,"Pending member not removed");
            await readyTrim.Task.WaitAsync(TimeSpan.FromSeconds(5));await host.PauseAsync(ready.PairId,default).WaitAsync(TimeSpan.FromSeconds(5));
            Check.Require(!trimToken.IsCancellationRequested,"Pair pause canceled volume trim");finishTrim.TrySetResult();
            observed.TrySetResult();await jobTrim.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }finally{bind.TrySetResult();observed.TrySetResult();finishTrim.TrySetResult();await host.StopAsync(default);}
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
        internal bool Quiet;
        private static async Task<ChangePage> WaitForStop(CancellationToken ct){await Task.Delay(Timeout.Infinite,ct);return new([],null,null);}
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method!.Name==nameof(ISide.ReadJobAsync) && Job!=null){Interlocked.Increment(ref JobReads);return Job((CancellationToken)args![1]!);}
            if(Quiet && method.Name==nameof(ISide.ReadChangesAsync) && args?.Length==3) return WaitForStop((CancellationToken)args[2]!);
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
