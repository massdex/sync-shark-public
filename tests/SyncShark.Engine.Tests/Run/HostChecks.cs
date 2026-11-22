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
        await Task.WhenAll(IndependentBind(),MemberRewind(),ForeignCancellation());
    }

    private static async Task IndependentBind()
    {
        using var db=new InMemoryStateStore();
        var slow=ItemChecks.Pair() with {PairId="slow"}; var other=slow with {PairId="other"};
        await db.SavePairAsync(slow,default); await db.SavePairAsync(other,default);
        using var lifetime=new CancellationTokenSource();
        var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pairToken=default;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),async (p,_,_,_,ct)=>{
            if(p.PairId==slow.PairId) {pairToken=ct; entered.SetResult(); await held.Task.WaitAsync(ct);}
            else observed.SetResult();
            return (new FakeSide(Side.Local),new FakeSide(Side.Dropbox));
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
    public class JournalProbe : System.Reflection.DispatchProxy
    {
        internal FakeSide Local=null!;
        internal bool Paused;
        internal int ReadAhead,Settled,Rewinds;
        internal Action? Published;
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method!.Name==nameof(ISide.SetMembershipAsync))
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
        var probes=new System.Collections.Concurrent.ConcurrentDictionary<string,JournalProbe>();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(pair,_,_,_)=>{
            var local=System.Reflection.DispatchProxy.Create<ISide,JournalProbe>(); var probe=(JournalProbe)(object)local;
            probe.Local=new(Side.Local){Root=(root.Local,null,null,DriveType.Fixed)};
            int once=0;probe.Published=()=>{if(Interlocked.Exchange(ref once,1)==0)ready.Signal();}; probes[pair.PairId]=probe;
            return(local,new FakeSide(Side.Dropbox){Root=(null,root.Dropbox,null,null)});
        },ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        await host.StartAsync(default); Check.Require(ready.Wait(TimeSpan.FromSeconds(5)),"Members not published");
        var a=probes[p.PairId]; var b=probes[second.PairId]; a.Settled=4;a.ReadAhead=7;b.Settled=3;b.ReadAhead=9;
        await host.BindAsync(p.PairId,default);
        Check.Require(a.ReadAhead==4 && !a.Paused && a.Rewinds>=2 && b.ReadAhead==9,"Bind lost read-ahead or rewound an unrelated journal member");
        await host.StopAsync(default); ready.Dispose();
    }

}
