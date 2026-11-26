namespace Massdex.SyncShark.Engine.Run;

/// <summary>Admits, runs, rewinds and drains each host member's pair runner on the host's shared state.</summary>
internal sealed class HostLifecycle(Host host)
{
    internal static string LogFolder => ResourceNames.LogDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal void Launch(HostMember m,IReadOnlyList<PairConfig> pairs,CancellationToken ct)
    {
        var rewinding=m.Live.IsCompleted && m.Stop!=null;
        if(rewinding) {m.Stop!.Dispose(); m.Stop=null;}
        if(Volatile.Read(ref m.Closing)!=0) return;
        m.Opening?.Dispose(); var opening=m.Opening=CancellationTokenSource.CreateLinkedTokenSource(ct);
        m.Setup=Task.Run(async ()=>
        {
            var token=opening.Token;
            try
            {
                if(rewinding && m.Local!=null && m.Pair.Roots!=null) await m.Local.SetMembershipAsync(m.Pair,true,false,token);
                // Admission uses the composition factory, which verifies current connection membership before any provider call.
                m.Local=null!;m.Dropbox=null!;
                await Observe(m,pairs,token,retryRead:m.Pair.Enabled); token.ThrowIfCancellationRequested();
                await Membership(m,false,token); token.ThrowIfCancellationRequested();
                Start(m);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { }
            catch(Exception error) {m.Admitted=false; await Failed(m,error,CancellationToken.None);}
        });
    }
    internal HostMember Attach(PairConfig p)
    {
        var m = new HostMember(p); m.Queue=new(ct=>m.Local.SettledJournalAsync(ct)){Unfinished=m.Runner.Unfinished,ScopeNow=()=>m.Paths,WakeOwedRepair=at=>{if(at!=null) Volatile.Write(ref m.JournalWake,at); WakeRepair(m);}};
        m.Runner.JournalReplay=()=>
        {
            lock(m) return ((m.RepairFinished??=new()).Token,ct=>ReplayJournal(m,ct));
        };
        host.Members[p.PairId] = m;
        return m;
    }
    internal async Task Observe(HostMember m,IReadOnlyList<PairConfig> pairs,CancellationToken ct,bool liveRootSignal=false,bool retryRead=false)
    {
        m.Admitted = false; int failures=0;
        while(true)
        {
            try
            {
                var (roots,drive,type) = await ReadRoots(m,ct);
                lock(host.Members) {m.Type=type; m.Paths=new(m.Pair,host.PublishedPairs ?? pairs,type,host.Settings.DefaultIgnoreRules);}
                m.Admitted = await m.Runner.CheckRootsAsync(m.Pair,roots,drive,host.Store!,ct);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                if(liveRootSignal && e is not (ItemFailedException or EngineRequestException)) await host.Store!.SaveNoticeAsync(m.Pair.PairId,new(0,NoticeKind.RootIdentity,
                    TextKey.RootUnavailable,string.Empty,new([],null),host.Clock.GetUtcNow(),false),ct);
                else await Failed(m,e,ct);
                if(!retryRead || e is EngineRequestException || liveRootSignal && RootGone(e)) return;
                await RetryDelay(e,failures,ct);
                if(failures<int.MaxValue) ++failures;
            }
        }
    }
    // A missing or replaced root is a definite refusal: admission fails (RecheckRoots stops the run). Other read faults retry.
    private static bool RootGone(Exception e) => e is ChangedUnderneathException or ItemFailedException {Cause:TextKey.RootLocalIdentityChanged or TextKey.LocalRootMissing};
    internal async Task<HostMember> Ready(PairConfig p,CancellationToken ct)
    {
        if (!host.Members.TryGetValue(p.PairId,out var m)) m=Attach(p);
        if(!m.Setup.IsCompleted && !m.Admitted) throw new EngineRequestException(TextKey.RootUnavailable);
        if (m.Stop==null || !m.Admitted) await Observe(m,await host.Configurations(ct),ct);
        if (!m.Admitted) throw new EngineRequestException(TextKey.PairAwaitingDecision,(TextArg.Pair,p.PairId)); return m;
    }
    internal async Task Adapters(HostMember m,CancellationToken ct)
    {
        if (m.Local==null) (m.Local,m.Dropbox)=await host.OpenSides(m.Pair,host.Store!,()=>m.Paths,m.Event,ct);
    }
    internal async Task<(RootIdentity? Roots,DriveEvidence? Drive,DriveType Type)> ReadRoots(HostMember m,CancellationToken ct,bool acceptDrive=false)
    {
        await Adapters(m,ct);
        var a=await m.Local.RootAsync(ct); var p=m.Pair;
        if(a.Drive!=null && DriveEvidence.Change(p.Drive,a.Drive)!=null && !acceptDrive)
            return (null,a.Drive,a.LocalDriveType ?? DriveType.Unknown);
        var b=await m.Dropbox.RootAsync(ct);
        return (a.Local != null && b.Dropbox != null ? new(p.LocalRoot,p.DropboxRoot,p.ConnectionId,a.Local,b.Dropbox) : null,
            a.Drive,a.LocalDriveType ?? DriveType.Unknown);
    }
    internal void Start(HostMember m)
    {
        if (!m.Admitted || !m.Pair.Enabled || host.IsPaused(m.Pair.PairId) || m.Stop != null || host.Stopped.IsCancellationRequested) return;
        if(Volatile.Read(ref m.Closing)!=0) return;
        var stop = m.Stop = CancellationTokenSource.CreateLinkedTokenSource(m.Opening?.Token ?? CancellationToken.None);
        m.Live=RunMemberAsync(m,stop);
    }
    // Boot and failures belong to one run: the catch-up callback and the retry loop share them.
    private async Task RunMemberAsync(HostMember m,CancellationTokenSource stop)
    {
        var boot=NewBoot(); int failures=0;
        TaskCompletionSource<bool> NewBoot() {var next=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);m.Boot=next.Task;return next;}
        void Attempted() { if(!stop.IsCancellationRequested) {Interlocked.Exchange(ref failures,0); boot.TrySetResult(true); WakeRepair(m); ReleaseTrim(m,false);} }
        var ct=stop.Token; var id=m.Pair.PairId; bool caughtUp=false, rewind=false;
        try
        {
            while(!ct.IsCancellationRequested && m.Admitted)
            {
                try
                {
                    if(rewind) {await Rewind(m,ct);if(!m.Admitted) return;rewind=false;}
                    m.Log??=NewLog(id,out _);
                    if(!caughtUp)
                    {
                        var pc=m.Local.BoundaryAsync(ct);var cloud=m.Dropbox.BoundaryAsync(ct);await Task.WhenAll(pc,cloud);
                        await m.Runner.CatchUpAsync(m.Pair,((await pc).Position,(await cloud).Cursor),host.Settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,host.Store!,Attempted,ct,continueLive:true);
                        caughtUp=true;
                    }
                    await BeginOwedRepair(m,ct);
                    await ReplayJournal(m,ct);
                    await m.Runner.RunAsync(m.Pair,host.Settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,host.Store!,ct);
                }
                catch(ChangedUnderneathException error) when(!ct.IsCancellationRequested && PathNames.Same(error.Path,m.Pair.LocalRoot)) {rewind=true;}
                catch(Exception error) when(!ct.IsCancellationRequested)
                {
                    if(boot.Task.IsCompleted) boot=NewBoot();
                    caughtUp=false;rewind=true;
                    await Failed(m,error,ct);await RetryDelay(error,failures,ct);
                    if(failures<int.MaxValue) ++failures;
                }
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        finally {boot.TrySetResult(false);m.Boot=Task.FromResult(false);m.Log?.Dispose();m.Log=null;}
    }
    internal void ReleaseTrim(HostMember m,bool removed)
    {
        if(m.Trim is not {} volume) return;
        lock(volume)
        {
            if(!removed && m.Type!=DriveType.Unknown) volume.Type=m.Type;
            if(!volume.Attempted(m.Pair.PairId) || volume.Type==DriveType.Unknown) return;
            if(host.Trim!=null) volume.Work=TrimAsync();
        }
        async Task TrimAsync()
        {
            var token=host.Shutdown.Token;
            try {await host.Trim!(Path.GetPathRoot(m.Pair.LocalRoot)!,volume.Type,token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested) { }
            catch(Exception error) {await Failed(m,error,token);}
        }
    }
    private async Task<JournalPosition?> ReplayJournal(HostMember m,CancellationToken ct)
    {
        // The Local reader (or the gap between readers) consumes each latched completion once.
        lock(m) if(m.RepairFinished is {IsCancellationRequested:true} signal){m.RepairFinished=null;signal.Dispose();}
        if(Volatile.Read(ref m.JournalWake) is not {} wake || (await host.Store!.ReadRepairAsync(m.Pair.PairId,Side.Local,ct)).Reason!=null) return null;
        var floor=await m.Queue.RewindJournalAsync(wake,async ()=>
        {
            await m.Local.SetMembershipAsync(m.Pair,true,false,ct);
            await Membership(m,false,ct);
        },ct);
        Interlocked.CompareExchange(ref m.JournalWake,null,wake);
        return floor;
    }
    private async Task Rewind(HostMember m,CancellationToken ct)
    {
        Interlocked.Increment(ref m.Closing);
        // A running Repair keeps going: AutoSync's failure is not its failure, and only a lost root (RecheckRoots) stops it.
        try {
            await m.Local.SetMembershipAsync(m.Pair,true,false,ct); await RecheckRoots(m,ct); m.Queue.RestartPrefix();
        } finally {Interlocked.Decrement(ref m.Closing);}
    }
    private async Task RecheckRoots(HostMember m,CancellationToken ct)
    {
        await Observe(m,await host.Configurations(ct),ct,true,retryRead:true); await Membership(m,false,ct);
        if(!m.Admitted) {m.Stop?.Cancel(); m.ManualStop?.Cancel();}
    }
    internal void WakeRepair(HostMember m)
    {
        if(!m.Admitted || !m.Pair.Enabled || host.IsPaused(m.Pair.PairId) || m.Stop==null) return;
        var ct=m.Stop.Token;
        lock(m)
        {
            if(m.Closing!=0) return;
            m.DebtChanged=true;
            if(!m.DebtWake.IsCompleted) return;
            m.DebtWake=Task.Run(async ()=>
            {
                try
                {
                    while(true)
                    {
                        await m.Manual.WaitAsync(ct);
                        lock(m){if(!m.DebtChanged) { m.DebtWake=Task.CompletedTask; return; } m.DebtChanged=false;}
                        await BeginOwedRepair(m,ct);
                    }
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
                catch(Exception error) {await Failed(m,error,ct);}
            });
        }
    }
    private async Task BeginOwedRepair(HostMember m,CancellationToken ct)
    {
        var id=m.Pair.PairId; var state=await host.Store!.RepairStateAsync(id,ct);
        var local=await host.Store.ReadRepairAsync(id,Side.Local,ct); var remote=await host.Store.ReadRepairAsync(id,Side.Dropbox,ct);
        var scopes=(await host.Store.PairAsync(id,ct))?.OwedScopes;
        if(local.Reason!=null) m.Queue.LoseCoverage(Side.Local);
        if(remote.Reason!=null) m.Queue.LoseCoverage(Side.Dropbox);
        var full=!state.FirstRepairDone || local.Reason!=null || remote.Reason!=null;
        if(!full && scopes is not {Count:>0}) return;
        _=ClaimRepair(m,ct,Run,false);
        async Task Run(CancellationToken token)
        {
            // One scope's known acquisition failure stays in its own run; cancellation, pair-root loss and other faults still propagate.
            async Task Isolated(Scope? scope,Func<Task> work)
            {
                try { await work(); }
                catch(InventoryIncompleteException error) { await Failed(m,error,token); }
                catch(ItemFailedException error) when(scope!=null && InScope(scope,error.Path)) { await Failed(m,error,token); }
            }
            if(full)
            {
                try { await ManualRepair(m,token); m.RepairFailures=0; }
                catch(InventoryIncompleteException error) { await Failed(m,error,token); Rewake(error); }
                catch(Exception error) when(!token.IsCancellationRequested) { Rewake(error); throw; }
            }
            // A full run neither lists nor retires owed scopes; run those still owed now, including any it owed itself.
            var stillOwed = full ? (await host.Store!.PairAsync(id,token))?.OwedScopes ?? [] : scopes ?? [];
            var served=new HashSet<Scope>(); var caught=m.CaughtUp;
            foreach(var scope in stillOwed)
            {
                served.Add(scope);
                // A pending no-keep exclusion is finished by its own owner, never by a scoped comparison; a fault leaves it owed for the next run.
                if(NoKeepRoot(m.Pair,scope) is {} exclusion) { await Isolated(scope,()=>FinishExclusion(m,scope,exclusion,token)); continue; }
                await Isolated(scope,()=>Repair.RunAsync(m.Pair,host.Settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,host.Store!,scope,null,token,caughtUp:caught));
            }
            await HandOff(m,served,token);
        }
        // The full Repair stays owed after a failure and nothing else re-runs it while AutoSync is healthy: wake it again after the
        // host's retry backoff. No failure class tells an outage from a permanent fault here, so every failure re-wakes at that backoff.
        void Rewake(Exception error)
        {
            var failures=m.RepairFailures; if(failures<int.MaxValue) m.RepairFailures=failures+1;
            _=Task.Run(async ()=>
            {
                try { await RetryDelay(error,failures,ct); WakeRepair(m); }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
            });
        }
    }
    /// <summary>The one owner of a confirmed no-keep apply, run under ClaimRepair. Its owed scope retires only at the revision read before the walk.</summary>
    internal async Task<(ExclusionOutcome Outcome,TextKey? Cause)> FinishExclusion(HostMember m,Scope scope,Exclusion exclusion,CancellationToken token)
    {
        var id=m.Pair.PairId; var revision=(await host.Store!.ReadRepairAsync(id,Side.Dropbox,token)).Revision;
        var applied=await new Executor.Exclusions().ApplyAsync(await host.Store!.PairAsync(id,token) ?? m.Pair,host.Settings,m.Event,m.Paths,exclusion,m.Local,m.Dropbox,host.Store!,
            (s,side,f,t)=>host.Populate(m,s,side,f,t),(b,before,after,t)=>host.Compare(m,b,before,after,t),token,m.Queue.Held,m.Queue.CoversHop);
        if(applied.Outcome==ExclusionOutcome.Applied) await host.Store!.FinishRepairAsync(id,scope,revision,token);
        return applied;
    }
    // The owed footprint is a no-keep root when its path on the rule's side is exactly that rule's path.
    private static Exclusion? NoKeepRoot(PairConfig pair,Scope scope) =>
        pair.Exclusions.FirstOrDefault(e => !e.Keep && PathNames.Same(e.Path,e.Side==Side.Local ? scope.LocalPath : scope.DropboxPath));
    private static bool InScope(Scope scope,string path) =>
        scope.LocalPath.Length==0 || PathNames.IsSelfOrUnder(path,scope.LocalPath) || scope.DropboxPath.Length==0 || PathNames.IsSelfOrUnder(path,scope.DropboxPath);
    /// <summary>Re-arm the existing wake only for a scope committed after the dispatch snapshot; a retained or re-issued one does not.</summary>
    internal async Task HandOff(HostMember m,ISet<Scope>? served,CancellationToken ct)
    {
        if((await host.Store!.PairAsync(m.Pair.PairId,ct))?.OwedScopes is not {} owed) return;
        for(int i=0;i<owed.Count;i++) if(served==null || !served.Contains(owed[i])) { WakeRepair(m); return; }
    }
    internal Task ClaimRepair(HostMember m,CancellationToken ct,Func<CancellationToken,Task> work,bool explicitRequest)
    {
        lock(m)
        {
            if(m.Closing!=0) throw new OperationCanceledException(ct);
            if(!m.Manual.IsCompleted)
            {
                if(explicitRequest) throw new EngineRequestException(TextKey.RepairAlreadyRunning,(TextArg.Pair,m.Pair.PairId));
                return m.Manual;
            }
            m.ManualStop?.Dispose(); m.ManualStop=CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token=m.ManualStop.Token; var run=Task.Run(async ()=>
            {
                try { await work(token); }
                catch(ChangedUnderneathException error) when(!token.IsCancellationRequested && PathNames.Same(error.Path,m.Pair.LocalRoot))
                {
                    await RecheckRoots(m,token);
                    if(explicitRequest) throw;
                }
                catch(Exception error) when(explicitRequest) { m.Event(new(RunEventKind.Failed,nameof(Repair),string.Empty,RunLog.FailureTag(error),string.Empty){Provider=(error as ItemFailedException)?.Provider}); throw; }
                catch(Exception error) when(!token.IsCancellationRequested) { await Failed(m,error,token); }
            },token);
            m.Manual=run.ContinueWith(static _=>{},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            return run;
        }
    }
    private RunLog NewLog(string id,out LiveOutput? view,TextWriter? live=null,bool showFiles=false)
    {
        if (!PathNames.IsSingleSegment(id) || !PathNames.IsWindowsTransferPath(id)) throw Host.Unknown(id); var run=ResourceNames.NewId();
        return new(host.Logs,Path.Combine(LogFolder,id + ResourceNames.LogNameSeparator + run + ResourceNames.LogExtension),host.Settings,view=live==null ? null : new LiveOutput(live,host.Clock,showFiles,(id,run)));
    }
    private Task RetryDelay(Exception e,int failures,CancellationToken ct) => host.Settings.Backoff.WaitAsync(
        e is ItemFailedException {ProviderWait:{} wait} provider && wait>TimeSpan.Zero
            ? wait-(provider.ProviderWaitStarted is {} start ? host.Clock.GetElapsedTime(start) : TimeSpan.Zero)
            : host.Settings.Backoff.RetryDelay(failures),host.Clock,ct);
    private async Task Failed(HostMember m,Exception e,CancellationToken ct)
    {
        if(e is not (ItemFailedException or EngineRequestException)) m.Event(new(RunEventKind.Failed,nameof(Host),string.Empty,e.Message,string.Empty){Error=e});
        try { await host.Store!.SaveNoticeAsync(m.Pair.PairId,
        new(0,NoticeKind.Failure,e switch {ItemFailedException i=>i.Cause,EngineRequestException request=>request.Key,_=>TextKey.RootUnavailable},e is ItemFailedException f ? f.Path : string.Empty,
            new([],null),host.Clock.GetUtcNow(),false),ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception error) { m.Event(new(RunEventKind.Failed,nameof(IStore.SaveNoticeAsync),string.Empty,error.Message,string.Empty){Error=error}); }
    }
    internal Task Membership(HostMember m,bool removed,CancellationToken ct) => m.Local==null || m.Pair.Roots==null ? Task.CompletedTask : m.Local.SetMembershipAsync(m.Pair,!m.Pair.Enabled || !m.Admitted || host.IsPaused(m.Pair.PairId),removed,ct);
    internal static async Task Drain(HostMember m)
    {
        Interlocked.Increment(ref m.Closing);
        try {
        m.Opening?.Cancel(); m.Stop?.Cancel();
        try {await m.Setup;} catch(OperationCanceledException) when(m.Opening?.IsCancellationRequested==true) { }
        m.Stop?.Cancel(); m.ManualStop?.Cancel();
        try { await Task.WhenAll(m.Live,m.Manual,m.DebtWake); }
        catch (OperationCanceledException) when (m.Stop?.IsCancellationRequested==true || m.ManualStop?.IsCancellationRequested==true) { }
        finally
        {
            m.Queue.RestartPrefix(); m.JournalWake=null; lock(m){m.RepairFinished?.Dispose();m.RepairFinished=null;} m.Opening?.Dispose(); m.Opening=null; m.Stop?.Dispose(); m.Stop=null;
            m.ManualStop?.Dispose(); m.ManualStop=null; m.Manual=Task.CompletedTask; m.Log?.Dispose(); m.Log=null;
            if(m.Local!=null && m.Pair.Roots!=null) await m.Local.SetMembershipAsync(m.Pair,true,false,CancellationToken.None);
        }
        } finally {Interlocked.Decrement(ref m.Closing);}
    }
    /// <summary>A live view goes to <paramref name="opened"/>, whose owner drains it outside this member task, so a stalled reader never holds Stop or Pause.</summary>
    internal async Task ManualRepair(HostMember m,CancellationToken ct,TextWriter? live=null,bool showFiles=false,Action<LiveOutput>? opened=null)
    {
        using var log=NewLog(m.Pair.PairId,out var view,live,showFiles);
        if(view!=null) opened!(view);
        m.OperationLog.Value=log.Write;
        await Repair.RunAsync(m.Pair,host.Settings,log.Write,m.Paths,m.Queue,m.Local,m.Dropbox,host.Store!,null,null,ct,caughtUp:m.CaughtUp,localRepaired:()=>
        {
            lock(m) if(m.JournalWake!=null) (m.RepairFinished??=new()).Cancel();
            host.Logs.Prune(LogFolder,TimeSpan.FromDays(host.Settings.LogRetentionDays),host.Clock.GetUtcNow()); // a completed full Repair keeps run logs for the retention period
        },progress:view==null ? null : view.Accept); // background Repair makes no per-item records
    }
}
