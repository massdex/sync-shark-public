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
    private Dictionary<string,VolumeTrim>? startupVolumes;
    private readonly Action<PairConfig>? reserve;
    private readonly Func<PairConfig,CancellationToken,Task>? releaseReservation;
    private readonly CancellationTokenSource shutdown=new();

    /// <summary>Compose cancellable per-pair adapters, including initial namespace resolution, inside the pair failure boundary.</summary>
    public Host(Func<bool,CancellationToken,Task<IStore?>> openStore,
        Func<PairConfig,IStore,Func<PathScope>,Action<RunEvent>,CancellationToken,Task<(ISide Local,ISide Dropbox)>> sides,
        EngineSettings settings,ILogFiles logs,TimeProvider clock,Func<CancellationToken,Task<string>> connect,Func<string,CancellationToken,Task> disconnect, Func<string,DriveType,CancellationToken,Task>? trim=null, Action<PairConfig>? reserve=null,Func<PairConfig,CancellationToken,Task>? releaseReservation=null)
        : this(openStore,(_,_,_,_) => throw new InvalidOperationException(),settings,logs,clock,connect,disconnect) { openSides=sides; this.trim=trim; this.reserve=reserve; this.releaseReservation=releaseReservation; }

    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly ConcurrentDictionary<string,HostMember> members = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,byte> paused = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopped = new();
    private volatile bool allPaused, refused;
    private string? startupMessage;
    private IStore? store;
    private IReadOnlyList<PairConfig>? publishedPairs;
    private HostLifecycle? life;
    private HostPairs? pairs;

    // Member lifetime and pair configuration act on this state; both views are stateless, so a racing second instance is equivalent.
    internal HostLifecycle Life => life ??= new(this);
    private HostPairs Pairs => pairs ??= new(this);
    internal IStore? Store => store;
    internal EngineSettings Settings => settings;
    internal ILogFiles Logs => logs;
    internal TimeProvider Clock => clock;
    internal SemaphoreSlim Commands => commands;
    internal ConcurrentDictionary<string,HostMember> Members => members;
    internal ConcurrentDictionary<string,byte> Paused => paused;
    internal CancellationTokenSource Stopped => stopped;
    internal CancellationTokenSource Shutdown => shutdown;
    internal IReadOnlyList<PairConfig>? PublishedPairs { get => publishedPairs; set => publishedPairs=value; }
    internal Func<string,DriveType,CancellationToken,Task>? Trim => trim;
    internal Func<PairConfig,CancellationToken,Task>? ReleaseReservation => releaseReservation;
    internal Func<PairConfig,IStore,Func<PathScope>,Action<RunEvent>,CancellationToken,Task<(ISide Local,ISide Dropbox)>> OpenSides => openSides;

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
            foreach(var p in pairs) { reserve?.Invoke(p); if(!members.ContainsKey(p.PairId)) Life.Attach(p); }
            if(startupVolumes==null)
            {
                startupVolumes=VolumeTrim.Register(pairs);
                foreach(var (id,group) in startupVolumes) members[id].Trim=group;
            }
            foreach(var p in pairs)
            {
                var m=members.TryGetValue(p.PairId,out var existing) ? existing : Life.Attach(p);
                if((m.Stop==null || m.Live.IsCompleted) && m.Setup.IsCompleted && !IsPaused(p.PairId)) Life.Launch(m,pairs,ct);
            }
        }
        finally { commands.Release(); }
    }
    /// <summary>Stop admitting work and keep the pipe reachable until every runner drains.</summary>
    public Task StopAsync(CancellationToken ct) => Command(async () =>
    { shutdown.Cancel(); await Task.WhenAll(members.Values.Select(HostLifecycle.Drain)); if(startupVolumes!=null) await Task.WhenAll(startupVolumes.Values.Distinct().Select(v=>v.Work)); members.Clear(); store?.Dispose(); store=null; stopped.Cancel(); },ct);
    /// <summary>Pause the selected pair or all pairs in this session.</summary>
    public Task PauseAsync(string? pairId,CancellationToken ct) => Command(async () =>
    {
        if (pairId == null) allPaused=true; else paused[pairId]=0;
        await Task.WhenAll(Selected(pairId).Select(async m => { await HostLifecycle.Drain(m); await Life.Membership(m,false,ct); }));
    },ct);
    /// <summary>Resume from settled source positions unless the other pause still applies.</summary>
    public Task ResumeAsync(string? pairId,CancellationToken ct) => Command(async () =>
    {
        if (pairId == null) allPaused=false; else paused.TryRemove(pairId,out _);
        var pairs=await Configurations(ct);
        foreach(var m in Selected(pairId).Where(m=>!IsPaused(m.Pair.PairId) && m.Stop==null && m.Setup.IsCompleted)) Life.Launch(m,pairs,ct);
    },ct);
    private IEnumerable<HostMember> Selected(string? id) => id == null ? members.Values : members.TryGetValue(id,out var m) ? [m] : [];
    /// <summary>Global pause outranks a per-pair resume.</summary>
    public bool IsPaused(string pairId) => allPaused || paused.ContainsKey(pairId);
    internal async Task Command(Func<Task> body,CancellationToken ct)
    { await commands.WaitAsync(ct); try { await body(); } finally { commands.Release(); } }
    internal async Task<IStore?> Open(bool firstAdd,CancellationToken ct) => refused || stopped.IsCancellationRequested ? null : (store ??= await openStore(firstAdd,ct));
    internal async Task<List<PairConfig>> Configurations(CancellationToken ct)
    {
        var pairs=new List<PairConfig>(); if (store != null) await foreach (var p in store.PairsAsync(ct)) pairs.Add(p); return pairs;
    }
    internal static EngineRequestException Unknown(string id) => new(TextKey.PairUnknown,(TextArg.Pair,id));
    internal async Task<PairConfig> Required(string id,CancellationToken ct) => (!members.TryGetValue(id,out var member) || Volatile.Read(ref member.Closing)==0) && await Open(false,ct) is { } db && await db.PairAsync(id,ct) is { } p ? p : throw Unknown(id);
    /// <summary>List stored configurations without creating storage.</summary>
    public async IAsyncEnumerable<PairConfig> PairsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await commands.WaitAsync(ct);
        try { if (await Open(false,ct) is { } db) await foreach (var p in db.PairsAsync(ct)) yield return p; }
        finally { commands.Release(); }
    }
    /// <summary>Only explicit first add can create storage. Bind both actual roots before publishing.</summary>
    public Task AddPairAsync(PairConfig pair,CancellationToken ct) => Pairs.AddPairAsync(pair,ct);
    // Callers own commands on entry and exit. Never hold it across an item's queue dependency.
    internal async Task OutsideCommands(Func<Task> work)
    {
        commands.Release(); try {await work();} finally {await commands.WaitAsync(CancellationToken.None);}
    }
    /// <summary>Remove atomically with the returned owner scopes; keep a refused pair intact.</summary>
    public Task<TextKey?> RemovePairAsync(string pairId,CancellationToken ct) => Pairs.RemovePairAsync(pairId,ct);
    /// <summary>Re-admit only when both observed roots still match the saved identities.</summary>
    public async Task BindAsync(string pairId,CancellationToken ct)
    {
        HostMember m; Task drain;
        await commands.WaitAsync(ct);
        try {var p=await Required(pairId,ct);m=members.TryGetValue(pairId,out var existing)?existing:Life.Attach(p);drain=HostLifecycle.Drain(m);}
        finally {commands.Release();}
        await drain;
        Task setup;
        await commands.WaitAsync(ct);
        try
        {
            if(stopped.IsCancellationRequested || Volatile.Read(ref m.Closing)!=0 || !members.TryGetValue(pairId,out var current) || current!=m) throw Unknown(pairId);
            if(m.Setup.IsCompleted && m.Stop==null) Life.Launch(m,await Configurations(ct),ct);
            setup=m.Setup;
        }
        finally {commands.Release();}
        await setup.WaitAsync(ct);
        if(!m.Admitted) throw new EngineRequestException(TextKey.PairAwaitingDecision,(TextArg.Pair,pairId));
    }
    /// <summary>Validate the path before provider access; preview and apply use the executor's eligibility.</summary>
    public Task<(ExclusionOutcome? Outcome,long CopyFiles,long CopyBytes,long RemoveFiles,long RemoveBytes,string? RecoveryPlace,TextKey? Cause)> SetExclusionAsync(string pairId,Exclusion exclusion,bool include,bool confirmed,CancellationToken ct) =>
        Pairs.SetExclusionAsync(pairId,exclusion,include,confirmed,ct);
    /// <summary>Retain bindings and request a comparison if an ignore rule was lifted.</summary>
    public Task SetIgnoresAsync(string pairId,IReadOnlyList<string> rules,CancellationToken ct) => Pairs.SetIgnoresAsync(pairId,rules,ct);
    /// <summary>Run explicit Repair on the pair's shared queue.</summary>
    public async Task RepairAsync(string pairId,CancellationToken ct)
    {
        Task run;
        await commands.WaitAsync(ct);
        try
        {
            var p=await Required(pairId,ct);
            if(!p.Enabled || IsPaused(pairId)) throw new EngineRequestException(TextKey.PairDisabled,(TextArg.Pair,pairId));
            var m=await Life.Ready(p,ct);
            run=Life.ClaimRepair(m,ct,async token=>{
                await store!.OweRepairAsync(pairId,Side.Dropbox,"requested",token); await Life.ManualRepair(m,token);
            },true);
        }
        finally { commands.Release(); }
        await run;
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
        if (n.Kind==NoticeKind.DriveChange && choice is ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally) { await ResolveDriveChangeAsync(pairId,p,n,choice,ct); return; }
        var m=await Life.Ready(p,ct);
        if(choice==ResolutionChoice.Retry) { await RetryNoticeAsync(pairId,id,p,m,n,ct); return; }
        if (choice==ResolutionChoice.BindRoots) { await Life.Membership(m,false,ct); Life.Start(m); return; }
        var outcome=await new Resolve().ApplyAsync(p,settings,m.Event,m.Paths,n,choice,name,m.Local,m.Dropbox,store!,
            (s,side,f,token)=>Populate(m,s,side,f,token),(b,before,after,token)=>Compare(m,b,before,after,token),ct,m.Queue.Held);
        if(outcome!=EffectOutcome.Applied) throw new EngineRequestException(n.Cause,(TextArg.Path,n.Path));
    },ct);
    // Accept the drive the pair now reads only when the Dropbox root is unchanged, then re-admit the member on it.
    private async Task ResolveDriveChangeAsync(string pairId,PairConfig p,Notice n,ResolutionChoice choice,CancellationToken ct)
    {
        var member=members.TryGetValue(pairId,out var existing) ? existing : Life.Attach(p);
        await HostLifecycle.Drain(member); var (roots,drive,_)=await Life.ReadRoots(member,ct,true);
        if (roots?.IsValid()!=true || drive==null || p.Roots==null || roots.Dropbox!=p.Roots.Dropbox) throw new EngineRequestException(TextKey.RootUnavailable);
        var updated=p with {Roots=roots,Drive=drive};
        if (!await store!.ResolveDriveAsync(updated,n,choice,ct)) throw new EngineRequestException(TextKey.ResolveStale);
        await Life.Membership(member,true,ct); member.Local=null!; member.Dropbox=null!; member.Pair=updated; await Life.Observe(member,await Configurations(ct),ct); await Life.Membership(member,false,ct); Life.Start(member);
    }
    // Retry the reviewed notice's held inputs and pending job, then its own retry; a put-back also re-attempts its footprint's owners.
    private async Task RetryNoticeAsync(string pairId,long id,PairConfig p,HostMember m,Notice n,CancellationToken ct)
    {
        var held=m.Queue.RetryHeldAsync(null,ct,n);
        var jobs=m.Runner.Unfinished.CheckAsync(p,settings,m.Event,m.Paths,null,m.Local,m.Dropbox,store!,m.Queue.Held,
            (s,side,f,token)=>Populate(m,s,side,f,token),(b,before,after,token)=>Compare(m,b,before,after,token),ct,reviewed:n);
        await jobs;
        if(!held.IsCompleted) await OutsideCommands(()=>held); // A queued publication needs commands to commit.
        var found=await held;
        if(!members.TryGetValue(pairId,out var current) || current!=m || Volatile.Read(ref m.Closing)!=0) throw Unknown(pairId);
        if(await store!.NoticeAsync(pairId,id,ct) is not {} saved) return;
        if(!saved.Matches(n)) throw new EngineRequestException(TextKey.ResolveStale,(TextArg.Id,id.ToString()));
        if(found && n.Kind!=NoticeKind.PutBack) return;
        await new Retries(m.Runner.Unfinished).RetryAsync(m.Pair,settings,m.Event,m.Paths,n,m.Local,m.Dropbox,store,m.Queue.Held,
            (s,side,f,token)=>Populate(m,s,side,f,token),(b,before,after,token)=>Compare(m,b,before,after,token),ct,m.Queue.CoversHop);
        if(n.Kind==NoticeKind.PutBack && await store.NoticeAsync(pairId,id,ct) is {} remaining)
            throw new EngineRequestException(remaining.Matches(n)?TextKey.WaitItemRetry:TextKey.ResolveStale,(TextArg.Id,id.ToString()));
        // A closed put-back ends its ancestor removal's custody; re-attempt only held inputs overlapping this notice's footprint.
        if(n.Kind==NoticeKind.PutBack)
        {
            var owners=m.Queue.RetryHeldAsync(new Scope(n.Path,n.Path),ct);
            if(!owners.IsCompleted) await OutsideCommands(()=>owners);
            await owners;
        }
    }
    internal Task Populate(HostMember m,Scope s,Side side,Recovery.Folder f,CancellationToken ct) => AutoSync.PopulateAsync(m.Pair,settings,m.Event,m.Paths,s,side,f,m.Local,m.Dropbox,store!,ct);
    internal Task Compare(HostMember m,SyncedEntry b,Scope before,Scope? after,CancellationToken ct) => Repair.RunAsync(m.Pair,settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,store!,null,(b,before,after),ct);
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
    { var affected=members.Values.Where(m=>m.Pair.ConnectionId==connectionId).ToArray(); foreach(var m in affected) paused[m.Pair.PairId]=0; await Task.WhenAll(affected.Select(async m=>{ await HostLifecycle.Drain(m); await Life.Membership(m,false,ct); m.Local=null!; m.Dropbox=null!; m.Admitted=false; })); await disconnect(connectionId,ct); },ct);
}
