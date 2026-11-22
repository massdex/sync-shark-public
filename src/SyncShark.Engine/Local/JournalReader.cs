using System.Globalization;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>One reader per host shares native pages between members of a volume. Only the common settled prefix is durable.</summary>
public sealed class JournalReader
{
    private readonly object gate = new();
    private readonly Dictionary<string, Volume> volumes = [];
    private readonly UsnJournal journal = new();
    private sealed class Volume
    {
        public readonly Dictionary<string, Member> Members = [];
        public readonly SemaphoreSlim Read = new(1), Save = new(1);
        public readonly Dictionary<JournalPosition,NtfsUsnPage> Pages = [];
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
        public bool Paused;
        public IStore? Store;
        public Func<PathScope>? Scope;
        public Func<ulong,bool,CancellationToken,(string? Path,IReadOnlyList<string>? Links)>? Locate;
        public Func<string,Entry?>? Entry;
        public Dictionary<ulong,(string? Path,string Parent,string? Name)> OldNames = [];
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
    public void Membership(PairConfig pair,bool paused,bool removed=false)
    {
        lock(gate)
        {
            if (!volumes.TryGetValue(Domain(pair),out var v) || !v.Members.TryGetValue(pair.PairId,out var member)) return;
            if (removed) v.Members.Remove(pair.PairId);
            else if (member.Paused!=paused) { member.PausedThrough=paused ? member.Read : null; member.Paused=paused; member.Read=member.Settled; member.OldNames=[]; }
            Trim(v);
        }
    }

    private static void Trim(Volume v)
    {
        foreach(var start in v.Pages.Keys.Where(k=>!v.Members.Values.Any(m=>!m.Paused && m.Read is { } pos && pos.JournalId==k.JournalId && pos.NextUsn<=k.NextUsn)).ToArray()) v.Pages.Remove(start);
    }

