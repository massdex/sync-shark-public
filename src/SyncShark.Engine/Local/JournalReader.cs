using System.Collections.Immutable;
using System.Globalization;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>One reader per host shares native pages between members of a volume. Only the common settled prefix is durable.</summary>
public sealed class JournalReader
{
    private readonly object gate = new();
    private readonly Dictionary<string, Volume> volumes = [];
    private readonly UsnJournal journal;
    /// <summary>Use the Windows volume journal.</summary>
    public JournalReader() : this(new UsnJournal()) { }
    /// <summary>Read the native journal using the supplied volume source.</summary>
    public JournalReader(UsnJournal journal) => this.journal=journal;
    private sealed class Volume
    {
        public readonly Dictionary<string, Member> Members = [];
        public readonly SemaphoreSlim Read = new(1), Save = new(1);
        public (JournalPosition From,NtfsUsnPage Page)? Current;
        public JournalPosition? LastReset;
        public readonly Dictionary<(JournalPosition From,JournalPosition? End),Pending> Reads=[];
        public JournalPosition? Saved;
    }
    private sealed class Pending(CancellationTokenSource stop,Task<NtfsUsnPage> page)
    {
        public readonly CancellationTokenSource Stop=stop;
        public readonly Task<NtfsUsnPage> Page=page;
        public int Readers;
    }
    private readonly record struct Half(string? Path,string Parent,string? Name,ulong Usn,bool Dirty=false,bool BeforeOld=false);
    private readonly record struct Hop(ulong Old,ulong New,ulong Id,string? FolderPath);
    private sealed class Member
    {
        public JournalPosition? Read, Settled, PausedThrough;
        public ulong? FailedUsn;
        public PairConfig? Pair;
        public bool Paused, ResetDue, RepairWake;
        public readonly Queue<Hop> Paired=new();
        public ImmutableQueue<(ulong Id,ulong Start,JournalPosition Until)> Deferred=ImmutableQueue<(ulong,ulong,JournalPosition)>.Empty;
        public readonly LinkedList<ulong> Holds=new();
        public readonly Dictionary<ulong,LinkedListNode<ulong>> HoldNodes=[];
        public ImmutableDictionary<ulong,Hop> Physical=ImmutableDictionary<ulong,Hop>.Empty;
        public void ClearLineage() { FailedUsn=null; OldNames=OldNames.Clear(); Physical=Physical.Clear(); Deferred=Deferred.Clear(); Paired.Clear(); Holds.Clear(); HoldNodes.Clear(); }
        public IStore? Store;
        public Func<PathScope>? Scope;
        public Func<ulong,bool,CancellationToken,(string? Path,IReadOnlyList<string>? Links)>? Locate;
        public Func<string,Entry?>? Entry;
        public ImmutableDictionary<ulong,Half> OldNames=ImmutableDictionary<ulong,Half>.Empty;
    }

    private static string Domain(PairConfig pair) => pair.Roots?.LocalVolumeKey() ??
        pair.Drive?.Serial?.ToString(CultureInfo.InvariantCulture) ?? throw new InvalidDataException(nameof(pair.Roots));

    /// <summary>Register an already bound member, retaining its old position on reattachment.</summary>
    public void Attach(PairConfig pair)
    {
        if (pair.Roots is null && pair.Drive?.Serial is null) return;
        lock (gate)
        {
            var key = Domain(pair);
            if (!volumes.TryGetValue(key,out var v)) volumes.Add(key,v=new());
            v.Members.TryAdd(pair.PairId,new Member { Paused=!pair.Enabled, Read=v.Saved, Settled=v.Saved });
        }
    }

    /// <summary>Install the store, resolved scope and bound-root metadata operations before acquisition.</summary>
    public void Attach(PairConfig pair,IStore store,Func<PathScope> scope,
        Func<ulong,bool,CancellationToken,(string? Path,IReadOnlyList<string>? Links)> locate,Func<string,Entry?> entry)
    {
        Attach(pair);
        lock (gate)
        {
            var member=volumes[Domain(pair)].Members[pair.PairId];
            member.Pair=pair;
            (member.Store,member.Scope,member.Locate,member.Entry)=(store,scope,locate,entry);
        }
    }

