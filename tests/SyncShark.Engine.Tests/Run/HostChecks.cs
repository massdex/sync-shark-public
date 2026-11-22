using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        foreach(var check in new Func<Task>[]{IndependentBind,MemberRewind,ForeignCancellation,ExplicitBind,PendingOuterScope,RetryWiring,VolumeAttempts})
            try { await check(); } catch(Exception e) {errors.Add(e.Message);}
        Check.Require(errors.Count==0,string.Join("; ",errors));
    }

    private static async Task VolumeAttempts()
    {
        var p=ItemChecks.Pair();
        var groups=Host.VolumeTrim.Register([p with {PairId="pending",LocalRoot=@"T:\Pending"},
            p with {PairId="ready",LocalRoot=@"T:\Ready"},p with {PairId="other",LocalRoot=@"U:\Other"}]);
        var firstBind=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=BindThenAttempt();
        async Task<bool> BindThenAttempt(){await firstBind.Task;return groups["pending"].Attempted("pending");}
        try
        {
            var early=groups["ready"].Attempted("ready"); var other=groups["other"].Attempted("other");
            Check.Require(!pending.IsCompleted && !early && other,"A pending first bind must delay trim only on its own volume");
        }
        finally {firstBind.TrySetResult();}
        Check.Require(await pending && !groups["pending"].Attempted("pending") && !groups["other"].Attempted("other"),"Trim was lost or repeated after the last member attempted");
    }

    // Layout-level proof on the isolated skeleton: actual job observation is checked with W18 at integration.
    private static Task RetryWiring()
    {
        var root=CheckArguments.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray()).Root;
        var syntax=CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"src","SyncShark.Engine","Run","Host.cs"))).GetRoot();
        var method=syntax.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText==nameof(Host.ResolveAsync));
        var branch=method.DescendantNodes().OfType<IfStatementSyntax>().SingleOrDefault(i=>i.Condition.ToString().Contains("ResolutionChoice.Retry",StringComparison.Ordinal));
        var calls=branch?.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Select(i=>i.Expression.ToString()).ToArray() ?? [];
        Check.Require(calls.Count(c=>c.EndsWith(".Queue.RetryHeldAsync",StringComparison.Ordinal))==1 &&
            calls.Count(c=>c.EndsWith(".Runner.Unfinished.CheckAsync",StringComparison.Ordinal))==1 &&
            branch!.Statement.DescendantNodes().OfType<ReturnStatementSyntax>().All(ret=>!ret.Ancestors().OfType<IfStatementSyntax>().Any(i=>i!=branch)),
            "Manual Retry must attempt the exact held operation and the existing pair checker once each without a queue-hit early return");
        return Task.CompletedTask;
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
    public class JournalProbe : System.Reflection.DispatchProxy
    {
        internal FakeSide Local=null!;
        internal bool Paused;
        internal int ReadAhead,Settled,Rewinds;
        internal Action? Published;
        internal bool FailRoot;
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method!.Name==nameof(ISide.RootAsync) && FailRoot) throw new IOException("unavailable root");
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
