using System.Globalization;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>One reader per host shares native pages between members of a volume. Only the common settled prefix is durable.</summary>
public sealed class JournalReader
{
    private readonly object gate = new();
    private readonly Dictionary<string, Volume> volumes = [];
    private readonly UsnJournal journal;
    public JournalReader() : this(new UsnJournal()) { }
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
    private sealed class Member
    {
        public JournalPosition? Read, Settled, PausedThrough;
        public PairConfig? Pair;
        public bool Paused, ResetDue;
        public readonly List<(ulong Old,ulong New)> Paired=[];
        public IStore? Store;
        public Func<PathScope>? Scope;
        public Func<ulong,bool,CancellationToken,(string? Path,IReadOnlyList<string>? Links)>? Locate;
        public Func<string,Entry?>? Entry;
        public Dictionary<ulong,(string? Path,string Parent,string? Name,ulong Usn)> OldNames = [];
    }

    private static string Domain(PairConfig pair) => pair.Roots?.LocalVolumeKey() ??
        pair.Drive?.Serial?.ToString(CultureInfo.InvariantCulture) ?? throw new InvalidDataException(nameof(pair.Roots));

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
            else if (member.Paused!=paused) { member.PausedThrough=paused ? member.Read : null; member.Paused=paused; member.Read=member.Settled; member.OldNames=[]; member.Paired.Clear(); }
        }
        return removed && store!=null ? SaveCommon(key,v,store,CancellationToken.None) : Task.CompletedTask;
    }

    public Task SettleAsync(PairConfig pair,JournalPosition position,IStore store,CancellationToken ct)
    {
        var key=Domain(pair); Volume v;
        lock(gate)
        {
            v=volumes[key];
            if (!v.Members.TryGetValue(pair.PairId,out var m) || (m.PausedThrough ?? m.Read) is not { } limit || limit.JournalId!=position.JournalId || position.NextUsn>limit.NextUsn) return Task.CompletedTask;
            m.Paired.RemoveAll(h=>h.New<position.NextUsn);
            var bound=position.NextUsn;
            foreach(var h in m.OldNames.Values) bound=Math.Min(bound,h.Usn);
            foreach(var h in m.Paired) bound=Math.Min(bound,h.Old);
            if (m.Settled is null || bound>m.Settled.NextUsn) { m.Settled=new(position.JournalId,bound); if (m.Paused) m.Read=m.Settled; }
        }
        return SaveCommon(key,v,store,ct);
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

    public async IAsyncEnumerable<ChangePage> ReadAsync(PairConfig pair,JournalPosition position,EngineSettings settings,[EnumeratorCancellation] CancellationToken ct)
    {
        while(true)
        {
            var page=await ReadPageAsync(pair,position,null,settings,ct).ConfigureAwait(false);
            yield return page;
            if (page.ContinuityLost) yield break;
            position=page.Position!;
        }
    }

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
                var initial=saved ?? position ?? journal.Current(pair.LocalRoot);
                if (saved is null) await store.SavePositionAsync(key,initial,ct).ConfigureAwait(false);
                lock(gate) { v.Saved=initial; foreach(var x in v.Members.Values) { x.Read??=initial; x.Settled??=initial; } }
            }
            while(true)
            {
                JournalPosition from; NtfsUsnPage? raw; Dictionary<ulong,(string? Path,string Parent,string? Name,ulong Usn)> old;
                lock(gate)
                {
                    if (m.Paused || !v.Members.ContainsKey(pair.PairId)) throw new OperationCanceledException(ct);
                    from=m.Read!;
                    if (TakeReset(v,m,end) is { } reset) return reset;
                    old=m.OldNames;
                    if (end?.JournalId==from.JournalId && end.NextUsn<=from.NextUsn) return new([],null,from){CaughtUp=true};
                    raw=v.Current is { } c && c.From==from && (end==null || end.JournalId==from.JournalId) ? c.Page : null;
                }
                if (raw is null)
                {
                    // A live wait cannot own the projection lock: another pair may already have a finite range to replay.
                    var target=end; var readFrom=from;
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
                        if (TakeReset(v,m,end) is { } reset) return reset;
                        if (m.Read!=from || m.Paused || !v.Members.ContainsKey(pair.PairId)) throw new OperationCanceledException(ct);
                    }
                    if (raw is null) continue;
                    if (readFrom!=from && raw.Status is (NtfsUsnPageStatus.More or NtfsUsnPageStatus.Complete)) continue;
                }
                if (raw.Status is NtfsUsnPageStatus.HistoryLost or NtfsUsnPageStatus.JournalChanged)
                {
                    var fresh=journal.Current(pair.LocalRoot);
                    await v.Save.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        string[] members; lock(gate) members=v.Members.Keys.ToArray();
                        await store.ResetJournalAsync(key,fresh,members,Text.Get(TextKey.JournalRepairRequired),ct).ConfigureAwait(false);
                        lock(gate) { v.Saved=v.LastReset=fresh; v.Current=null; foreach(var x in v.Members.Values) { x.Read=x.Settled=fresh; x.PausedThrough=null; x.OldNames=[]; x.Paired.Clear(); x.ResetDue=x!=m; } }
                    }
                    finally { v.Save.Release(); }
                    foreach(var read in v.Reads.Values) read.Stop.Cancel();
                    return new([],null,fresh){ContinuityLost=true};
                }
                if (raw.Status is not (NtfsUsnPageStatus.Complete or NtfsUsnPageStatus.More) || raw.ConsumedNextUsn<=from.NextUsn) throw UsnJournal.Failure(raw.Status,raw.NativeError);
                var next=new JournalPosition(from.JournalId,Math.Min(end?.NextUsn ?? ulong.MaxValue,raw.ConsumedNextUsn));
                if (raw.Records!.Any(r=>(r.Reason&(Old|New))!=0)) old=new(old);
                var paired=new List<(ulong Old,ulong New)>();
                var changes=await Project(pair,m,from,key,raw.Records!,next.NextUsn,old,ct,paired:paired).ConfigureAwait(false);
                Member[] paused;
                lock(gate) paused=v.Members.Values.Where(x=>x.Paused && x.Pair!=null && x.Read==from &&
                    (x.PausedThrough is null || x.PausedThrough.NextUsn<=from.NextUsn)).ToArray();
                foreach(var other in paused)
                {
                    // The paused pair gets no queue or delivered entry. Stop its coverage at its first relevant record.
                    var passed=from.NextUsn;
                    try
                    {
                        var probe=await Project(other.Pair!,other,from,key,raw.Records!,next.NextUsn,[],ct,true).ConfigureAwait(false);
                        passed=probe.Count==0 ? next.NextUsn : probe[0].Usn!.Value;
                    }
                    catch(Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ChangedUnderneathException or ItemFailedException) { /* Unproved range remains in NTFS. */ }
                    lock(gate) if (other.Paused && other.Read==from) other.Read=other.Settled=new JournalPosition(from.JournalId,passed);
                }
                lock(gate)
                {
                    if (m.Paused || !v.Members.ContainsKey(pair.PairId) || m.Read!=from) throw new OperationCanceledException(ct);
                    m.OldNames=old; m.Paired.AddRange(paired); m.Read=next; v.Current=(from,raw);
                }
                return new(changes,null,next){CaughtUp=end==next};
            }
        }
        finally { v.Read.Release(); }
    }

    private static ChangePage? TakeReset(Volume v,Member m,JournalPosition? end)
    {
        if (v.LastReset is not { } fresh || !(m.ResetDue || end!=null && end.JournalId!=fresh.JournalId)) return null;
        m.ResetDue=false;
        return new([],null,fresh){ContinuityLost=true};
    }

    // USN_REASON bits from winioctl.h; only the three data-change bits establish a content write.
    private const uint Content=7, Create=0x100, Delete=0x200, Old=0x1000, New=0x2000, Links=0x10000, Reparse=0x100000, Close=0x80000000;
    private static async Task<List<Change>> Project(PairConfig pair,Member m,JournalPosition from,string volume,IReadOnlyList<UsnRecord> records,ulong end,
        Dictionary<ulong,(string? Path,string Parent,string? Name,ulong Usn)> old,CancellationToken ct,bool firstOnly=false,List<(ulong Old,ulong New)>? paired=null)
    {
        var result=new List<Change>(); bool owesRepair=false; var scope=m.Scope!(); var store=m.Store!; var root=pair.Roots!.Local.FileId;
        var armed=await store.ArmedAsync(pair.PairId,ct).ConfigureAwait(false);
        bool Excluded(string path,bool folder) => scope.Excludes(path,null,folder) &&
            !(pair.Exclusions.Any(e=>!e.Keep && e.Side==Side.Local && PathNames.IsSelfOrUnder(path,e.Path)) && !scope.Configured.Excludes(path,null,folder));
        var locations=new Dictionary<ulong,(string? Path,IReadOnlyList<string>? Links)>();
        var saved=new Dictionary<string,List<SyncedEntry>>();
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
            var id=LocalIdentity.FileIdText(record.FileId);
            var parent=LocalIdentity.FileIdText(record.ParentId);
            var rows=await Bindings(id).ConfigureAwait(false);
            string? history=null;
            if (record.Name is {Length:>0} name)
            {
                if (record.ParentId==root) history=name;
                else if (await Bindings(parent).ConfigureAwait(false) is [var binding]) history=PathNames.Child(binding.LocalPath,name);
            }
            var victim=armed?.Victim.Proof;
            var affected=victim!=null && victim.VolumeId==volume && (victim.Expected.Id==id || victim.Expected.IsFolder && victim.Expected.Id==parent);
            // OLD is not a change at its former path. Preserve the half until NEW arrives, including across pages.
            if ((flags&Old)!=0 && (flags&New)==0)
            {
                var previous=history ?? (rows.Count==1 ? rows[0].LocalPath : null);
                if (affected || previous!=null || record.ParentId==root || (await Bindings(parent)).Count!=0)
                {
                    if (firstOnly) result.Add(new(Side.Local,ChangeKind.Renamed,null,previous,id,null,false,false,record.Usn,volume,parent,record.Name,JournalId:from.JournalId));
                    else old[record.FileId]=(previous,parent,record.Name,record.Usn);
                }
                continue;
            }
            bool renaming=(flags&New)!=0;
            old.TryGetValue(record.FileId,out var prior);
            if (renaming && old.Remove(record.FileId)) paired?.Add((prior.Usn,record.Usn));
            (string? Path,IReadOnlyList<string>? Links) loc;
            try
            {
                if (!locations.TryGetValue(record.FileId,out loc) || (flags&Links)!=0)
                    locations[record.FileId]=loc=m.Locate!(record.FileId,(flags&Links)!=0,ct);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ChangedUnderneathException or ItemFailedException)
            {
                // Unreadable is not absence. A named affected binding gets its normal typed Failure;
                // redacted input under a known parent needs Repair before this page can settle.
                var known=rows.Select(x=>x.LocalPath).Concat(history is null ? [] : [history]).Distinct(StringComparer.Ordinal).ToArray();
                foreach(var path in known.Where(path=>affected || !Excluded(path,folder)))
                {
                    if (firstOnly) result.Add(new(Side.Local,ChangeKind.Changed,path,null,id,null,false,false,record.Usn,JournalId:from.JournalId));
                    else await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,error is ItemFailedException item ? item.Cause : TextKey.ReadLocalFailed,path,
                        new([new(Side.Local,path,id)],Side.Local),DateTimeOffset.UtcNow,false),ct).ConfigureAwait(false);
                }
                if (known.Length==0 && (affected || record.ParentId==root || (await Bindings(parent)).Count!=0))
                {
                    if (firstOnly) result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,false,false,record.Usn,JournalId:from.JournalId));
                    else owesRepair=true;
                }
                continue;
            }
            var paths=new HashSet<string>(StringComparer.Ordinal);
            foreach(var full in loc.Links ?? (loc.Path is null ? Array.Empty<string>() : [loc.Path]))
                if(PathNames.RelativeUnderLocalRoot(pair.LocalRoot,full) is { } path) paths.Add(path);
            // Exact surviving aliases cannot be the predecessor of a different new link.
            var savedPaths=rows.Select(row=>row.LocalPath).ToHashSet(StringComparer.Ordinal);
            var missing=rows.Where(row=>!paths.Contains(row.LocalPath)).ToArray();
            var added=paths.Where(path=>!savedPaths.Contains(path)).ToArray();
            string? source=prior.Path!=null && !paths.Contains(prior.Path) ? prior.Path : missing.Length==1 ? missing[0].LocalPath : null;
            string? destination=history!=null && paths.Contains(history) ? history : added.Length==1 ? added[0] : null;
            bool matched=renaming && source!=null && destination!=null && !savedPaths.Contains(destination);
            var seen=new HashSet<string>(StringComparer.Ordinal);
            void Add(string? path,string? oldPath,ChangeKind kind,Entry? item,bool deleted)
            {
                if (path!=null && (!seen.Add(path) || !folder && StageNames.IsStage(PathNames.Name(path)))) return;
                if (!affected && (path is null || Excluded(path,folder) && (oldPath is null || Excluded(oldPath,folder)))) return;
                result.Add(new(Side.Local,kind,path,oldPath,id,item,(flags&Content)!=0,deleted,record.Usn,volume,parent,record.Name,renaming?prior.Parent:null,renaming?prior.Name:null,from.JournalId));
            }
            if (renaming && history!=null && prior.Path!=null)
            {
                // NEW names the physical hop even when the file has already moved farther.
                var observed=paths.Contains(history) ? m.Entry!(history) : null;
                Add(history,prior.Path,ChangeKind.Renamed,observed?.Id==id ? observed : null,false);
                continue;
            }
            foreach(var path in paths)
            {
                Entry? entry;
                try { entry=m.Entry!(path); }
                catch(Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ChangedUnderneathException or ItemFailedException)
                { Add(path,null,ChangeKind.Changed,null,false); continue; }
                if (entry?.Id!=id) { Add(path,null,ChangeKind.Changed,null,false); continue; }
                var previous=matched && path==destination ? source : null;
                Add(path,previous,previous!=null && previous!=path ? ChangeKind.Renamed : ChangeKind.Changed,entry,false);
            }
            foreach(var row in missing.Where(b=>!matched || source!=b.LocalPath))
                Add(row.LocalPath,null,ChangeKind.Deleted,null,(flags&Delete)!=0 && loc.Path is null);
            if (history!=null && paths.Count==0 && rows.Count==0 && (flags&Delete)==0)
                Add(history,null,ChangeKind.Changed,null,false);
            if ((flags&Delete)!=0 && history!=null && paths.Count==0) Add(history,null,ChangeKind.Deleted,null,loc.Path is null);
            if (affected && seen.Count==0) Add(history,null,ChangeKind.Changed,null,false);
            if (history==null && rows.Count==0 && paths.Count==0 && !affected &&
                (record.ParentId==root || (await Bindings(parent).ConfigureAwait(false)).Count!=0))
            {
                if (firstOnly) result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,false,false,record.Usn,volume,parent,JournalId:from.JournalId));
                else owesRepair=true;
            }

        }
        if (owesRepair) await store.OweRepairAsync(pair.PairId,Side.Local,Text.Get(TextKey.JournalRepairRequired),ct).ConfigureAwait(false);
        return result;
    }
}

/// <summary>Intake held the source prefix because the configured root itself changed.</summary>
public sealed class JournalRootChangedException(string root) : ChangedUnderneathException(root);
