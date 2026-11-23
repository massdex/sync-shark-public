using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns the store, pair membership and the lifetime of the pair runners.</summary>
public sealed class Host(Func<bool,CancellationToken,Task<IStore?>> openStore,
    Func<PairConfig,IStore,Func<PathScope>,Action<RunEvent>,(ISide Local,ISide Dropbox)> sides,
    EngineSettings settings, ILogFiles logs, TimeProvider clock, Func<CancellationToken,Task<string>> connect,
    Func<string,CancellationToken,Task> disconnect)
{
    private readonly Func<PairConfig,IStore,Func<PathScope>,Action<RunEvent>,CancellationToken,Task<(ISide Local,ISide Dropbox)>> openSides =
        (pair,db,scope,events,_) => Task.FromResult(sides(pair,db,scope,events));

    private readonly Func<string,DriveType,CancellationToken,Task>? trim;
    /// <summary>One volume's startup trim, shared by its members. A pending bind never reports an attempt.</summary>
    private sealed class VolumeTrim
    {
        private readonly HashSet<string> pending=new(StringComparer.Ordinal);
        internal DriveType Type;
        internal Task Work=Task.CompletedTask;
        private VolumeTrim() { }
        public static Dictionary<string,VolumeTrim> Register(IEnumerable<PairConfig> pairs)
        {
            var byPair=new Dictionary<string,VolumeTrim>(StringComparer.Ordinal);
            foreach(var volume in pairs.Where(p=>p.Enabled).GroupBy(p=>Path.GetPathRoot(p.LocalRoot)!,PathNames.Comparer))
            {
                var group=new VolumeTrim();
                foreach(var p in volume) {group.pending.Add(p.PairId); byPair.Add(p.PairId,group);}
            }
            return byPair;
        }
        public bool Attempted(string pairId)
        {
            lock(pending)
            {
                return pending.Remove(pairId) && pending.Count==0;
            }
        }
    }
    private Dictionary<string,VolumeTrim>? startupVolumes;
    private readonly Action<PairConfig>? reserve;
    private readonly Func<PairConfig,CancellationToken,Task>? releaseReservation;
    private readonly CancellationTokenSource shutdown=new();

    /// <summary>Compose cancellable per-pair adapters, including initial namespace resolution, inside the pair failure boundary.</summary>
    public Host(Func<bool,CancellationToken,Task<IStore?>> openStore,
        Func<PairConfig,IStore,Func<PathScope>,Action<RunEvent>,CancellationToken,Task<(ISide Local,ISide Dropbox)>> sides,
        EngineSettings settings,ILogFiles logs,TimeProvider clock,Func<CancellationToken,Task<string>> connect,Func<string,CancellationToken,Task> disconnect, Func<string,DriveType,CancellationToken,Task>? trim=null, Action<PairConfig>? reserve=null,Func<PairConfig,CancellationToken,Task>? releaseReservation=null)
        : this(openStore,(_,_,_,_) => throw new InvalidOperationException(),settings,logs,clock,connect,disconnect) { openSides=sides; this.trim=trim; this.reserve=reserve; this.releaseReservation=releaseReservation; }

    private sealed class Member(PairConfig pair)
    {
        internal PairConfig Pair = pair;
        internal readonly PairRunner Runner = new();
        internal WorkQueue Queue = null!;
        internal PathScope Paths = null!;
        internal ISide Local = null!, Dropbox = null!;
        internal RunLog? Log;
        internal DriveType Type;
        internal VolumeTrim? Trim;
        internal int Closing;
        internal bool Admitted;
        internal CancellationTokenSource? Stop;
        internal Task Live = Task.CompletedTask, Manual = Task.CompletedTask;
        internal CancellationTokenSource? ManualStop, Opening;
        internal Task Setup=Task.CompletedTask, DebtWake=Task.CompletedTask;
        internal bool DebtChanged;
        internal readonly AsyncLocal<Action<RunEvent>?> OperationLog=new();
        internal void Event(RunEvent value) { if(OperationLog.Value is {} sink) sink(value); else Log?.Write(value); }
    }

    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly ConcurrentDictionary<string,Member> members = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,byte> paused = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopped = new();
    private volatile bool allPaused, refused;
    private string? startupMessage;
    private IStore? store;
    private IReadOnlyList<PairConfig>? publishedPairs;
    private static string LogFolder => ResourceNames.LogDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Signals the pipe owner only after callbacks have drained.</summary>
    public CancellationToken Stopping => stopped.Token;
    /// <summary>A refused switch serves status only; a warning after publication still permits startup.</summary>
    public void SetStartupMessage(string message, bool refused) { startupMessage = message; this.refused = refused; }

    /// <summary>Reserve startup members before independent bindings. Trim a volume after its last member attempts finite replay, not after settlement.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await commands.WaitAsync(ct);
        try
        {
            if (await Open(false,ct)==null) return;
            var pairs=await Configurations(ct);
            lock(members) publishedPairs=pairs;
            foreach(var p in pairs) { reserve?.Invoke(p); if(!members.ContainsKey(p.PairId)) Attach(p); }
            if(startupVolumes==null)
            {
                startupVolumes=VolumeTrim.Register(pairs);
                foreach(var (id,group) in startupVolumes) members[id].Trim=group;
            }
            foreach(var p in pairs)
            {
                var m=members.TryGetValue(p.PairId,out var existing) ? existing : Attach(p);
                if((m.Stop==null || m.Live.IsCompleted) && m.Setup.IsCompleted && !IsPaused(p.PairId)) Launch(m,pairs,ct);
            }
        }
        finally { commands.Release(); }
    }
    private void Launch(Member m,IReadOnlyList<PairConfig> pairs,CancellationToken ct)
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
                await Observe(m,pairs,token); token.ThrowIfCancellationRequested();
                await Membership(m,false,token); token.ThrowIfCancellationRequested();
                Start(m);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { }
            catch(Exception error) {m.Admitted=false; await Failed(m,error,CancellationToken.None);}
        });
    }
    private Member Attach(PairConfig p)
    {
        var m = new Member(p); m.Queue=new(ct=>m.Local.SettledJournalAsync(ct)){Unfinished=m.Runner.Unfinished,ScopeNow=()=>m.Paths,WakeOwedRepair=()=>WakeRepair(m)};
        members[p.PairId] = m;
        return m;
    }
    private async Task Observe(Member m,IReadOnlyList<PairConfig> pairs,CancellationToken ct,bool liveRootSignal=false)
    {
        m.Admitted = false;
        try
        {
            var (roots,drive,type) = await ReadRoots(m,ct);
            lock(members) {m.Type=type; m.Paths=new(m.Pair,publishedPairs ?? pairs,type,settings.DefaultIgnoreRules);}
            m.Admitted = await m.Runner.CheckRootsAsync(m.Pair,roots,drive,store!,ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            if(liveRootSignal) await store!.SaveNoticeAsync(m.Pair.PairId,new(0,NoticeKind.RootIdentity,
                e is ItemFailedException failure ? failure.Cause : TextKey.RootUnavailable,string.Empty,new([],null),clock.GetUtcNow(),false),ct);
            else await Failed(m,e,ct);
        }
    }
    private async Task Adapters(Member m,CancellationToken ct)
    {
        if (m.Local==null) (m.Local,m.Dropbox)=await openSides(m.Pair,store!,()=>m.Paths,m.Event,ct);
    }
    private async Task<(RootIdentity? Roots,DriveEvidence? Drive,DriveType Type)> ReadRoots(Member m,CancellationToken ct,bool acceptDrive=false)
    {
        await Adapters(m,ct);
        var a=await m.Local.RootAsync(ct); var p=m.Pair;
        if(a.Drive!=null && DriveEvidence.Change(p.Drive,a.Drive)!=null && !acceptDrive)
            return (null,a.Drive,a.LocalDriveType ?? DriveType.Unknown);
        var b=await m.Dropbox.RootAsync(ct);
        return (a.Local != null && b.Dropbox != null ? new(p.LocalRoot,p.DropboxRoot,p.ConnectionId,a.Local,b.Dropbox) : null,
            a.Drive,a.LocalDriveType ?? DriveType.Unknown);
    }
    private void Start(Member m)
    {
        if (!m.Admitted || !m.Pair.Enabled || IsPaused(m.Pair.PairId) || m.Stop != null || stopped.IsCancellationRequested) return;
        if(Volatile.Read(ref m.Closing)!=0) return;
        var stop = m.Stop = CancellationTokenSource.CreateLinkedTokenSource(m.Opening?.Token ?? CancellationToken.None);
        m.Live=Run();
        void Attempted() { if(!stop.IsCancellationRequested) {WakeRepair(m); ReleaseTrim(m,false);} }
        async Task Run()
        {
            try
            {
                var ct = stop.Token; var id = m.Pair.PairId;
                m.Log=NewLog(id);
                var pc = m.Local.BoundaryAsync(ct); var cloud = m.Dropbox.BoundaryAsync(ct); await Task.WhenAll(pc,cloud);
                while(m.Admitted)
                {
                    try {await m.Runner.CatchUpAsync(m.Pair,((await pc).Position,(await cloud).Cursor),settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,store!,Attempted,ct);break;}
                    catch(ChangedUnderneathException error) when(PathNames.Same(error.Path,m.Pair.LocalRoot)) {await Rewind(m,ct);}
                }
                if(!m.Admitted) return;
                await BeginOwedRepair(m,ct);
                while(!ct.IsCancellationRequested && m.Admitted)
                {
                    try
                    {
                        await m.Runner.RunAsync(m.Pair,settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,store!,ct);
                        // The live reader returns at a reset; reevaluate the debt written by Monitor before continuing.
                        if(!m.Admitted || !await BeginOwedRepair(m,ct)) break;
                    }
                    catch(ChangedUnderneathException error) when(PathNames.Same(error.Path,m.Pair.LocalRoot))
                    {
                        await Rewind(m,ct);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { await Failed(m,e,CancellationToken.None); }
            finally { m.Log?.Dispose(); m.Log=null; }
        }
    }
    private void ReleaseTrim(Member m,bool removed)
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
    private async Task Rewind(Member m,CancellationToken ct)
    {
        Interlocked.Increment(ref m.Closing);
        try {
            m.ManualStop?.Cancel();
            try { await Task.WhenAll(m.Manual,m.DebtWake); } catch(OperationCanceledException) when(m.ManualStop?.IsCancellationRequested==true) { }
            await m.Local.SetMembershipAsync(m.Pair,true,false,ct); await RecheckRoots(m,ct); m.Queue.RestartPrefix();
        } finally {Interlocked.Decrement(ref m.Closing);}
    }
    private async Task RecheckRoots(Member m,CancellationToken ct)
    {
        await Observe(m,await Configurations(ct),ct,true); await Membership(m,false,ct);
        if(!m.Admitted) m.Stop?.Cancel();
    }
    private void WakeRepair(Member m)
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
    private async Task<bool> BeginOwedRepair(Member m,CancellationToken ct)
    {
        var id=m.Pair.PairId; var state=await store!.RepairStateAsync(id,ct);
        var local=await store.ReadRepairAsync(id,Side.Local,ct); var remote=await store.ReadRepairAsync(id,Side.Dropbox,ct);
        var scopes=(await store.PairAsync(id,ct))?.OwedScopes;
        if(local.Reason!=null) m.Queue.LoseCoverage(Side.Local);
        if(remote.Reason!=null) m.Queue.LoseCoverage(Side.Dropbox);
        var full=!state.FirstRepairDone || local.Reason!=null || remote.Reason!=null;
        if(!full && scopes is not {Count:>0}) return false;
        _=ClaimRepair(m,ct,Run,false); return true;
        async Task Run(CancellationToken token)
        {
            if(full) await ManualRepair(m,token);
            else foreach(var scope in scopes!) await Repair.RunAsync(m.Pair,settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,store!,scope,null,token,caughtUp:true);
        }
    }
    private Task ClaimRepair(Member m,CancellationToken ct,Func<CancellationToken,Task> work,bool explicitRequest)
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
            var token=m.ManualStop.Token; return m.Manual=Task.Run(async ()=>
            {
                try { await work(token); }
                catch(ChangedUnderneathException error) when(!token.IsCancellationRequested && PathNames.Same(error.Path,m.Pair.LocalRoot))
                {
                    await RecheckRoots(m,token);
                    if(explicitRequest) throw;
                }
                catch(Exception error) when(!explicitRequest && !token.IsCancellationRequested) { await Failed(m,error,token); }
            },token);
        }
    }
    private RunLog NewLog(string id)
    {
        if (!PathNames.IsSingleSegment(id) || !PathNames.IsWindowsTransferPath(id)) throw Unknown(id);
        return new(logs,Path.Combine(LogFolder,id + ResourceNames.LogNameSeparator + ResourceNames.NewId() + ResourceNames.LogExtension),settings);
    }
    private Task Failed(Member m,Exception e,CancellationToken ct) => store!.SaveNoticeAsync(m.Pair.PairId,
        new(0,NoticeKind.Failure,e is ItemFailedException i ? i.Cause : TextKey.RootUnavailable,e is ItemFailedException f ? f.Path : string.Empty,
            new([],null),clock.GetUtcNow(),false),ct);
    private Task Membership(Member m,bool removed,CancellationToken ct) => m.Local==null || m.Pair.Roots==null ? Task.CompletedTask : m.Local.SetMembershipAsync(m.Pair,!m.Pair.Enabled || !m.Admitted || IsPaused(m.Pair.PairId),removed,ct);
    private static async Task Drain(Member m)
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
            m.Queue.RestartPrefix(); m.Opening?.Dispose(); m.Opening=null; m.Stop?.Dispose(); m.Stop=null;
            m.ManualStop?.Dispose(); m.ManualStop=null; m.Manual=Task.CompletedTask; m.Log?.Dispose(); m.Log=null;
            if(m.Local!=null && m.Pair.Roots!=null) await m.Local.SetMembershipAsync(m.Pair,true,false,CancellationToken.None);
        }
        } finally {Interlocked.Decrement(ref m.Closing);}
    }
    /// <summary>Stop admitting work and keep the pipe reachable until every runner drains.</summary>
    public Task StopAsync(CancellationToken ct) => Command(async () =>
    { shutdown.Cancel(); await Task.WhenAll(members.Values.Select(Drain)); if(startupVolumes!=null) await Task.WhenAll(startupVolumes.Values.Distinct().Select(v=>v.Work)); members.Clear(); store?.Dispose(); store=null; stopped.Cancel(); },ct);
    /// <summary>Pause the selected pair or all pairs in this session.</summary>
    public Task PauseAsync(string? pairId,CancellationToken ct) => Command(async () =>
    {
        if (pairId == null) allPaused=true; else paused[pairId]=0;
        await Task.WhenAll(Selected(pairId).Select(async m => { await Drain(m); await Membership(m,false,ct); }));
    },ct);
    /// <summary>Resume from settled source positions unless the other pause still applies.</summary>
    public Task ResumeAsync(string? pairId,CancellationToken ct) => Command(async () =>
    {
        if (pairId == null) allPaused=false; else paused.TryRemove(pairId,out _);
        var pairs=await Configurations(ct);
        foreach(var m in Selected(pairId).Where(m=>!IsPaused(m.Pair.PairId) && m.Stop==null && m.Setup.IsCompleted)) Launch(m,pairs,ct);
    },ct);
    private IEnumerable<Member> Selected(string? id) => id == null ? members.Values : members.TryGetValue(id,out var m) ? [m] : [];
    /// <summary>Global pause outranks a per-pair resume.</summary>
    public bool IsPaused(string pairId) => allPaused || paused.ContainsKey(pairId);
    private async Task Command(Func<Task> body,CancellationToken ct)
    { await commands.WaitAsync(ct); try { await body(); } finally { commands.Release(); } }
    private async Task<IStore?> Open(bool firstAdd,CancellationToken ct) => refused || stopped.IsCancellationRequested ? null : (store ??= await openStore(firstAdd,ct));
    private async Task<List<PairConfig>> Configurations(CancellationToken ct)
    {
        var pairs=new List<PairConfig>(); if (store != null) await foreach (var p in store.PairsAsync(ct)) pairs.Add(p); return pairs;
    }
    private static EngineRequestException Unknown(string id) => new(TextKey.PairUnknown,(TextArg.Pair,id));
    private async Task<PairConfig> Required(string id,CancellationToken ct) => (!members.TryGetValue(id,out var member) || Volatile.Read(ref member.Closing)==0) && await Open(false,ct) is { } db && await db.PairAsync(id,ct) is { } p ? p : throw Unknown(id);
    /// <summary>List stored configurations without creating storage.</summary>
    public async IAsyncEnumerable<PairConfig> PairsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await commands.WaitAsync(ct);
        try { if (await Open(false,ct) is { } db) await foreach (var p in db.PairsAsync(ct)) yield return p; }
        finally { commands.Release(); }
    }
    /// <summary>Only explicit first add can create storage. Bind both actual roots before publishing.</summary>
    public async Task AddPairAsync(PairConfig pair,CancellationToken ct)
    {
        if (!PathNames.IsDropboxRoot(pair.DropboxRoot)) throw new EngineRequestException(TextKey.DropboxPathInvalid,(TextArg.Path,pair.DropboxRoot));
        Member m; IStore db; CancellationToken token;
        await commands.WaitAsync(ct);
        try
        {
            db=await Open(false,ct) ?? await Open(true,ct) ?? throw new EngineRequestException(TextKey.StatusUnavailable);
            if(members.ContainsKey(pair.PairId) || await db.PairAsync(pair.PairId,ct)!=null) throw new EngineRequestException(TextKey.ResolveStale);
            m=Attach(pair); m.Opening=CancellationTokenSource.CreateLinkedTokenSource(ct); token=m.Opening.Token;
            m.Setup=Task.Run(async ()=>
            {
                var (roots,drive,type)=await ReadRoots(m,token);
                if(roots?.IsValid()!=true || type==DriveType.Unknown) throw new EngineRequestException(TextKey.RootUnavailable);
                m.Pair=pair with {Roots=roots,Drive=drive}; m.Type=type;

            });
        }
        finally {commands.Release();}
        try
        {
            await m.Setup; await commands.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested(); var all=await Configurations(token); all.Add(m.Pair);
                m.Paths=new(m.Pair,all,m.Type,settings.DefaultIgnoreRules);
                await PublishParents(pair,all,async ()=>{await db.SavePairAsync(m.Pair,token); return true;},token);
                // Register the bound adapters only after this pair exists in the store’s published membership.
                m.Local=null!; m.Dropbox=null!; await Adapters(m,token);
                m.Admitted=true; await Membership(m,false,token); Start(m);
            }
            finally {commands.Release();}
        }
        catch
        {
            await commands.WaitAsync(CancellationToken.None);
            try
            {
                if(members.TryGetValue(pair.PairId,out var current) && ReferenceEquals(m,current) && !m.Admitted)
                { members.TryRemove(pair.PairId,out _); await Membership(m,true,CancellationToken.None); m.Opening?.Dispose(); }
            }
            finally {commands.Release();}
            throw;
        }
    }
    // Callers own commands on entry and exit. Never hold it across an item's queue dependency.
    private async Task OutsideCommands(Func<Task> work)
    {
        commands.Release(); try {await work();} finally {await commands.WaitAsync(CancellationToken.None);}
    }
    private Task PublishParents(PairConfig child,IReadOnlyList<PairConfig> pairs,Func<Task<bool>> commit,CancellationToken ct)
    {
        var parents=members.Values.Where(x=>x.Pair.PairId!=child.PairId && (Under(x.Pair,child,Side.Local)!=null || Under(x.Pair,child,Side.Dropbox)!=null))
            .OrderBy(x=>x.Pair.PairId,StringComparer.Ordinal).ToArray();
        var before=publishedPairs;
        return OutsideCommands(()=>Publish(0));
        async Task Publish(int index)
        {
            if(index<parents.Length)
            {
                var outer=parents[index];var paths=new List<string>();
                foreach(var side in new[]{Side.Local,Side.Dropbox}) if(Under(outer.Pair,child,side) is {} rel)
                {var mapped=await Mapped(outer.Pair,side,rel,ct);paths.Add(mapped.LocalPath);paths.Add(mapped.DropboxPath);}
                await outer.Queue.PublishAsync(paths,()=>Publish(index+1),ct); return;
            }
            await Command(async ()=>
            {
                if(stopped.IsCancellationRequested || !ReferenceEquals(before,publishedPairs)) throw new EngineRequestException(TextKey.ResolveStale);
                if(!await commit()) return;
                lock(members) {publishedPairs=pairs; foreach(var outer in parents) outer.Paths=new(outer.Pair,pairs,outer.Type,settings.DefaultIgnoreRules);}
            },ct);
        }
    }
    private static string? Under(PairConfig parent,PairConfig child,Side side) => side == Side.Local ? PathNames.RelativeUnderLocalRoot(parent.LocalRoot,child.LocalRoot) :
        parent.ConnectionId==child.ConnectionId ? PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(parent.DropboxRoot),PathNames.TrimDropboxRoot(child.DropboxRoot)) : null;
    /// <summary>Remove atomically with the returned owner scopes; keep a refused pair intact.</summary>
    public async Task<TextKey?> RemovePairAsync(string pairId,CancellationToken ct)
    {
        TextKey? cause=null;
        await Command(async () =>
        {
            var p=await Required(pairId,ct); var all=await Configurations(ct); var owed=new HashSet<(string,Scope)>();
            foreach (var side in new[]{Side.Local,Side.Dropbox})
            {
                var owner=all.Where(q => q.Enabled && q.PairId!=pairId).Select(q => (q,rel:Under(q,p,side))).Where(x => !string.IsNullOrEmpty(x.rel)).OrderBy(x => PathNames.Depth(x.rel!)).FirstOrDefault();
                if (owner.q==null || !p.Enabled) continue;
                owed.Add((owner.q.PairId,await Mapped(owner.q,side,owner.rel!,ct)));
            }
            var m=members.TryGetValue(pairId,out var member)?member:Attach(p);
            if(Interlocked.Increment(ref m.Closing)!=1) {Interlocked.Decrement(ref m.Closing);throw new EngineRequestException(TextKey.ResolveStale);}
            bool committed=false;
            try {
                await Drain(m);
                await PublishParents(p,all.Where(x=>x.PairId!=pairId).ToArray(),async ()=>
                {cause=await store!.RemovePairAsync(pairId,ct,owed.ToArray());return committed=cause==null;},ct);
            } finally {
                if(committed) {
                    try {
                        if((m.Local==null || p.Roots==null) && releaseReservation!=null) await releaseReservation(p,CancellationToken.None);
                        else await Membership(m,true,CancellationToken.None);
                    } finally {
                        members.TryRemove(pairId,out _);paused.TryRemove(pairId,out _);ReleaseTrim(m,true);
                        foreach(var id in owed.Select(x=>x.Item1).Distinct()) if(members.TryGetValue(id,out var owner)) WakeRepair(owner);
                    }
                } else {Interlocked.Decrement(ref m.Closing);if(!stopped.IsCancellationRequested && !IsPaused(pairId)) Launch(m,all,CancellationToken.None);}
            }
        },ct); return cause;
    }
    private async Task<Scope> Mapped(PairConfig pair,Side side,string path,CancellationToken ct)
    {
        var binding=await store!.FindAsync(pair.PairId,side,PathNames.KeyFor(side,path),ct);
        if(binding!=null) return new(binding.LocalPath,binding.DropboxPath);
        var destination=await ItemSync.CounterpartAsync(pair,side,path,store,ct);
        return side==Side.Local ? new(path,destination) : new(destination,path);
    }
    /// <summary>Re-admit only when both observed roots still match the saved identities.</summary>
    public async Task BindAsync(string pairId,CancellationToken ct)
    {
        Member m; Task drain;
        await commands.WaitAsync(ct);
        try {var p=await Required(pairId,ct);m=members.TryGetValue(pairId,out var existing)?existing:Attach(p);drain=Drain(m);}
        finally {commands.Release();}
        await drain;
        Task setup;
        await commands.WaitAsync(ct);
        try
        {
            if(stopped.IsCancellationRequested || Volatile.Read(ref m.Closing)!=0 || !members.TryGetValue(pairId,out var current) || current!=m) throw Unknown(pairId);
            if(m.Setup.IsCompleted && m.Stop==null) Launch(m,await Configurations(ct),ct);
            setup=m.Setup;
        }
        finally {commands.Release();}
        await setup.WaitAsync(ct);
        if(!m.Admitted) throw new EngineRequestException(TextKey.PairAwaitingDecision,(TextArg.Pair,pairId));
    }
    /// <summary>Validate the path before provider access; preview and apply use the executor's eligibility.</summary>
    public async Task<(ExclusionOutcome? Outcome,long CopyFiles,long CopyBytes,long RemoveFiles,long RemoveBytes,string? RecoveryPlace,TextKey? Cause)> SetExclusionAsync(string pairId,Exclusion exclusion,bool include,bool confirmed,CancellationToken ct)
    {
        try { if (Path.IsPathRooted(exclusion.Path)) throw new ArgumentException(); exclusion=exclusion with {Path=PathNames.NormalizeRelative(exclusion.Path)}; }
        catch (ArgumentException e) { throw new EngineRequestException(TextKey.ExclusionPathInvalid,(TextArg.Path,exclusion.Path),(TextArg.Message,e.Message)); }
        await commands.WaitAsync(ct);
        try
        {
            var p=await Required(pairId,ct);
            Member m;
            if (!include && !exclusion.Keep && !confirmed)
            {
                m=members.TryGetValue(pairId,out var existing) ? existing : Attach(p);
                if (m.Paths==null)
                {
                    await Adapters(m,ct);
                    m.Type=(await m.Local.RootAsync(ct)).LocalDriveType ?? DriveType.Unknown;
                    m.Paths=new(p,await Configurations(ct),m.Type,settings.DefaultIgnoreRules);
                }
            }
            else m=await Ready(p,ct);
            var list=p.Exclusions.Where(e => e.Side!=exclusion.Side || !PathNames.Same(e.Path,exclusion.Path)).ToList(); if (!include) list.Add(exclusion);
            var next=p with {Exclusions=list};
            if (!include && !exclusion.Keep) return await Exclude(m,next,exclusion,confirmed,ct);
            var mapped=await Mapped(p,exclusion.Side,exclusion.Path,ct); await Change(m,next,include ? [mapped] : [],ct,mapped); return (ExclusionOutcome.Applied,0,0,0,0,null,null);
        }
        finally { commands.Release(); }
    }
    private async Task Change(Member m,PairConfig next,IReadOnlyList<Scope> returned,CancellationToken ct,Scope? footprint=null,bool excluding=false)
    {
        var scope=new PathScope(next,await Configurations(ct),m.Type,settings.DefaultIgnoreRules);
        var owed=returned.Where(s => excluding || !scope.Excludes(s.LocalPath,s.DropboxPath,true)).Select(s => (next.PairId,s)).Distinct().ToArray();
        var expected=m.Pair; var before=publishedPairs;
        await OutsideCommands(()=>m.Queue.PublishAsync(footprint==null ? [string.Empty] : [footprint.LocalPath,footprint.DropboxPath],()=>Command(async ()=>
        {
            if(!members.TryGetValue(next.PairId,out var current) || current!=m || !ReferenceEquals(m.Pair,expected) || !ReferenceEquals(before,publishedPairs))
                throw new EngineRequestException(TextKey.ResolveStale);
            await store!.SavePairAsync(next,ct,owed); lock(members){m.Pair=next; m.Paths=scope;}
        },ct),ct));
        if(owed.Length!=0) WakeRepair(m);
    }
    /// <summary>Retain bindings and request a comparison if an ignore rule was lifted.</summary>
    public Task SetIgnoresAsync(string pairId,IReadOnlyList<string> rules,CancellationToken ct) => Command(async () =>
    {
        var p=await Required(pairId,ct); var m=await Ready(p,ct);
        var edited=IgnoreRule.Normalize(rules.SelectMany(IgnoreRule.ParseForEdit).ToArray()).Select(IgnoreRule.Format).ToArray();
        bool broadened=IgnoreRule.Normalize(p.IgnoreRules.SelectMany(IgnoreRule.Parse).ToArray()).Select(IgnoreRule.Format).Except(edited).Any();
        await Change(m,p with {IgnoreRules=edited},broadened ? [new("","")] : [],ct);
    },ct);
    private async Task<Member> Ready(PairConfig p,CancellationToken ct)
    {
        if (!members.TryGetValue(p.PairId,out var m)) m=Attach(p);
        if(!m.Setup.IsCompleted && !m.Admitted) throw new EngineRequestException(TextKey.RootUnavailable);
        if (m.Stop==null || !m.Admitted) await Observe(m,await Configurations(ct),ct);
        if (!m.Admitted) throw new EngineRequestException(TextKey.PairAwaitingDecision,(TextArg.Pair,p.PairId)); return m;
    }
    /// <summary>Run explicit Repair on the pair's shared queue.</summary>
    public async Task RepairAsync(string pairId,CancellationToken ct)
    {
        Task run;
        await commands.WaitAsync(ct);
        try
        {
            var p=await Required(pairId,ct);
            if(!p.Enabled || IsPaused(pairId)) throw new EngineRequestException(TextKey.PairDisabled,(TextArg.Pair,pairId));
            var m=await Ready(p,ct);
            run=ClaimRepair(m,ct,async token=>{
                await store!.OweRepairAsync(pairId,Side.Dropbox,"requested",token); await ManualRepair(m,token);
            },true);
        }
        finally { commands.Release(); }
        await run;
    }
    private async Task ManualRepair(Member m,CancellationToken ct)
    {
        using var log=NewLog(m.Pair.PairId);
        m.OperationLog.Value=log.Write;
        await Repair.RunAsync(m.Pair,settings,log.Write,m.Paths,m.Queue,m.Local,m.Dropbox,store!,null,null,ct,caughtUp:m.Stop!=null && !m.Stop.IsCancellationRequested);
    }
    /// <summary>Read stored status and any startup switch refusal or warning.</summary>
    public async Task<string> StatusAsync(string? pairId,CancellationToken ct)
    {
        await commands.WaitAsync(ct);
        try
        {
            if (refused) return startupMessage ?? Text.Get(TextKey.StatusUnavailable);
            var result=await Open(false,ct) is { } db ? await new Status().ReadAsync(db,pairId,ct) : Text.Get(TextKey.NoPairsYet);
            return startupMessage==null ? result : startupMessage+Environment.NewLine+result;
        }
        finally { commands.Release(); }
    }
    /// <summary>Expose the exact reviewed notice, never just a reusable numeric ID.</summary>
    public async IAsyncEnumerable<Notice> NoticesAsync(string pairId,[EnumeratorCancellation] CancellationToken ct)
    {
        await commands.WaitAsync(ct);
        try { if (await Open(false,ct) is { } db) await foreach (var n in db.NoticesAsync(pairId,ct)) yield return Project(n); }
        finally { commands.Release(); }
    }
    private static Notice Project(Notice n) => n with {Choices=Resolutions.Choices(n),Reviewed=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(n with {Reviewed=null})))};
    /// <summary>Apply only an allowed choice to the same version the caller reviewed.</summary>
    public Task ResolveAsync(string pairId,long id,string reviewed,ResolutionChoice choice,string? name,CancellationToken ct) => Command(async () =>
    {
        var p=await Required(pairId,ct); var n=await store!.NoticeAsync(pairId,id,ct);
        if (n==null || Project(n).Reviewed!=reviewed) throw new EngineRequestException(TextKey.ResolveStale);
        if (!Resolutions.Choices(n).Contains(choice)) throw new EngineRequestException(TextKey.ResolveBadChoice);
        if (choice is ResolutionChoice.Ignore or ResolutionChoice.Unignore)
        {
            if(!await store.UpdateNoticeVisibilityAsync(pairId,n,choice==ResolutionChoice.Ignore,ct)) throw new EngineRequestException(TextKey.ResolveStale);
            return;
        }
        if (n.Kind==NoticeKind.DriveChange && choice is ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally)
        {
            var member=members.TryGetValue(pairId,out var existing) ? existing : Attach(p);
            await Drain(member); var (roots,drive,_)=await ReadRoots(member,ct,true);
            if (roots?.IsValid()!=true || drive==null || p.Roots==null || roots.Dropbox!=p.Roots.Dropbox) throw new EngineRequestException(TextKey.RootUnavailable);
            var updated=p with {Roots=roots,Drive=drive};
            if (!await store.ResolveDriveAsync(updated,n,choice,ct)) throw new EngineRequestException(TextKey.ResolveStale);
            await Membership(member,true,ct); member.Local=null!; member.Dropbox=null!; member.Pair=updated; await Observe(member,await Configurations(ct),ct); await Membership(member,false,ct); Start(member); return;
        }
        var m=await Ready(p,ct);
        if(choice==ResolutionChoice.Retry)
        {
            var cloud=n.Subject.Items.FirstOrDefault(i=>i.Side==Side.Dropbox && PathNames.Same(i.Path,n.Path)) ?? n.Subject.Items.FirstOrDefault(i=>i.Side==Side.Dropbox);
            var endpoints=n.Subject.Items.Select(i=>(i.Side,i.Path)).DefaultIfEmpty((n.Subject.Incoming ?? Side.Local,n.Path));
            if(n.Subject.Move is {} move) endpoints=endpoints.Concat(new[]{move.Source,move.Destination,move.Intermediate}.OfType<string>().Select(path=>(Side.Dropbox,path)));
            var footprints=new List<Scope>();
            foreach(var (side,path) in endpoints.Distinct()) footprints.Add(await Mapped(p,side,path,ct));
            var received=new Change(Side.Dropbox,ChangeKind.Changed,cloud?.Path ?? footprints[0].DropboxPath,null,cloud?.Id,null,false,false);
            var held=m.Queue.RetryHeldAsync(null,ct,n);
            var jobs=m.Runner.Unfinished.CheckAsync(p,settings,m.Event,m.Paths,received,m.Local,m.Dropbox,store!,m.Queue.Held,
                (s,side,f,token)=>Populate(m,s,side,f,token),(b,before,after,token)=>Compare(m,b,before,after,token),ct);
            await Task.WhenAll(held,jobs);
            if(await held && (n.Kind!=NoticeKind.PutBack || await store!.NoticeAsync(pairId,id,ct) is not {} saved || !saved.Matches(n))) return;
            await new Retries(m.Runner.Unfinished).RetryAsync(p,settings,m.Event,m.Paths,n,m.Local,m.Dropbox,store!,m.Queue.Held,
                (s,side,f,token)=>Populate(m,s,side,f,token),(b,before,after,token)=>Compare(m,b,before,after,token),ct);
            return;
        }
        if (choice==ResolutionChoice.BindRoots) { await Membership(m,false,ct); Start(m); return; }
        await new Resolve().ApplyAsync(p,settings,m.Event,m.Paths,n,choice,name,m.Local,m.Dropbox,store!,
            (s,side,f,token)=>Populate(m,s,side,f,token),(b,before,after,token)=>Compare(m,b,before,after,token),ct);
    },ct);
    private Task Populate(Member m,Scope s,Side side,Recovery.Folder f,CancellationToken ct) => AutoSync.PopulateAsync(m.Pair,settings,m.Event,m.Paths,s,side,f,m.Local,m.Dropbox,store!,ct);
    private Task Compare(Member m,SyncedEntry b,Scope before,Scope after,CancellationToken ct) => Repair.RunAsync(m.Pair,settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,store!,null,(b,before,after),ct);
    /// <summary>Match a newly saved token to already-held news on the same pair.</summary>
    public Task InstalledAsync(AcceptedJob job,CancellationToken ct)
    {
        if (!members.TryGetValue(job.PairId,out var m)) throw Unknown(job.PairId);
        return m.Runner.Unfinished.InstalledAsync(job,m.Queue.Held,c=>m.Runner.Unfinished.CheckAsync(m.Pair,settings,m.Event,m.Paths,c,m.Local,m.Dropbox,store!,m.Queue.Held,
            (s,side,f,token)=>Populate(m,s,side,f,token),(b,before,after,token)=>Compare(m,b,before,after,token),ct));
    }
    /// <summary>Connect through the configured credential owner.</summary>
    public Task<string> ConnectAsync(CancellationToken ct) => connect(ct);
    /// <summary>Pause affected pairs before disconnecting; leave files and bindings intact.</summary>
    public Task DisconnectAsync(string connectionId,CancellationToken ct) => Command(async () =>
    { var affected=members.Values.Where(m=>m.Pair.ConnectionId==connectionId).ToArray(); foreach(var m in affected) paused[m.Pair.PairId]=0; await Task.WhenAll(affected.Select(async m=>{ await Drain(m); await Membership(m,false,ct); m.Local=null!; m.Dropbox=null!; m.Admitted=false; })); await disconnect(connectionId,ct); },ct);
    private async Task<(ExclusionOutcome?,long,long,long,long,string?,TextKey?)> Exclude(Member m,PairConfig next,Exclusion exclusion,bool confirmed,CancellationToken ct)
    {
        var executor=new Exclusions(); var v=await executor.PreviewAsync(m.Pair,exclusion,m.Local,m.Dropbox,store!,m.Paths,ct);
        var place=v.RemoveFiles==0 ? null : exclusion.Side==Side.Local && m.Type==DriveType.Removable ? Path.Combine(Path.GetPathRoot(m.Pair.LocalRoot)!,ResourceNames.RootBinFolder) : Text.Get(v.RecoveryPlace);
        ExclusionOutcome? outcome=null;
        if (confirmed)
        {
            var mapped=await Mapped(m.Pair,exclusion.Side,exclusion.Path,ct);
            await Change(m,next,[mapped],ct,mapped,true);
            outcome=await executor.ApplyAsync(await Required(next.PairId,ct),settings,m.Event,m.Paths,exclusion,m.Local,m.Dropbox,store!,
                (s,side,f,token)=>Populate(m,s,side,f,token),(b,before,after,token)=>Compare(m,b,before,after,token),ct,m.Queue.Held);
        }
        return (outcome,v.CopyFiles,v.CopyBytes,v.RemoveFiles,v.RemoveBytes,place,!confirmed && v.BlockedByProtected ? TextKey.FailExcludeHoldsProtected : null);
    }
}