    /// <summary>Only successful removal releases the hold; pausing drops unread in-memory input, not the settled bound.</summary>
    public Task Membership(PairConfig pair,bool paused,bool removed=false)
    {
        var key=Domain(pair); Volume? v; IStore? store;
        lock(gate)
        {
            if (!volumes.TryGetValue(key,out v) || !v.Members.TryGetValue(pair.PairId,out var member)) return Task.CompletedTask;
            store=member.Store;
            if (removed) v.Members.Remove(pair.PairId);
            else if (member.Paused!=paused)
            {
                member.PausedThrough=paused && member.Read is { } read ? HeldPrefix(member,read) : null;
                member.Paused=paused; member.Read=member.Settled; member.ClearLineage();
            }
        }
        return removed && store!=null ? SaveCommon(key,v,store,CancellationToken.None) : Task.CompletedTask;
    }

    /// <summary>Return the member's actual contiguous settlement; unknown is not coverage.</summary>
    public Task<JournalPosition?> SettledJournalAsync(PairConfig pair,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock(gate) return Task.FromResult(pair.Roots==null && pair.Drive?.Serial==null ? null :
            volumes.TryGetValue(Domain(pair),out var v) && v.Members.TryGetValue(pair.PairId,out var m) ? m.Settled : null);
    }

    /// <summary>Release only work strictly before this acknowledged continuation.</summary>
    public Task SettleAsync(PairConfig pair,JournalPosition position,IStore store,CancellationToken ct)
    {
        var key=Domain(pair); Volume v;
        lock(gate)
        {
            v=volumes[key];
            if (!v.Members.TryGetValue(pair.PairId,out var m) || (m.PausedThrough ?? m.Read) is not { } limit || limit.JournalId!=position.JournalId || position.NextUsn>limit.NextUsn) return Task.CompletedTask;
            while (m.Paired.TryPeek(out var hop) && hop.New<position.NextUsn)
            {
                m.Paired.Dequeue();
                if (m.HoldNodes.Remove(hop.Old,out var node)) m.Holds.Remove(node);
                if (hop.FolderPath!=null && m.Physical.TryGetValue(hop.Id,out var physical) && physical==hop) m.Physical=m.Physical.Remove(hop.Id);
            }
            var bound=HeldPrefix(m,position);
            if (m.Settled is null || bound.NextUsn>m.Settled.NextUsn) { m.Settled=bound; if (m.Paused) m.Read=bound; }
        }
        return SaveCommon(key,v,store,ct);
    }

    private static JournalPosition HeldPrefix(Member m,JournalPosition position)
    {
        var oldest=Math.Min(m.FailedUsn??position.NextUsn,m.Holds.First?.Value??position.NextUsn);
        return oldest<position.NextUsn ? new(position.JournalId,oldest) : position;
    }

