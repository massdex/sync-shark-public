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

    /// <summary>Compose cancellable per-pair adapters, including initial namespace resolution, inside the pair failure boundary.</summary>
    public Host(Func<bool,CancellationToken,Task<IStore?>> openStore,
        Func<PairConfig,IStore,Func<PathScope>,Action<RunEvent>,CancellationToken,Task<(ISide Local,ISide Dropbox)>> sides,
        EngineSettings settings,ILogFiles logs,TimeProvider clock,Func<CancellationToken,Task<string>> connect,Func<string,CancellationToken,Task> disconnect)
        : this(openStore,(_,_,_,_) => throw new InvalidOperationException(),settings,logs,clock,connect,disconnect) => openSides=sides;

    private sealed class Member(PairConfig pair)
    {
        internal PairConfig Pair = pair;
        internal readonly PairRunner Runner = new();
        internal WorkQueue Queue = new();
        internal PathScope Paths = null!;
        internal ISide Local = null!, Dropbox = null!;
        internal RunLog? Log;
        internal DriveType Type;
        internal bool Admitted;
        internal CancellationTokenSource? Stop;
        internal Task Live = Task.CompletedTask, Boot = Task.CompletedTask, Manual = Task.CompletedTask;
        internal CancellationTokenSource? ManualStop;
        internal void Event(RunEvent value) => Log?.Write(value);
    }

    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly ConcurrentDictionary<string,Member> members = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,byte> paused = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopped = new();
    private volatile bool allPaused, refused;
    private string? startupMessage;
    private IStore? store;
    private static string LogFolder => ResourceNames.LogDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Signals the pipe owner only after callbacks have drained.</summary>
    public CancellationToken Stopping => stopped.Token;
    /// <summary>A refused switch serves status only; a warning after publication still permits startup.</summary>
    public void SetStartupMessage(string message, bool refused) { startupMessage = message; this.refused = refused; }

    /// <summary>Attach every member before acquisition; await the finite pass, never the live feed.</summary>
    public async Task<IReadOnlyList<(string VolumeRoot,DriveType Type)>> StartAsync(CancellationToken ct)
    {
        Member[] snapshot;
        await commands.WaitAsync(ct);
        try
        {
            if (await Open(false,ct) == null) return [];
            var pairs = await Configurations(ct);
            var fresh = pairs.Where(p => !members.ContainsKey(p.PairId)).Select(Attach).ToArray();
            await Task.WhenAll(fresh.Select(m => Observe(m,pairs,ct)));
            await Task.WhenAll(fresh.Select(m => Membership(m,false,ct)));
            foreach (var m in fresh) Start(m);
            snapshot = members.Values.ToArray();
        }
        finally { commands.Release(); }
        await Task.WhenAll(snapshot.Select(m => m.Boot)).WaitAsync(ct);
        return snapshot.Where(m => m.Type != DriveType.Unknown).Select(m => (Path.GetPathRoot(m.Pair.LocalRoot)!,m.Type)).Distinct().ToArray();
    }
    private Member Attach(PairConfig p)
    {
        var m = new Member(p); m.Queue.Unfinished=m.Runner.Unfinished;
        members[p.PairId] = m;
        return m;
    }
    private async Task Observe(Member m,IReadOnlyList<PairConfig> pairs,CancellationToken ct)
    {
        m.Admitted = false;
        try
        {
            var (roots,drive,type) = await ReadRoots(m,ct);
            m.Type = type; m.Paths = new(m.Pair,pairs,type,settings.DefaultIgnoreRules);
            m.Admitted = await m.Runner.CheckRootsAsync(m.Pair,roots,drive,store!,ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { await Failed(m,e,ct); }
    }
    private async Task Adapters(Member m,CancellationToken ct)
    {
        if (m.Local==null) (m.Local,m.Dropbox)=await openSides(m.Pair,store!,()=>m.Paths,m.Event,ct);
    }
    private async Task<(RootIdentity? Roots,DriveEvidence? Drive,DriveType Type)> ReadRoots(Member m,CancellationToken ct)
    {
        await Adapters(m,ct);
        var pc = m.Local.RootAsync(ct); var cloud = m.Dropbox.RootAsync(ct); await Task.WhenAll(pc,cloud);
        var a = await pc; var b = await cloud; var p = m.Pair;
        return (a.Local != null && b.Dropbox != null ? new(p.LocalRoot,p.DropboxRoot,p.ConnectionId,a.Local,b.Dropbox) : null,
            a.Drive,a.LocalDriveType ?? DriveType.Unknown);
    }
    private void Start(Member m)
    {
        if (!m.Admitted || !m.Pair.Enabled || IsPaused(m.Pair.PairId) || m.Stop != null || stopped.IsCancellationRequested) return;
        var stop = m.Stop = new(); m.Queue = new() { Unfinished = m.Runner.Unfinished };
        var boot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Boot = boot.Task; m.Live = Run();
        async Task Run()
        {
            try
            {
                var ct = stop.Token; var id = m.Pair.PairId;
                m.Log=NewLog(id);
                var pc = m.Local.BoundaryAsync(ct); var cloud = m.Dropbox.BoundaryAsync(ct); await Task.WhenAll(pc,cloud);
                await m.Runner.CatchUpAsync(m.Pair,((await pc).Position,(await cloud).Cursor),settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,store!,ct);
                boot.TrySetResult();
                await m.Runner.RunAsync(m.Pair,settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,store!,ct);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { await Failed(m,e,CancellationToken.None); }
            finally { m.Log?.Dispose(); m.Log=null; boot.TrySetResult(); }
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
    private Task Membership(Member m,bool removed,CancellationToken ct) => m.Local==null ? Task.CompletedTask : m.Local.SetMembershipAsync(m.Pair,!m.Pair.Enabled || !m.Admitted || IsPaused(m.Pair.PairId),removed,ct);
    private static async Task Drain(Member m)
    {
        m.Stop?.Cancel(); m.ManualStop?.Cancel();
        try { await Task.WhenAll(m.Live,m.Manual); }
        catch (OperationCanceledException) when (m.Stop?.IsCancellationRequested==true || m.ManualStop?.IsCancellationRequested==true) { }
        finally { m.Stop?.Dispose(); m.Stop=null; m.ManualStop?.Dispose(); m.ManualStop=null; m.Manual=Task.CompletedTask; m.Log?.Dispose(); m.Log=null; }
    }
    /// <summary>Stop admitting work and keep the pipe reachable until every runner drains.</summary>
    public Task StopAsync(CancellationToken ct) => Command(async () =>
    { await Task.WhenAll(members.Values.Select(Drain)); members.Clear(); store?.Dispose(); store=null; stopped.Cancel(); },ct);
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
        await Task.WhenAll(Selected(pairId).Where(m => !IsPaused(m.Pair.PairId) && m.Stop==null).Select(async m => { await Observe(m,pairs,ct); await Membership(m,false,ct); Start(m); }));
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
    private async Task<PairConfig> Required(string id,CancellationToken ct) => await Open(false,ct) is { } db && await db.PairAsync(id,ct) is { } p ? p : throw Unknown(id);
    /// <summary>List stored configurations without creating storage.</summary>
    public async IAsyncEnumerable<PairConfig> PairsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await commands.WaitAsync(ct);
        try { if (await Open(false,ct) is { } db) await foreach (var p in db.PairsAsync(ct)) yield return p; }
        finally { commands.Release(); }
    }
    /// <summary>Only explicit first add can create storage. Bind both actual roots before publishing.</summary>
    public Task AddPairAsync(PairConfig pair,CancellationToken ct) => Command(async () =>
    {
        if (!PathNames.IsDropboxRoot(pair.DropboxRoot)) throw new EngineRequestException(TextKey.DropboxPathInvalid,(TextArg.Path,pair.DropboxRoot));
        var db=await Open(false,ct) ?? await Open(true,ct) ?? throw new EngineRequestException(TextKey.StatusUnavailable);
        if (await db.PairAsync(pair.PairId,ct) != null) throw new EngineRequestException(TextKey.ResolveStale);
        var all=await Configurations(ct); all.Add(pair); var m=Attach(pair);
        try
        {
            var (roots,drive,type)=await ReadRoots(m,ct);
            if (roots?.IsValid() != true || type==DriveType.Unknown) throw new EngineRequestException(TextKey.RootUnavailable);
            m.Pair=pair with {Roots=roots,Drive=drive}; m.Type=type; m.Paths=new(m.Pair,all,type,settings.DefaultIgnoreRules);
            await db.SavePairAsync(m.Pair,ct); m.Admitted=true;
        }
        catch { members.TryRemove(pair.PairId,out _); throw; }
        await Membership(m,false,ct); await RefreshParents(pair,ct); Start(m);
    },ct);
    private static string? Under(PairConfig parent,PairConfig child,Side side) => side == Side.Local ? PathNames.RelativeUnderLocalRoot(parent.LocalRoot,child.LocalRoot) :
        parent.ConnectionId==child.ConnectionId ? PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(parent.DropboxRoot),PathNames.TrimDropboxRoot(child.DropboxRoot)) : null;
    private async Task RefreshParents(PairConfig child,CancellationToken ct)
    {
        var pairs=await Configurations(ct);
        await Task.WhenAll(members.Values.Where(m => m.Pair.PairId!=child.PairId && (Under(m.Pair,child,Side.Local)!=null || Under(m.Pair,child,Side.Dropbox)!=null))
            .Select(async m => { await Drain(m); await Observe(m,pairs,ct); await Membership(m,false,ct); Start(m); }));
    }
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
                var rel=owner.rel!; var binding=await store!.FindAsync(owner.q.PairId,side,PathNames.KeyFor(side,rel),ct);
                owed.Add((owner.q.PairId,new(binding?.LocalPath ?? rel,binding?.DropboxPath ?? rel)));
            }
            if (members.TryGetValue(pairId,out var m)) await Drain(m);
            cause=await store!.RemovePairAsync(pairId,ct,owed.ToArray());
            if (cause!=null) { if (m!=null) Start(m); return; }
            if (m!=null) await Membership(m,true,ct);
            members.TryRemove(pairId,out _); paused.TryRemove(pairId,out _); await RefreshParents(p,ct);
        },ct); return cause;
    }
    /// <summary>Re-admit only when both observed roots still match the saved identities.</summary>
    public Task BindAsync(string pairId,CancellationToken ct) => Command(async () =>
    {
        var p=await Required(pairId,ct); var m=members.TryGetValue(pairId,out var found) ? found : Attach(p);
        await Drain(m); await Observe(m,await Configurations(ct),ct);
        if (!m.Admitted) throw new EngineRequestException(TextKey.PairAwaitingDecision,(TextArg.Pair,pairId)); Start(m);
    },ct);
    /// <summary>Validate the path before provider access; preview and apply use the executor's eligibility.</summary>
    public async Task<(bool Applied,long CopyFiles,long CopyBytes,long RemoveFiles,long RemoveBytes,string? RecoveryPlace,TextKey? Cause)> SetExclusionAsync(string pairId,Exclusion exclusion,bool include,bool confirmed,CancellationToken ct)
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
            await Change(m,next,include ? [new(exclusion.Path,exclusion.Path)] : [],ct); return (true,0,0,0,0,null,null);
        }
        finally { commands.Release(); }
    }
    private async Task Change(Member m,PairConfig next,IReadOnlyList<Scope> returned,CancellationToken ct,bool restart=true)
    {
        await Drain(m); var scope=new PathScope(next,await Configurations(ct),m.Type,settings.DefaultIgnoreRules);
        var owed=returned.Where(s => !restart || !scope.Excludes(s.LocalPath,s.DropboxPath,true)).Select(s => (next.PairId,s)).Distinct().ToArray();
        await store!.SavePairAsync(next,ct,owed); m.Pair=next; m.Paths=scope; if (restart) Start(m);
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
            var m=await Ready(await Required(pairId,ct),ct);
            if (!m.Manual.IsCompleted) throw new EngineRequestException(TextKey.RepairAlreadyRunning);
            m.ManualStop?.Dispose(); m.ManualStop=CancellationTokenSource.CreateLinkedTokenSource(ct);
            run=m.Manual=ManualRepair(m,m.ManualStop.Token);
        }
        finally { commands.Release(); }
        await run;
    }
    private async Task ManualRepair(Member m,CancellationToken ct)
    {
        using var log=NewLog(m.Pair.PairId);
        await Repair.RunAsync(m.Pair,settings,log.Write,m.Paths,m.Queue,m.Local,m.Dropbox,store!,null,null,ct);
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
            await store.SaveNoticeAsync(pairId,n with {Ignored=choice==ResolutionChoice.Ignore},ct); return;
        }
        if (n.Kind==NoticeKind.DriveChange && choice is ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally)
        {
            var member=members.TryGetValue(pairId,out var existing) ? existing : Attach(p);
            await Drain(member); var (roots,drive,_)=await ReadRoots(member,ct);
            if (roots?.IsValid()!=true || drive==null || p.Roots==null || roots.Dropbox!=p.Roots.Dropbox) throw new EngineRequestException(TextKey.RootUnavailable);
            var updated=p with {Roots=roots,Drive=drive};
            if (!await store.ResolveDriveAsync(updated,n,choice,ct)) throw new EngineRequestException(TextKey.ResolveStale);
            member.Pair=updated; await Observe(member,await Configurations(ct),ct); await Membership(member,false,ct); Start(member); return;
        }
        var m=await Ready(p,ct);
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
    { var affected=members.Values.Where(m=>m.Pair.ConnectionId==connectionId).ToArray(); foreach(var m in affected) paused[m.Pair.PairId]=0; await Task.WhenAll(affected.Select(async m=>{ await Drain(m); await Membership(m,false,ct); })); await disconnect(connectionId,ct); },ct);
    private async Task<(bool,long,long,long,long,string?,TextKey?)> Exclude(Member m,PairConfig next,Exclusion exclusion,bool confirmed,CancellationToken ct)
    {
        var executor=new Exclusions(); var v=await executor.PreviewAsync(m.Pair,exclusion,m.Local,m.Dropbox,store!,m.Paths,ct);
        var place=v.RemoveFiles==0 ? null : exclusion.Side==Side.Local && m.Type==DriveType.Removable ? Path.Combine(Path.GetPathRoot(m.Pair.LocalRoot)!,ResourceNames.RootBinFolder) : Text.Get(v.RecoveryPlace);
        var applied=false;
        if (confirmed)
        {
            await Change(m,next,[new(exclusion.Path,exclusion.Path)],ct,false);
            try { applied=await executor.ApplyAsync(next,settings,m.Event,m.Paths,exclusion,m.Local,m.Dropbox,store!,
                (s,side,f,token)=>Populate(m,s,side,f,token),(b,before,after,token)=>Compare(m,b,before,after,token),ct)==ExclusionOutcome.Applied; } finally { Start(m); }
        }
        return (applied,v.CopyFiles,v.CopyBytes,v.RemoveFiles,v.RemoveBytes,place,v.BlockedByProtected ? TextKey.FailExcludeHoldsProtected : null);
    }
}
