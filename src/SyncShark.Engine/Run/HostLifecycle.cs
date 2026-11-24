using System.Collections.Concurrent;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Admits, runs, rewinds and drains each host member's pair runner on the host's shared state.</summary>
internal sealed class HostLifecycle(Host host)
{
    // The host's state under its own names, so the bodies moved from Host read unchanged.
    private IStore? store => host.Store;
    private EngineSettings settings => host.Settings;
    private TimeProvider clock => host.Clock;
    private ILogFiles logs => host.Logs;
    private ConcurrentDictionary<string,HostMember> members => host.Members;
    private IReadOnlyList<PairConfig>? publishedPairs => host.PublishedPairs;
    private CancellationTokenSource stopped => host.Stopped;
    private CancellationTokenSource shutdown => host.Shutdown;
    private Func<string,DriveType,CancellationToken,Task>? trim => host.Trim;
    private Func<PairConfig,IStore,Func<PathScope>,Action<RunEvent>,CancellationToken,Task<(ISide Local,ISide Dropbox)>> openSides => host.OpenSides;
    private bool IsPaused(string pairId) => host.IsPaused(pairId);
    private Task<List<PairConfig>> Configurations(CancellationToken ct) => host.Configurations(ct);
    private static string LogFolder => ResourceNames.LogDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

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
        members[p.PairId] = m;
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
                lock(members) {m.Type=type; m.Paths=new(m.Pair,publishedPairs ?? pairs,type,settings.DefaultIgnoreRules);}
                m.Admitted = await m.Runner.CheckRootsAsync(m.Pair,roots,drive,store!,ct);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                if(liveRootSignal && e is not (ItemFailedException or EngineRequestException)) await store!.SaveNoticeAsync(m.Pair.PairId,new(0,NoticeKind.RootIdentity,
                    TextKey.RootUnavailable,string.Empty,new([],null),clock.GetUtcNow(),false),ct);
                else await Failed(m,e,ct);
                if(!retryRead || e is EngineRequestException) return;
                await RetryDelay(e,failures,ct);
                if(failures<int.MaxValue) ++failures;
            }
        }
    }
    internal async Task<HostMember> Ready(PairConfig p,CancellationToken ct)
    {
        if (!members.TryGetValue(p.PairId,out var m)) m=Attach(p);
        if(!m.Setup.IsCompleted && !m.Admitted) throw new EngineRequestException(TextKey.RootUnavailable);
        if (m.Stop==null || !m.Admitted) await Observe(m,await Configurations(ct),ct);
        if (!m.Admitted) throw new EngineRequestException(TextKey.PairAwaitingDecision,(TextArg.Pair,p.PairId)); return m;
    }
    internal async Task Adapters(HostMember m,CancellationToken ct)
    {
        if (m.Local==null) (m.Local,m.Dropbox)=await openSides(m.Pair,store!,()=>m.Paths,m.Event,ct);
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
        if (!m.Admitted || !m.Pair.Enabled || IsPaused(m.Pair.PairId) || m.Stop != null || stopped.IsCancellationRequested) return;
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
                    m.Log??=NewLog(id);
                    if(!caughtUp)
                    {
                        var pc=m.Local.BoundaryAsync(ct);var cloud=m.Dropbox.BoundaryAsync(ct);await Task.WhenAll(pc,cloud);
                        await m.Runner.CatchUpAsync(m.Pair,((await pc).Position,(await cloud).Cursor),settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,store!,Attempted,ct,continueLive:true);
                        caughtUp=true;
                    }
                    await BeginOwedRepair(m,ct);
                    await ReplayJournal(m,ct);
                    await m.Runner.RunAsync(m.Pair,settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,store!,ct);
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
            if(trim!=null) volume.Work=TrimAsync();
        }
        async Task TrimAsync()
        {
            var token=shutdown.Token;
            try {await trim!(Path.GetPathRoot(m.Pair.LocalRoot)!,volume.Type,token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested) { }
            catch(Exception error) {await Failed(m,error,token);}
        }
    }
    private async Task<JournalPosition?> ReplayJournal(HostMember m,CancellationToken ct)
    {
        // The Local reader (or the gap between readers) consumes each latched completion once.
        lock(m) if(m.RepairFinished is {IsCancellationRequested:true} signal){m.RepairFinished=null;signal.Dispose();}
        if(Volatile.Read(ref m.JournalWake) is not {} wake || (await store!.ReadRepairAsync(m.Pair.PairId,Side.Local,ct)).Reason!=null) return null;
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
        try {
            m.ManualStop?.Cancel();
            try { await Task.WhenAll(m.Manual,m.DebtWake); } catch(OperationCanceledException) when(m.ManualStop?.IsCancellationRequested==true) { }
            await m.Local.SetMembershipAsync(m.Pair,true,false,ct); await RecheckRoots(m,ct); m.Queue.RestartPrefix();
        } finally {Interlocked.Decrement(ref m.Closing);}
    }
    private async Task RecheckRoots(HostMember m,CancellationToken ct)
    {
        await Observe(m,await Configurations(ct),ct,true,retryRead:true); await Membership(m,false,ct);
        if(!m.Admitted) m.Stop?.Cancel();
    }
    internal void WakeRepair(HostMember m)
    {
        if(!m.Admitted || !m.Pair.Enabled || IsPaused(m.Pair.PairId) || m.Stop==null) return;
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
        var id=m.Pair.PairId; var state=await store!.RepairStateAsync(id,ct);
        var local=await store.ReadRepairAsync(id,Side.Local,ct); var remote=await store.ReadRepairAsync(id,Side.Dropbox,ct);
        var scopes=(await store.PairAsync(id,ct))?.OwedScopes;
        if(local.Reason!=null) m.Queue.LoseCoverage(Side.Local);
        if(remote.Reason!=null) m.Queue.LoseCoverage(Side.Dropbox);
        var full=!state.FirstRepairDone || local.Reason!=null || remote.Reason!=null;
        if(!full && scopes is not {Count:>0}) return;
        _=ClaimRepair(m,ct,Run,false);
        async Task Run(CancellationToken token)
        {
            if(full) await ManualRepair(m,token);
            else foreach(var scope in scopes!) await Repair.RunAsync(m.Pair,settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,store!,scope,null,token,caughtUp:await m.Boot.WaitAsync(token));
        }
    }
    internal Task ClaimRepair(HostMember m,CancellationToken ct,Func<CancellationToken,Task> work,bool explicitRequest)
    {
        lock(m)
        {
            if(m.Closing!=0) throw new OperationCanceledException(ct);
            if(!m.Manual.IsCompleted)
            {
                if(explicitRequest) throw new EngineRequestException(TextKey.RepairAlreadyRunning);
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
                catch(Exception error) when(!explicitRequest && !token.IsCancellationRequested) { await Failed(m,error,token); }
            },token);
            m.Manual=run.ContinueWith(static _=>{},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            return run;
        }
    }
    private RunLog NewLog(string id)
    {
        if (!PathNames.IsSingleSegment(id) || !PathNames.IsWindowsTransferPath(id)) throw Host.Unknown(id);
        return new(logs,Path.Combine(LogFolder,id + ResourceNames.LogNameSeparator + ResourceNames.NewId() + ResourceNames.LogExtension),settings);
    }
    private Task RetryDelay(Exception e,int failures,CancellationToken ct) => settings.Backoff.WaitAsync(
        e is ItemFailedException {ProviderWait:{} wait} provider && wait>TimeSpan.Zero
            ? wait-(provider.ProviderWaitStarted is {} start ? clock.GetElapsedTime(start) : TimeSpan.Zero)
            : settings.Backoff.RetryDelay(failures),clock,ct);
    private async Task Failed(HostMember m,Exception e,CancellationToken ct)
    {
        try { await store!.SaveNoticeAsync(m.Pair.PairId,
        new(0,NoticeKind.Failure,e switch {ItemFailedException i=>i.Cause,EngineRequestException request=>request.Key,_=>TextKey.RootUnavailable},e is ItemFailedException f ? f.Path : string.Empty,
            new([],null),clock.GetUtcNow(),false),ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception error) { m.Event(new(RunEventKind.Failed,nameof(IStore.SaveNoticeAsync),string.Empty,error.Message,string.Empty){Error=error}); }
    }
    internal Task Membership(HostMember m,bool removed,CancellationToken ct) => m.Local==null || m.Pair.Roots==null ? Task.CompletedTask : m.Local.SetMembershipAsync(m.Pair,!m.Pair.Enabled || !m.Admitted || IsPaused(m.Pair.PairId),removed,ct);
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
    internal async Task ManualRepair(HostMember m,CancellationToken ct)
    {
        using var log=NewLog(m.Pair.PairId);
        m.OperationLog.Value=log.Write;
        await Repair.RunAsync(m.Pair,settings,log.Write,m.Paths,m.Queue,m.Local,m.Dropbox,store!,null,null,ct,caughtUp:await m.Boot.WaitAsync(ct),localRepaired:()=>
        {lock(m) if(m.JournalWake!=null) (m.RepairFinished??=new()).Cancel();});
    }
}