    private async Task SaveCommon(string key,Volume v,IStore store,CancellationToken ct)
    {
        await v.Save.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            JournalPosition common;
            lock(gate)
            {
                if (v.Saved is null || v.Members.Count==0 || v.Members.Values.Any(x=>x.Settled?.JournalId!=v.Saved.JournalId)) return;
                common=v.Members.Values.Select(x=>x.Settled!).MinBy(x=>x.NextUsn)!;
                if (v.Saved.NextUsn>=common.NextUsn) return;
            }
            await store.SavePositionAsync(key,common,ct).ConfigureAwait(false);
            lock(gate) v.Saved=common;
        }
        finally { v.Save.Release(); }
    }

    /// <summary>Project one finite or live native page without acknowledging its unsettled changes.</summary>
    public async Task<ChangePage> ReadPageAsync(PairConfig pair,JournalPosition? position,JournalPosition? end,EngineSettings settings,CancellationToken ct)
    {
        var key=Domain(pair); Volume v; Member m; lock(gate) { v=volumes[key]; m=v.Members[pair.PairId]; }
        var store=m.Store ?? throw new InvalidOperationException(nameof(Attach));
        await v.Read.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (v.Saved is null)
            {
                var saved=await store.PositionAsync(key,ct).ConfigureAwait(false);
                var range=journal.Retained(pair.LocalRoot);
                var initial=saved ?? position ?? range.Tail;
                if(initial.JournalId!=range.Tail.JournalId || initial.NextUsn<range.First || initial.NextUsn>range.Tail.NextUsn)
                {
                    await ResetVolume(v,m,key,range.Tail,store,ct).ConfigureAwait(false);
                    return new([],null,range.Tail){ContinuityLost=true};
                }
                if (saved is null) await store.SavePositionAsync(key,initial,ct).ConfigureAwait(false);
                lock(gate) { v.Saved=initial; foreach(var x in v.Members.Values) { x.Read??=initial; x.Settled??=initial; } }
            }
            while(true)
            {
                JournalPosition from; NtfsUsnPage? raw; ImmutableDictionary<ulong,Half>.Builder old; ImmutableDictionary<ulong,Hop> physical; ImmutableQueue<(ulong Id,ulong Start,JournalPosition Until)> deferred;
                lock(gate)
                {
                    if (m.Paused || !v.Members.ContainsKey(pair.PairId)) throw new OperationCanceledException(ct);
                    from=m.Read!;
                    if (TakeReset(v,m) is { } reset) return reset;
                    if(m.RepairWake) { m.RepairWake=false; return new([],null,from){ContinuityLost=true}; }
                    old=m.OldNames.ToBuilder(); physical=m.Physical; deferred=m.Deferred;
                    if (end?.JournalId==from.JournalId && end.NextUsn<=from.NextUsn) return new([],null,from){CaughtUp=true};
                    raw=v.Current is { } c && c.From==from && (end==null || end.JournalId==from.JournalId) ? c.Page : null;
                }
                if (end!=null && end.JournalId!=from.JournalId && journal.Current(pair.LocalRoot).JournalId==from.JournalId)
                    return new([],null,from){ContinuityLost=true}; // Obsolete capture; the current journal has not reset again.
                if (raw is null)
                {
                    // A live wait cannot own the projection lock: another pair may already have a finite range to replay.
                    var target=end ?? (!deferred.IsEmpty && deferred.Peek().Until.JournalId==from.JournalId ? deferred.Peek().Until : null); var readFrom=from;
                    if (target==null && v.Reads.Keys.FirstOrDefault(k=>k.End==null).From is { } waiting && waiting!=from)
                    {
                        // Lag catches up to the existing wait using finite reads, never opens a second live wait.
                        target=journal.Current(pair.LocalRoot);
                        if(target==from) { readFrom=waiting; target=null; }
                    }
                    var range=(readFrom,target);
                    if (!v.Reads.TryGetValue(range,out var pending))
                    {
                        var stop=new CancellationTokenSource();
                        pending=new(stop,journal.ReadAsync(pair.LocalRoot,readFrom,target,settings.JournalPageBufferBytes,stop.Token));
                        v.Reads.Add(range,pending);
                    }
                    pending.Readers++;
                    v.Read.Release();
                    try { raw=await pending.Page.WaitAsync(ct).ConfigureAwait(false); }
                    catch(OperationCanceledException) when(!ct.IsCancellationRequested && pending.Stop.IsCancellationRequested) { raw=null; }
                    finally
                    {
                        await v.Read.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        if (--pending.Readers==0)
                        {
                            v.Reads.Remove(range);
                            if (!pending.Page.IsCompleted) pending.Stop.Cancel();
                            // The native request must release its WaitHandle before its cancellation source is disposed.
                            _=pending.Page.ContinueWith(t=>{ _=t.Exception; pending.Stop.Dispose(); },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                        }
                    }
                    lock(gate)
                    {
                        if (TakeReset(v,m) is { } reset) return reset;
                        if (m.Read!=from || m.Paused || !v.Members.ContainsKey(pair.PairId)) throw new OperationCanceledException(ct);
                    }
                    if (raw is null) continue;
                    if (readFrom!=from && raw.Status is (NtfsUsnPageStatus.More or NtfsUsnPageStatus.Complete)) continue;
                }
                if (raw.Status is NtfsUsnPageStatus.HistoryLost or NtfsUsnPageStatus.JournalChanged)
                {
                    var fresh=journal.Current(pair.LocalRoot);
                    await ResetVolume(v,m,key,fresh,store,ct).ConfigureAwait(false);
                    foreach(var read in v.Reads.Values) read.Stop.Cancel();
                    return new([],null,fresh){ContinuityLost=true};
                }
                if (raw.Status is not (NtfsUsnPageStatus.Complete or NtfsUsnPageStatus.More) || raw.ConsumedNextUsn<=from.NextUsn) throw UsnJournal.Failure(raw.Status,raw.NativeError);
                var next=new JournalPosition(from.JournalId,Math.Min(end?.NextUsn ?? ulong.MaxValue,raw.ConsumedNextUsn));
                var paired=new List<Hop>(); var starts=new List<ulong>();
                JournalPosition? fence=null;
                void Defer(ulong id,ulong usn)
                {
                    fence??=journal.Current(pair.LocalRoot);
                    deferred=deferred.Enqueue((id,usn,fence));
                }
                var (changes,uncertain,failedUsn)=await Project(pair,m,from,key,raw.Records!,next.NextUsn,old,physical,ct,paired:paired,starts:starts,defer:Defer).ConfigureAwait(false);
                while(!deferred.IsEmpty && deferred.Peek().Until.JournalId==next.JournalId && deferred.Peek().Until.NextUsn<=next.NextUsn)
                {
                    deferred=deferred.Dequeue(out var due);
                    if (!old.TryGetValue(due.Id,out var half) || !half.BeforeOld || half.Usn!=due.Start) continue;
                    old.Remove(due.Id); paired.Add(new(half.Usn,next.NextUsn-1,due.Id,null)); uncertain=true;
                }
                if(uncertain) await store.OweRepairAsync(pair.PairId,Side.Local,Text.Get(TextKey.JournalRepairRequired),ct).ConfigureAwait(false);
                Member[] paused;
                lock(gate) paused=v.Members.Values.Where(x=>x.Paused && x.Pair!=null && x.Read==from &&
                    (x.PausedThrough is null || x.PausedThrough.NextUsn<=from.NextUsn)).ToArray();
                foreach(var other in paused)
                {
                    // The paused pair gets no queue or delivered entry. Stop its coverage at its first relevant record.
                    var passed=from.NextUsn;
                    try
                    {
                        var probe=await Project(other.Pair!,other,from,key,raw.Records!,next.NextUsn,ImmutableDictionary.CreateBuilder<ulong,Half>(),other.Physical,ct,true).ConfigureAwait(false);
                        passed=probe.Changes.Count==0 ? next.NextUsn : probe.Changes[0].Usn!.Value;
                    }
                    catch(Exception error) when (MetadataFailure(error)) { /* Unproved range remains in NTFS. */ }
                    lock(gate) if (other.Paused && other.Read==from) other.Read=other.Settled=new JournalPosition(from.JournalId,passed);
                }
                lock(gate)
                {
                    if (m.Paused || !v.Members.ContainsKey(pair.PairId) || m.Read!=from) throw new OperationCanceledException(ct);
                    m.FailedUsn??=failedUsn; m.OldNames=old.ToImmutable(); m.Deferred=deferred;
                    foreach(var usn in starts) m.HoldNodes.Add(usn,m.Holds.AddLast(usn));
                    foreach(var hop in paired) { m.Paired.Enqueue(hop); if(hop.FolderPath!=null) m.Physical=m.Physical.SetItem(hop.Id,hop); }
                    m.Read=next; m.RepairWake=uncertain; v.Current=(from,raw);
                }
                return new(changes,null,next){CaughtUp=end==next && !uncertain};
            }
        }
        finally { v.Read.Release(); }
    }

    private async Task ResetVolume(Volume v,Member detecting,string volume,JournalPosition fresh,IStore store,CancellationToken ct)
    {
        var reason=Text.Get(TextKey.JournalRepairRequired);
        await v.Save.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The store resolves durable volume membership in its write transaction, not from an earlier reader snapshot.
            await store.ResetJournalAsync(volume,fresh,[],reason,ct).ConfigureAwait(false);
            lock(gate)
            {
                v.Saved=v.LastReset=fresh; v.Current=null;
                foreach(var x in v.Members.Values)
                {
                    x.PausedThrough=null; x.RepairWake=false; x.ClearLineage();
                    x.Read=x.Settled=fresh; x.ResetDue=x!=detecting;
                }
            }
        }
        finally { v.Save.Release(); }
    }

    private static ChangePage? TakeReset(Volume v,Member m)
    {
        if (v.LastReset is not { } fresh || !m.ResetDue) return null;
        m.ResetDue=false;
        return new([],null,fresh){ContinuityLost=true};
    }

    private static bool BoundRootChanged(Exception error,PairConfig pair) => error is ChangedUnderneathException changed && PathNames.Same(changed.Path,pair.LocalRoot);
    private static bool MetadataFailure(Exception e) => e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ChangedUnderneathException or ItemFailedException;
    // USN_REASON bits from winioctl.h; only the three data-change bits establish a content write.
    private const uint Content=7, Create=0x100, Delete=0x200, Old=0x1000, New=0x2000, Links=0x10000, Reparse=0x100000, Close=0x80000000;
    private static async Task<(List<Change> Changes,bool Uncertain,ulong? FailedUsn)> Project(PairConfig pair,Member m,JournalPosition from,string volume,IReadOnlyList<UsnRecord> records,ulong end,
        ImmutableDictionary<ulong,Half>.Builder old,ImmutableDictionary<ulong,Hop> physical,CancellationToken ct,bool firstOnly=false,List<Hop>? paired=null,List<ulong>? starts=null,Action<ulong,ulong>? defer=null)
    {
        var result=new List<Change>(); bool owesRepair=false; ulong? failedUsn=null; var scope=m.Scope!(); var store=m.Store!; var root=pair.Roots!.Local.FileId;
        var armed=await store.ArmedAsync(pair.PairId,ct).ConfigureAwait(false);
        bool Excluded(string path,bool folder) => scope.Excludes(path,null,folder) &&
            !(pair.Exclusions.Any(e=>!e.Keep && e.Side==Side.Local && PathNames.IsSelfOrUnder(path,e.Path)) && !scope.Configured.Excludes(path,null,folder));
        var locations=new Dictionary<ulong,(string? Path,IReadOnlyList<string>? Links)>();
        var saved=new Dictionary<string,List<SyncedEntry>>();
        var movedParents=new Dictionary<ulong,string>();
        async Task<List<SyncedEntry>> Bindings(string id)
        {
            if (saved.TryGetValue(id,out var bindings)) return bindings;
            bindings=[]; await foreach(var row in store.ByIdAsync(pair.PairId,Side.Local,id,ct)) bindings.Add(row);
            return saved[id]=bindings;
        }
        foreach(var record in records)
        {
            ct.ThrowIfCancellationRequested(); if (record.Usn>=end || firstOnly && result.Count!=0) break;
            var flags=record.Reason;
            if ((flags & (Content|Create|Delete|Old|New|Links|Reparse|Close))==0) continue;
            if (record.FileId==root)
            {
                if ((flags&(Delete|Old|New|Reparse))!=0) throw new JournalRootChangedException(pair.LocalRoot);
                continue;
            }
            bool folder=(record.Attributes&FileAttributes.Directory)!=0;
            bool IsStagePath(string path) => !folder && StageNames.IsStage(PathNames.Name(path));
            var id=LocalIdentity.FileIdText(record.FileId);
            var parent=LocalIdentity.FileIdText(record.ParentId);
            var rows=await Bindings(id).ConfigureAwait(false);
            string? history=null;
            if (record.Name is {Length:>0} name)
            {
                if (record.ParentId==root) history=name;
                else if (movedParents.TryGetValue(record.ParentId,out var folderPath)) history=PathNames.Child(folderPath,name);
                else if (physical.TryGetValue(record.ParentId,out var parentHop)) history=PathNames.Child(parentHop.FolderPath!,name);
                else if (await Bindings(parent).ConfigureAwait(false) is [var binding]) history=PathNames.Child(binding.LocalPath,name);
            }
            var victim=armed?.Victim.Proof;
            var affected=victim!=null && victim.VolumeId==volume && (victim.Expected.Id==id || victim.Expected.IsFolder && victim.Expected.Id==parent);
            var pathlessKind=(flags&Delete)!=0 ? ChangeKind.Deleted : ChangeKind.Changed;
            void RecoveryEvidence(bool absent,bool incomplete)
            {
                if(!affected) return;
                result.Add(new Change(Side.Local,pathlessKind,null,null,id,null,(flags&Content)!=0,
                    pathlessKind==ChangeKind.Deleted && absent,record.Usn,volume,parent,record.Name,JournalId:from.JournalId)
                    { AffectedPath=victim!.Expected.Path });
                if(incomplete && !firstOnly) { failedUsn??=record.Usn; owesRepair=true; }
            }
            // OLD is not a change at its former path. Preserve the half until NEW arrives, including across pages.
            if ((flags&Old)!=0 && (flags&New)==0)
            {
                var previous=history ?? (rows.Count==1 ? rows[0].LocalPath : null);
                if (affected || previous!=null || record.ParentId==root || (await Bindings(parent)).Count!=0)
                {
                    if (firstOnly) result.Add(new(Side.Local,ChangeKind.Renamed,null,previous,id,null,false,false,record.Usn,volume,parent,record.Name,JournalId:from.JournalId));
                    else if (old.TryGetValue(record.FileId,out var earlier)) old[record.FileId]=new(previous,parent,record.Name,earlier.Usn,earlier.Dirty || (flags&Content)!=0);
                    else { old[record.FileId]=new(previous,parent,record.Name,record.Usn,(flags&Content)!=0); starts!.Add(record.Usn); }
                }
                continue;
            }
            bool renaming=(flags&New)!=0;
            bool hasPrior=old.TryGetValue(record.FileId,out var prior);
            if (hasPrior && (renaming || prior.BeforeOld && (flags&Delete)!=0))
            { old.Remove(record.FileId); paired?.Add(new(prior.Usn,record.Usn,record.FileId,renaming && folder?history:null)); }
            bool dirty=(flags&Content)!=0 || renaming && hasPrior && prior.Dirty;
            async Task RetainFailure(string path,Exception error)
            {
                if(IsStagePath(path)) return;
                if(!firstOnly)
                {
                    var cause=error is ItemFailedException item ? item.Cause : TextKey.ReadLocalFailed;
                    await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,cause,path,
                        new([new(Side.Local,path,id)],Side.Local),DateTimeOffset.UtcNow,false),ct).ConfigureAwait(false);
                    failedUsn??=record.Usn; owesRepair=true;
                }
                // The queue orders footprints, not notices. Preserve the original ID and operation for its decisive retry.
                result.Add(new(Side.Local,pathlessKind,path,null,id,null,dirty,false,
                    record.Usn,volume,parent,record.Name,JournalId:from.JournalId));
            }
            bool namedHop=renaming && hasPrior && prior.Path!=null && history!=null;
            if(namedHop)
            {
                // A physical hop is known before lookup. Metadata errors cannot change it into independent path work.
                Entry? item=null;
                try { item=m.Entry!(history!); }
                catch(Exception e) when(MetadataFailure(e) && !BoundRootChanged(e,pair)) { /* The ordered rename owns the retry, including both endpoints. */ }
                if (folder) movedParents[record.FileId]=history!;
                if(!IsStagePath(history!) && (affected || !(Excluded(history!,folder) && Excluded(prior.Path!,folder))))
                    result.Add(new(Side.Local,ChangeKind.Renamed,history,prior.Path,id,item?.Id==id?item:null,dirty,false,record.Usn,volume,parent,record.Name,prior.Parent,prior.Name,from.JournalId));
                if (!dirty || folder) continue;
            }
            bool outsideHop=false;
            if(renaming && history==null && (hasPrior || rows.Count!=0) && record.Name!=null)
            {
                // A different final alias alone never proves the physical destination was outside the pair.
                try { var parentLocation=m.Locate!(record.ParentId,false,ct);
                    outsideHop=parentLocation.Path!=null && !PathNames.Same(parentLocation.Path,pair.LocalRoot) && PathNames.RelativeUnderLocalRoot(pair.LocalRoot,parentLocation.Path)==null; }
                catch(Exception e) when(MetadataFailure(e) && !BoundRootChanged(e,pair)) { /* An unproved hop goes to Repair. */ }
            }
            if(renaming && history==null && (hasPrior || rows.Count!=0) && !outsideHop)
            {
                // A redacted intermediate destination cannot be reconstructed from today's final alias.
                if(firstOnly) result.Add(new(Side.Local,ChangeKind.Renamed,null,prior.Path,id,null,false,false,record.Usn,JournalId:from.JournalId));
                else owesRepair=true;
                continue;
            }
            (string? Path,IReadOnlyList<string>? Links) loc;
            try
            {
                if (!locations.TryGetValue(record.FileId,out loc) || namedHop || (flags&Links)!=0)
                    locations[record.FileId]=loc=m.Locate!(record.FileId,(flags&Links)!=0,ct);
            }
            catch (Exception error) when (MetadataFailure(error) && !BoundRootChanged(error,pair))
            {
                // Unreadable is not absence. A named affected binding gets its normal typed Failure;
                // redacted input under a known parent needs Repair before this page can settle.
                var known=namedHop ? rows.Select(x=>x.LocalPath).Where(path=>path!=prior.Path && path!=history).ToArray() :
                    rows.Select(x=>x.LocalPath).Concat(history is null ? [] : [history]).Distinct(StringComparer.Ordinal).ToArray();
                foreach(var path in known.Where(path=>affected || !Excluded(path,folder)))
                {
                    await RetainFailure(path,error).ConfigureAwait(false);
                }
                if (known.Length==0 && (affected || record.ParentId==root || (await Bindings(parent)).Count!=0))
                {
                    if (affected) RecoveryEvidence(absent:false,incomplete:true);
                    else if (firstOnly) result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,false,false,record.Usn,JournalId:from.JournalId));
                    if(!firstOnly) owesRepair=true;
                }
                continue;
            }
            var paths=new HashSet<string>(StringComparer.Ordinal);
            foreach(var full in loc.Links ?? (loc.Path is null ? Array.Empty<string>() : [loc.Path]))
                if(PathNames.RelativeUnderLocalRoot(pair.LocalRoot,full) is { } path) paths.Add(path);
            // Exact surviving aliases cannot be the predecessor of a different new link.
            var savedPaths=rows.Select(row=>row.LocalPath).ToHashSet(StringComparer.Ordinal);
            if (namedHop)
            {
                // If the physical destination is still present, its other live names are independent links.
                // If it has moved again, only exact surviving bindings distinguish links from the later hop.
                bool destinationPresent=paths.Contains(history!);
                paths.RemoveWhere(path=>path==prior.Path || path==history || !destinationPresent && !savedPaths.Contains(path));
            }
            var missing=rows.Where(row=>!paths.Contains(row.LocalPath)).ToArray();
            var added=paths.Where(path=>!savedPaths.Contains(path)).ToArray();
            string? source=prior.Path!=null && !paths.Contains(prior.Path) ? prior.Path : missing.Length==1 ? missing[0].LocalPath : null;
            string? destination=history!=null && paths.Contains(history) ? history : added.Length==1 ? added[0] : null;
            bool matched=renaming && !namedHop && !outsideHop && source!=null && destination!=null && !savedPaths.Contains(destination);
            var seen=new HashSet<string>(StringComparer.Ordinal); bool unresolved=false;
            void Add(string? path,string? oldPath,ChangeKind kind,Entry? item,bool deleted)
            {
                if(path==null) { unresolved=true; return; }
                if (!seen.Add(path) || IsStagePath(path)) return;
                if (!affected && Excluded(path,folder) && (oldPath is null || Excluded(oldPath,folder))) return;
                result.Add(new Change(Side.Local,kind,path,oldPath,id,item,dirty,deleted,record.Usn,volume,parent,record.Name,kind==ChangeKind.Renamed?prior.Parent:null,kind==ChangeKind.Renamed?prior.Name:null,from.JournalId));
            }
            void DeferMissing(string? path)
            {
                if(firstOnly) { Add(path ?? history,null,ChangeKind.Changed,null,false); return; }
                if(old.TryGetValue(record.FileId,out var held)) { if(dirty) old[record.FileId]=held with {Dirty=true}; return; }
                old[record.FileId]=new(path,parent,record.Name,record.Usn,dirty,true); starts!.Add(record.Usn); defer!(record.FileId,record.Usn);
            }
            if (!renaming && (flags&Delete)==0)
            {
                bool parentMoved=movedParents.ContainsKey(record.ParentId)||physical.ContainsKey(record.ParentId);
                if (parentMoved && history!=null)
                {
                    Add(history,null,ChangeKind.Changed,null,false);
                    if(loc.Links==null) continue;
                    // Keep independent hard links, never substitute the later spelling of this same binding.
                    paths.RemoveWhere(path=>!savedPaths.Contains(path));
                }
                if (missing.Length!=0 && !parentMoved)
                {
                    // Metadata is newer than this record. Only a name operation can remove/move a saved binding.
                    DeferMissing(missing.Length==1?missing[0].LocalPath:null);
                    paths.RemoveWhere(path=>!savedPaths.Contains(path) && ((flags&Links)==0 || path!=history));
                }
            }
            foreach(var path in paths)
            {
                Entry? entry;
                try { entry=m.Entry!(path); }
                catch(Exception error) when (MetadataFailure(error) && !BoundRootChanged(error,pair))
                {
                    if(!Excluded(path,folder) || affected)
                    {
                        if(affected && history!=path && !savedPaths.Contains(path)) unresolved=true;
                        else { await RetainFailure(path,error).ConfigureAwait(false); seen.Add(path); }
                    }
                    continue;
                }
                if (entry?.Id!=id)
                {
                    if(savedPaths.Contains(path) && !renaming && (flags&Delete)==0) DeferMissing(path);
                    else if(affected) unresolved=true;
                    continue;
                }
                var previous=matched && path==destination ? source : null;
                Add(path,previous,previous!=null && previous!=path ? ChangeKind.Renamed : ChangeKind.Changed,entry,false);
            }
            if ((flags&Delete)!=0 || outsideHop) foreach(var row in missing.Where(b=>outsideHop ? b.LocalPath==source : loc.Path==null || b.LocalPath==history))
                Add(row.LocalPath,null,ChangeKind.Deleted,null,(flags&Delete)!=0 && loc.Path is null);
            if (!namedHop && !affected && history!=null && paths.Count==0 && rows.Count==0 && (flags&Delete)==0)
                Add(history,null,ChangeKind.Changed,null,false);
            if ((flags&Delete)!=0 && history!=null && paths.Count==0) Add(history,null,ChangeKind.Deleted,null,loc.Path is null);
            if (!namedHop && affected && (seen.Count==0 || unresolved))
                RecoveryEvidence(absent:loc.Path is null && loc.Links is not {Count:>0},incomplete:unresolved);
            if (history==null && rows.Count==0 && paths.Count==0 && !affected &&
                (record.ParentId==root || (await Bindings(parent).ConfigureAwait(false)).Count!=0))
            {
                if (firstOnly) result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,false,false,record.Usn,volume,parent,JournalId:from.JournalId));
                else owesRepair=true;
            }

        }
        return (result,owesRepair,failedUsn);
    }
}

/// <summary>Intake held the source prefix because the configured root itself changed.</summary>
public sealed class JournalRootChangedException(string root) : ChangedUnderneathException(root);
