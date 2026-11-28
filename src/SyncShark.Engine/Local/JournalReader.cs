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
    internal readonly record struct Half(string? Path,string Parent,string? Name,ulong Usn,bool Dirty=false,bool BeforeOld=false);
    internal readonly record struct Hop(ulong Old,ulong New,ulong Id,string? FolderPath);
    internal sealed class Member
    {
        public JournalPosition? Read, Settled, PausedThrough;
        public ulong? FailedUsn;
        public PairConfig? Pair;
        public bool Paused, ResetDue, RepairWake;
        public readonly Queue<Hop> Paired=new();
        public ImmutableQueue<(ulong Id,ulong Start,JournalPosition Until)> Deferred=ImmutableQueue<(ulong,ulong,JournalPosition)>.Empty;
        public readonly LinkedList<(ulong Usn,bool Projected)> Holds=new();
        public readonly Dictionary<ulong,LinkedListNode<(ulong Usn,bool Projected)>> HoldNodes=[];
        public LinkedListNode<(ulong Usn,bool Projected)>? FirstUnprojected;
        public ImmutableDictionary<ulong,Hop> Physical=ImmutableDictionary<ulong,Hop>.Empty;
        public void ClearLineage() { FailedUsn=null; FirstUnprojected=null; OldNames=OldNames.Clear(); Physical=Physical.Clear(); Deferred=Deferred.Clear(); Paired.Clear(); Holds.Clear(); HoldNodes.Clear(); }
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
                member.PausedThrough=paused && member.Read is { } read ? Prefix(read,member.FailedUsn,member.Holds.First?.Value.Usn) : null;
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
            var bound=Prefix(position,m.FailedUsn,m.Holds.First?.Value.Usn);
            if (m.Settled is null || bound.NextUsn>m.Settled.NextUsn) { m.Settled=bound; if (m.Paused) m.Read=bound; }
        }
        return SaveCommon(key,v,store,ct);
    }

    private static JournalPosition Prefix(JournalPosition position,ulong? failed,ulong? oldestHold)
    {
        var oldest=Math.Min(failed??position.NextUsn,oldestHold??position.NextUsn);
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

    // Every await of a page (native read, projection, Repair request, paused probes) is in this frame: one state machine per page.
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
                if (BeginPage(pair,v,m,end,ct,out var page) is { } early) return early;
                var (from,raw,old,physical,deferred)=page;
                if (raw is null)
                {
                    var (range,pending)=JoinRead(pair,v,from,end,deferred,settings);
                    try { raw=await pending.Page.WaitAsync(ct).ConfigureAwait(false); }
                    catch(OperationCanceledException) when(!ct.IsCancellationRequested && pending.Stop.IsCancellationRequested) { /* The last reader stopped it; look again. */ }
                    finally { await v.Read.WaitAsync(CancellationToken.None).ConfigureAwait(false); LeaveRead(v,range,pending); }
                    if (ResetAfterRead(pair,v,m,from,ct) is { } reset) return reset;
                    if (raw is null || range.From!=from && raw.Status is (NtfsUsnPageStatus.More or NtfsUsnPageStatus.Complete)) continue;
                }
                if (raw.Status is NtfsUsnPageStatus.HistoryLost or NtfsUsnPageStatus.JournalChanged)
                {
                    var fresh=journal.Current(pair.LocalRoot);
                    await ResetVolume(v,m,key,fresh,store,ct).ConfigureAwait(false);
                    foreach(var read in v.Reads.Values) read.Stop.Cancel();
                    return new([],null,fresh){ContinuityLost=true};
                }
                var next=NextPosition(raw,from,end);
                var paired=new List<Hop>(); var starts=new List<ulong>(); JournalPosition? fence=null;
                void Defer(ulong id,ulong usn) { fence??=journal.Current(pair.LocalRoot); deferred=deferred.Enqueue((id,usn,fence)); }
                var (changes,uncertain,failedUsn,records)=await JournalProjection.Project(pair,m,from,key,raw.Records!,next.NextUsn,old,physical,ct,paired:paired,starts:starts,defer:Defer).ConfigureAwait(false);
                if (ExpireFences(ref deferred,old,next,records) is { } expired) { failedUsn=Math.Min(failedUsn ?? expired,expired); uncertain=true; }
                if(uncertain) await store.OweRepairAsync(pair.PairId,Side.Local,Text.Get(TextKey.JournalRepairRequired),ct).ConfigureAwait(false);
                foreach(var other in PausedAt(v,from))
                {
                    var passed=from.NextUsn;
                    try { var probe=await JournalProjection.Project(other.Pair!,other,from,key,raw.Records!,next.NextUsn,ImmutableDictionary.CreateBuilder<ulong,Half>(),other.Physical,ct,true).ConfigureAwait(false); passed=probe.Changes.Count==0 ? next.NextUsn : probe.Changes[0].Usn!.Value; }
                    catch(Exception error) when (JournalProjection.MetadataFailure(error)) { /* Unproved range remains in NTFS. */ }
                    lock(gate) if (other.Paused && other.Read==from) other.Read=other.Settled=new JournalPosition(from.JournalId,passed);
                }
                return PublishPage(pair,v,m,from,end,next,raw,old,deferred,paired,starts,changes,uncertain,failedUsn,ct) with {Uncertain=records};
            }
        }
        finally { v.Read.Release(); }
    }

    // Under the gate: the page that answers without a native read, or the member's position, cached page and lineage.
    private ChangePage? BeginPage(PairConfig pair,Volume v,Member m,JournalPosition? end,CancellationToken ct,
        out (JournalPosition From,NtfsUsnPage? Raw,ImmutableDictionary<ulong,Half>.Builder Old,ImmutableDictionary<ulong,Hop> Physical,ImmutableQueue<(ulong Id,ulong Start,JournalPosition Until)> Deferred) page)
    {
        page=default;
        lock(gate)
        {
            if (m.Paused || !v.Members.ContainsKey(pair.PairId)) throw new OperationCanceledException(ct);
            var from=m.Read!;
            if (TakeReset(v,m) is { } reset) return reset;
            if(m.RepairWake) { m.RepairWake=false; return new([],null,from){ContinuityLost=true}; }
            page=(from,null,m.OldNames.ToBuilder(),m.Physical,m.Deferred);
            if (end?.JournalId==from.JournalId && end.NextUsn<=from.NextUsn) return new([],null,from){CaughtUp=true,ProjectedThrough=Prefix(from,m.FailedUsn,m.FirstUnprojected?.Value.Usn)};
            page.Raw=v.Current is { } c && c.From==from && (end==null || end.JournalId==from.JournalId) ? c.Page : null;
        }
        if (end!=null && end.JournalId!=page.From.JournalId && journal.Current(pair.LocalRoot).JournalId==page.From.JournalId)
            return new([],null,page.From){ContinuityLost=true}; // Obsolete capture; the current journal has not reset again.
        return null;
    }

    // The member's next native range. A live wait cannot own the projection lock: another pair may already have a finite range to replay.
    private (JournalPosition From,JournalPosition? End) ReadRange(PairConfig pair,Volume v,JournalPosition from,JournalPosition? end,
        ImmutableQueue<(ulong Id,ulong Start,JournalPosition Until)> deferred)
    {
        var target=end ?? (!deferred.IsEmpty && deferred.Peek().Until.JournalId==from.JournalId ? deferred.Peek().Until : null); var readFrom=from;
        if (target==null && v.Reads.Keys.FirstOrDefault(k=>k.End==null).From is { } waiting && waiting!=from)
        {
            // Lag catches up to the existing wait using finite reads, never opens a second live wait.
            target=journal.Current(pair.LocalRoot);
            if(target==from) { readFrom=waiting; target=null; }
        }
        return (readFrom,target);
    }

    // Members of one volume share each native read of the member's range. The caller waits without the read semaphore.
    private ((JournalPosition From,JournalPosition? End) Range,Pending Pending) JoinRead(PairConfig pair,Volume v,JournalPosition from,JournalPosition? end,
        ImmutableQueue<(ulong Id,ulong Start,JournalPosition Until)> deferred,EngineSettings settings)
    {
        var range=ReadRange(pair,v,from,end,deferred);
        if (!v.Reads.TryGetValue(range,out var pending))
        {
            var stop=new CancellationTokenSource();
            pending=new(stop,journal.ReadAsync(pair.LocalRoot,range.From,range.End,settings.JournalPageBufferBytes,stop.Token));
            v.Reads.Add(range,pending);
        }
        pending.Readers++;
        v.Read.Release();
        return (range,pending);
    }

    // Called again under the read semaphore: the last reader stops the range and disposes its source.
    private static void LeaveRead(Volume v,(JournalPosition From,JournalPosition? End) range,Pending pending)
    {
        if (--pending.Readers!=0) return;
        v.Reads.Remove(range);
        if (!pending.Page.IsCompleted) pending.Stop.Cancel();
        // The native request must release its WaitHandle before its cancellation source is disposed.
        _=pending.Page.ContinueWith(t=>{ _=t.Exception; pending.Stop.Dispose(); },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }

    // A reset or a membership change during the wait wins over the page that was read.
    private ChangePage? ResetAfterRead(PairConfig pair,Volume v,Member m,JournalPosition from,CancellationToken ct)
    {
        lock(gate)
        {
            if (TakeReset(v,m) is { } reset) return reset;
            if (m.Read!=from || m.Paused || !v.Members.ContainsKey(pair.PairId)) throw new OperationCanceledException(ct);
            return null;
        }
    }

    // A usable page advances the member's position; any other status is the native failure.
    private static JournalPosition NextPosition(NtfsUsnPage raw,JournalPosition from,JournalPosition? end)
    {
        if (raw.Status is not (NtfsUsnPageStatus.Complete or NtfsUsnPageStatus.More) || raw.ConsumedNextUsn<=from.NextUsn) throw UsnJournal.Failure(raw.Status,raw.NativeError);
        return new JournalPosition(from.JournalId,Math.Min(end?.NextUsn ?? ulong.MaxValue,raw.ConsumedNextUsn));
    }

    // Under the gate: publish the projected page's lineage and position for the member.
    private ChangePage PublishPage(PairConfig pair,Volume v,Member m,JournalPosition from,JournalPosition? end,JournalPosition next,NtfsUsnPage raw,ImmutableDictionary<ulong,Half>.Builder old,
        ImmutableQueue<(ulong Id,ulong Start,JournalPosition Until)> deferred,List<Hop> paired,List<ulong> starts,List<Change> changes,bool uncertain,ulong? failedUsn,CancellationToken ct)
    {
        lock(gate)
        {
            if (m.Paused || !v.Members.ContainsKey(pair.PairId) || m.Read!=from) throw new OperationCanceledException(ct);
            if (failedUsn is { } failure) m.FailedUsn=Math.Min(m.FailedUsn ?? failure,failure);
            m.OldNames=old.ToImmutable(); m.Deferred=deferred;
            foreach(var usn in starts)
            {
                var node=m.Holds.AddLast((usn,false)); m.HoldNodes.Add(usn,node); m.FirstUnprojected??=node;
            }
            foreach(var hop in paired)
            {
                m.HoldNodes[hop.Old].Value=(hop.Old,true); m.Paired.Enqueue(hop);
                if(hop.FolderPath!=null) m.Physical=m.Physical.SetItem(hop.Id,hop);
            }
            // Settlement retains paired nodes; projection skips each classified origin at most once.
            while(m.FirstUnprojected is { } first && first.Value.Projected) m.FirstUnprojected=first.Next;
            m.Read=next; m.RepairWake=uncertain; v.Current=(from,raw);
            return new(changes,null,next){CaughtUp=end==next && !uncertain,ProjectedThrough=Prefix(next,m.FailedUsn,m.FirstUnprojected?.Value.Usn)};
        }
    }

    // Dequeue the fences this page passed and drop their unpaired halves. The fence requests Repair; it does not settle
    // the original mismatch. Returns the lowest expired origin, if any.
    private static ulong? ExpireFences(ref ImmutableQueue<(ulong Id,ulong Start,JournalPosition Until)> deferred,ImmutableDictionary<ulong,Half>.Builder old,JournalPosition next,List<RunEvent> records)
    {
        ulong? expired=null;
        while(!deferred.IsEmpty && deferred.Peek().Until.JournalId==next.JournalId && deferred.Peek().Until.NextUsn<=next.NextUsn)
        {
            deferred=deferred.Dequeue(out var due);
            if (!old.TryGetValue(due.Id,out var half) || !half.BeforeOld || half.Usn!=due.Start) continue;
            old.Remove(due.Id); expired=Math.Min(expired ?? half.Usn,half.Usn);
            records.Add(JournalProjection.Record(half.Usn,LocalIdentity.FileIdText(due.Id),half.Path ?? half.Name,nameof(ExpireFences)));
        }
        return expired;
    }

    // The paused pairs at this page's start get no queue or delivered entry; a probe stops each one's coverage at its first relevant record.
    private Member[] PausedAt(Volume v,JournalPosition from)
    {
        lock(gate) return v.Members.Values.Where(x=>x.Paused && x.Pair!=null && x.Read==from &&
            (x.PausedThrough is null || x.PausedThrough.NextUsn<=from.NextUsn)).ToArray();
    }

    private async Task ResetVolume(Volume v,Member detecting,string volume,JournalPosition fresh,IStore store,CancellationToken ct)
    {
        var reason=Text.Get(TextKey.JournalRepairRequired);
        await v.Save.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The store resolves durable volume membership in its write transaction, not from an earlier reader snapshot.
            await store.ResetJournalAsync(volume,fresh,reason,ct).ConfigureAwait(false);
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
}