    public async Task SettleAsync(PairConfig pair,JournalPosition position,IStore store,CancellationToken ct)
    {
        var key=Domain(pair); Volume v; lock(gate) v=volumes[key];
        await v.Save.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            JournalPosition common;
            lock(gate)
            {
                if (!v.Members.TryGetValue(pair.PairId,out var m) || (m.PausedThrough ?? m.Read) is not { } limit || limit.JournalId!=position.JournalId || position.NextUsn>limit.NextUsn) return;
                if (m.Settled is null || position.NextUsn>m.Settled.NextUsn) { m.Settled=position; if (m.Paused) m.Read=position; }
                if (v.Members.Values.Any(x=>x.Settled?.JournalId!=position.JournalId)) return;
                common=v.Members.Values.Select(x=>x.Settled!).MinBy(x=>x.NextUsn)!;
                if (v.Saved is not null && v.Saved.NextUsn>=common.NextUsn) return;
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
            JournalPosition from; NtfsUsnPage? raw; Dictionary<ulong,(string? Path,string Parent,string? Name)> old;
            lock(gate)
            {
                if (m.Paused || !v.Members.ContainsKey(pair.PairId)) throw new OperationCanceledException(ct);
                from=m.Read!; old=new(m.OldNames);
                if (end?.JournalId==from.JournalId && end.NextUsn<=from.NextUsn) return new([],null,from){CaughtUp=true};
                v.Pages.TryGetValue(from,out raw);
            }
            if (raw is null)
            {
                // A live wait cannot own the projection lock: another pair may already have a finite range to replay.
                var range=(from,end);
                if (!v.Reads.TryGetValue(range,out var pending))
                {
                    var stop=new CancellationTokenSource();
                    pending=new(stop,journal.ReadAsync(pair.LocalRoot,from,end,settings.JournalPageBufferBytes,stop.Token));
                    v.Reads.Add(range,pending);
                }
                pending.Readers++;
                v.Read.Release();
                try { raw=await pending.Page.WaitAsync(ct).ConfigureAwait(false); }
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
                lock(gate) if (m.Read!=from || m.Paused || !v.Members.ContainsKey(pair.PairId)) throw new OperationCanceledException(ct);
            }
            if (raw.Status is NtfsUsnPageStatus.HistoryLost or NtfsUsnPageStatus.JournalChanged)
            {
                var fresh=journal.Current(pair.LocalRoot);
                await v.Save.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    string[] members; lock(gate) members=v.Members.Keys.ToArray();
                    await store.ResetJournalAsync(key,fresh,members,Text.Get(TextKey.JournalRepairRequired),ct).ConfigureAwait(false);
                    lock(gate) { v.Saved=fresh; v.Pages.Clear(); foreach(var x in v.Members.Values) { x.Read=x.Settled=fresh; x.PausedThrough=null; x.OldNames=[]; } }
                }
                finally { v.Save.Release(); }
                return new([],null,fresh){ContinuityLost=true};
            }
            if (raw.Status is not (NtfsUsnPageStatus.Complete or NtfsUsnPageStatus.More) || raw.ConsumedNextUsn<=from.NextUsn) throw UsnJournal.Failure(raw.Status,raw.NativeError);
            var next=new JournalPosition(from.JournalId,Math.Min(end?.NextUsn ?? ulong.MaxValue,raw.ConsumedNextUsn));
            var changes=await Project(pair,m,from,key,raw.Records!,next.NextUsn,old,ct).ConfigureAwait(false);
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
                m.OldNames=old; m.Read=next;
                if (v.Members.Values.Any(x=>!x.Paused && x.Read is { } cursor && cursor.JournalId==from.JournalId && cursor.NextUsn<=from.NextUsn)) v.Pages[from]=raw;
                else v.Pages.Remove(from);
            }
            return new(changes,null,next){CaughtUp=end==next};
        }
        finally { v.Read.Release(); }
    }

    // USN_REASON bits from winioctl.h; only the three data-change bits establish a content write.
    private const uint Content=7, Create=0x100, Delete=0x200, Old=0x1000, New=0x2000, Links=0x10000, Reparse=0x100000, Close=0x80000000;
    private static async Task<List<Change>> Project(PairConfig pair,Member m,JournalPosition from,string volume,IReadOnlyList<UsnRecord> records,ulong end,
        Dictionary<ulong,(string? Path,string Parent,string? Name)> old,CancellationToken ct,bool firstOnly=false)
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
                if ((flags&(Delete|Old|New|Reparse))!=0) throw new ChangedUnderneathException(pair.LocalRoot);
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
            (string? Path,IReadOnlyList<string>? Links) loc;
            try
            {
                if (!locations.TryGetValue(record.FileId,out loc) || (flags&Links)!=0)
                    locations[record.FileId]=loc=m.Locate!(record.FileId,(flags&Links)!=0,ct);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ChangedUnderneathException or ItemFailedException)
            {
                // A saved binding is still actionable, but a failed alias lookup never proves that binding deleted.
                var known=rows.Select(x=>x.LocalPath).Concat(history is null ? [] : [history]).Distinct(StringComparer.Ordinal).ToArray();
                foreach(var path in known)
                    result.Add(new(Side.Local,ChangeKind.Changed,path,null,id,null,(flags&Content)!=0,false,record.Usn,volume,parent,record.Name,JournalId:from.JournalId));
                if (known.Length==0 && affected) result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,(flags&Content)!=0,false,record.Usn,volume,parent,record.Name,JournalId:from.JournalId));
                continue;
            }
            var paths=new HashSet<string>(StringComparer.Ordinal);
            foreach(var full in loc.Links ?? (loc.Path is null ? Array.Empty<string>() : [loc.Path]))
                if(PathNames.RelativeUnderLocalRoot(pair.LocalRoot,full) is { } path) paths.Add(path);
            if ((flags&Old)!=0) old[record.FileId]=(history ?? (rows.Count==1 ? rows[0].LocalPath : null),parent,record.Name);
            if ((flags&Old)!=0 && (flags&New)==0)
            {
                // Carry the physical old hop, not a guessed move to today's newest alias. A later new hop may be on another page.
                if (affected || history!=null || rows.Count!=0)
                    result.Add(new(Side.Local,ChangeKind.Changed,old[record.FileId].Path,null,id,null,(flags&Content)!=0,false,record.Usn,volume,parent,record.Name,parent,record.Name,from.JournalId));
                continue;
            }
            bool renaming=(flags&New)!=0;
            old.TryGetValue(record.FileId,out var prior);
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
                if (!affected && (path is null || Excluded(path,folder) && (oldPath is null || Excluded(oldPath,folder)) || item?.Skip!=null)) return;
                result.Add(new(Side.Local,kind,path,oldPath,id,item,(flags&Content)!=0,deleted,record.Usn,volume,parent,record.Name,renaming?prior.Parent:null,renaming?prior.Name:null,from.JournalId));
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
            if ((flags&Delete)!=0 && history!=null && paths.Count==0) Add(history,null,ChangeKind.Deleted,null,loc.Path is null);
            if (affected && seen.Count==0) Add(history,null,ChangeKind.Changed,null,false);
            if (history==null && rows.Count==0 && paths.Count==0 && !affected &&
                (record.ParentId==root || (await Bindings(parent).ConfigureAwait(false)).Count!=0))
            {
                if (firstOnly) result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,false,false,record.Usn,volume,parent,JournalId:from.JournalId));
                else owesRepair=true;
            }
            if ((flags&New)!=0) old.Remove(record.FileId);
        }
        if (owesRepair) await store.OweRepairAsync(pair.PairId,Side.Local,Text.Get(TextKey.JournalRepairRequired),ct).ConfigureAwait(false);
        return result;
    }
}
