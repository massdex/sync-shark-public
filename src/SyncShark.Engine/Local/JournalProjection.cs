using System.Collections.Immutable;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Projects one native page into a member's ordered changes; name halves, holds and failures go back to the reader.</summary>
internal sealed class JournalProjection
{
    // USN_REASON bits from winioctl.h; only the three data-change bits establish a content write.
    private const uint Content=7, Create=0x100, Delete=0x200, Old=0x1000, New=0x2000, Links=0x10000, Reparse=0x100000, Close=0x80000000;
    private readonly PairConfig pair;
    private readonly JournalReader.Member m;
    private readonly JournalPosition from;
    private readonly string volume;
    private readonly ImmutableDictionary<ulong,JournalReader.Half>.Builder old;
    private readonly ImmutableDictionary<ulong,JournalReader.Hop> physical;
    private readonly CancellationToken ct;
    private readonly bool firstOnly;
    private readonly List<JournalReader.Hop>? paired;
    private readonly List<ulong>? starts;
    private readonly Action<ulong,ulong>? defer;
    private readonly PathScope scope;
    private readonly IStore store;
    private readonly ulong root;
    private readonly List<Change> result=new();
    private readonly Dictionary<ulong,(string? Path,IReadOnlyList<string>? Links)> locations=new();
    private readonly Dictionary<string,List<SyncedEntry>> saved=new();
    private readonly Dictionary<ulong,string> movedParents=new();
    private RemovalProof? victim;
    private ulong? failedUsn;

    private JournalProjection(PairConfig pair,JournalReader.Member m,JournalPosition from,string volume,ImmutableDictionary<ulong,JournalReader.Half>.Builder old,
        ImmutableDictionary<ulong,JournalReader.Hop> physical,CancellationToken ct,bool firstOnly,List<JournalReader.Hop>? paired,List<ulong>? starts,Action<ulong,ulong>? defer)
    {
        (this.pair,this.m,this.from,this.volume,this.old,this.physical,this.ct)=(pair,m,from,volume,old,physical,ct);
        (this.firstOnly,this.paired,this.starts,this.defer)=(firstOnly,paired,starts,defer);
        scope=m.Scope!(); store=m.Store!; root=pair.Roots!.Local.FileId;
    }

    /// <summary>Project the records before end. A first-only probe stops at the first relevant change and keeps no half, hold or failure.</summary>
    internal static async Task<(List<Change> Changes,bool Uncertain,ulong? FailedUsn)> Project(PairConfig pair,JournalReader.Member m,JournalPosition from,string volume,IReadOnlyList<UsnRecord> records,ulong end,
        ImmutableDictionary<ulong,JournalReader.Half>.Builder old,ImmutableDictionary<ulong,JournalReader.Hop> physical,CancellationToken ct,bool firstOnly=false,List<JournalReader.Hop>? paired=null,List<ulong>? starts=null,Action<ulong,ulong>? defer=null)
    {
        var page=new JournalProjection(pair,m,from,volume,old,physical,ct,firstOnly,paired,starts,defer);
        page.victim=(await page.store.ArmedAsync(pair.PairId,ct).ConfigureAwait(false))?.Victim.Proof;
        foreach(var record in records)
        {
            ct.ThrowIfCancellationRequested(); if (record.Usn>=end || firstOnly && page.result.Count!=0) break;
            var flags=record.Reason;
            if ((flags & (Content|Create|Delete|Old|New|Links|Reparse|Close))==0) continue;
            if (record.FileId==page.root)
            {
                if ((flags&(Delete|Old|New|Reparse))!=0) throw new JournalRootChangedException(pair.LocalRoot);
                continue;
            }
            var id=LocalIdentity.FileIdText(record.FileId);
            var parent=LocalIdentity.FileIdText(record.ParentId);
            var rows=await page.BindingsAsync(id).ConfigureAwait(false);
            string? history=null;
            if (record.Name is {Length:>0} name)
            {
                if (record.ParentId==page.root) history=name;
                else if (page.movedParents.TryGetValue(record.ParentId,out var folderPath)) history=PathNames.Child(folderPath,name);
                else if (physical.TryGetValue(record.ParentId,out var parentHop)) history=PathNames.Child(parentHop.FolderPath!,name);
                else if (await page.BindingsAsync(parent).ConfigureAwait(false) is [var binding]) history=PathNames.Child(binding.LocalPath,name);
            }
            await new RecordProjection(page,record,id,parent,rows,history).ProjectAsync().ConfigureAwait(false);
        }
        return (page.result,page.failedUsn.HasValue,page.failedUsn);
    }

    internal static bool MetadataFailure(Exception e) => e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ChangedUnderneathException or ItemFailedException;
    private static bool BoundRootChanged(Exception error,PairConfig pair) => error is ChangedUnderneathException changed && PathNames.Same(changed.Path,pair.LocalRoot);

    private bool Excluded(string path,bool folder) => scope.Excludes(path,null,folder) &&
        !(pair.Exclusions.Any(e=>!e.Keep && e.Side==Side.Local && PathNames.IsSelfOrUnder(path,e.Path)) && !scope.Configured.Excludes(path,null,folder));

    private async Task<List<SyncedEntry>> BindingsAsync(string id)
    {
        if (saved.TryGetValue(id,out var bindings)) return bindings;
        bindings=[]; await foreach(var row in store.ByIdAsync(pair.PairId,Side.Local,id,ct)) bindings.Add(row);
        return saved[id]=bindings;
    }

    /// <summary>One record: its name halves, its physical hop and the links it changed against the saved bindings.</summary>
    private sealed class RecordProjection
    {
        private readonly JournalProjection p;
        private readonly UsnRecord record;
        private readonly string id, parent;
        private readonly List<SyncedEntry> rows;
        private readonly string? history;
        private readonly uint flags;
        private readonly bool folder, affected;
        private readonly ChangeKind pathlessKind;
        private bool renaming, hasPrior, dirty, namedHop, outsideHop, matched, unresolved;
        private JournalReader.Half prior;
        private (string? Path,IReadOnlyList<string>? Links) loc;
        // MatchPaths assigns these before any reader runs.
        private HashSet<string> paths=null!, savedPaths=null!, seen=null!;
        private SyncedEntry[] missing=null!;
        private string? source, destination;

        public RecordProjection(JournalProjection p,UsnRecord record,string id,string parent,List<SyncedEntry> rows,string? history)
        {
            (this.p,this.record,this.id,this.parent,this.rows,this.history)=(p,record,id,parent,rows,history);
            flags=record.Reason; folder=(record.Attributes&FileAttributes.Directory)!=0;
            var victim=p.victim;
            affected=victim!=null && victim.VolumeId==p.volume && (victim.Expected.Id==id || victim.Expected.IsFolder && victim.Expected.Id==parent);
            pathlessKind=(flags&Delete)!=0 ? ChangeKind.Deleted : ChangeKind.Changed;
        }

        public async Task ProjectAsync()
        {
            if ((flags&Old)!=0 && (flags&New)==0) { await OldNameAsync().ConfigureAwait(false); return; }
            renaming=(flags&New)!=0;
            hasPrior=p.old.TryGetValue(record.FileId,out prior);
            dirty=(flags&Content)!=0 || renaming && hasPrior && prior.Dirty;
            namedHop=renaming && hasPrior && prior.Path!=null && history!=null;
            if(renaming && history==null && (hasPrior || rows.Count!=0))
            {
                outsideHop=record.Name!=null && OutsideHop();
                if(!outsideHop) { UnclassifiedHop(); return; }
            }
            if (hasPrior && (renaming || prior.BeforeOld && (flags&Delete)!=0))
            { p.old.Remove(record.FileId); p.paired?.Add(new(prior.Usn,record.Usn,record.FileId,renaming && folder?history:null)); }
            if(namedHop && NamedHop()) return;
            if(!await LocateAsync().ConfigureAwait(false)) return;
            MatchPaths();
            if (!renaming && (flags&Delete)==0 && UnmovedChange()) return;
            await CurrentPathsAsync().ConfigureAwait(false);
            await RemainderAsync().ConfigureAwait(false);
        }

        private bool IsStagePath(string path) => !folder && StageNames.IsStage(PathNames.Name(path));

        // OLD is not a change at its former path. Preserve the half until NEW arrives, including across pages.
        private async Task OldNameAsync()
        {
            var previous=history ?? (rows.Count==1 ? rows[0].LocalPath : null);
            if (!(affected || previous!=null || record.ParentId==p.root || (await p.BindingsAsync(parent)).Count!=0)) return;
            if (p.firstOnly) p.result.Add(new(Side.Local,ChangeKind.Renamed,null,previous,id,null,false,false,record.Usn,p.volume,parent,record.Name,JournalId:p.from.JournalId));
            else if (p.old.TryGetValue(record.FileId,out var earlier)) p.old[record.FileId]=new(previous,parent,record.Name,earlier.Usn,earlier.Dirty || (flags&Content)!=0);
            else { p.old[record.FileId]=new(previous,parent,record.Name,record.Usn,(flags&Content)!=0); p.starts!.Add(record.Usn); }
        }

        // A different final alias alone never proves the physical destination was outside the pair.
        private bool OutsideHop()
        {
            try
            {
                var parentLocation=p.m.Locate!(record.ParentId,false,p.ct);
                return parentLocation.Path!=null && !PathNames.Same(parentLocation.Path,p.pair.LocalRoot) && PathNames.RelativeUnderLocalRoot(p.pair.LocalRoot,parentLocation.Path)==null;
            }
            catch(Exception e) when(MetadataFailure(e) && !BoundRootChanged(e,p.pair)) { return false; /* An unproved hop goes to Repair. */ }
        }

        // A redacted intermediate destination cannot be reconstructed from today's final alias.
        private void UnclassifiedHop()
        {
            if(p.firstOnly) { p.result.Add(new(Side.Local,ChangeKind.Renamed,null,prior.Path,id,null,false,false,record.Usn,JournalId:p.from.JournalId)); return; }
            // A later acknowledgement cannot reconstruct the OLD half of an unclassified hop.
            var origin=hasPrior ? prior.Usn : record.Usn;
            p.failedUsn=Math.Min(p.failedUsn ?? origin,origin);
            p.old.Remove(record.FileId);
        }

        // A physical hop is known before lookup. Metadata errors cannot change it into independent path work.
        private bool NamedHop()
        {
            Entry? item=null;
            try { item=p.m.Entry!(history!); }
            catch(Exception e) when(MetadataFailure(e) && !BoundRootChanged(e,p.pair)) { /* The ordered rename owns the retry, including both endpoints. */ }
            if (folder) p.movedParents[record.FileId]=history!;
            if(!IsStagePath(history!) && (affected || !(p.Excluded(history!,folder) && p.Excluded(prior.Path!,folder))))
                p.result.Add(new(Side.Local,ChangeKind.Renamed,history,prior.Path,id,item?.Id==id?item:null,dirty,false,record.Usn,p.volume,parent,record.Name,prior.Parent,prior.Name,p.from.JournalId));
            return !dirty || folder;
        }

        private async Task<bool> LocateAsync()
        {
            try
            {
                if (!p.locations.TryGetValue(record.FileId,out loc) || namedHop || (flags&Links)!=0)
                    p.locations[record.FileId]=loc=p.m.Locate!(record.FileId,(flags&Links)!=0,p.ct);
                return true;
            }
            catch (Exception error) when (MetadataFailure(error) && !BoundRootChanged(error,p.pair))
            {
                await LocateFailedAsync(error).ConfigureAwait(false);
                return false;
            }
        }

        // Unreadable is not absence. A named affected binding gets its normal typed Failure;
        // redacted input under a known parent needs Repair before this page can settle.
        private async Task LocateFailedAsync(Exception error)
        {
            var known=namedHop ? rows.Select(x=>x.LocalPath).Where(path=>path!=prior.Path && path!=history).ToArray() :
                rows.Select(x=>x.LocalPath).Concat(history is null ? [] : [history]).Distinct(StringComparer.Ordinal).ToArray();
            foreach(var path in known.Where(path=>affected || !p.Excluded(path,folder)))
            {
                await RetainFailureAsync(path,error).ConfigureAwait(false);
            }
            if (known.Length==0 && (affected || record.ParentId==p.root || (await p.BindingsAsync(parent)).Count!=0))
            {
                if (affected) RecoveryEvidence(absent:false,incomplete:true);
                else if (p.firstOnly) p.result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,false,false,record.Usn,JournalId:p.from.JournalId));
                if(!p.firstOnly) p.failedUsn??=record.Usn;
            }
        }

        private async Task RetainFailureAsync(string path,Exception error)
        {
            if(IsStagePath(path)) return;
            if(!p.firstOnly)
            {
                var cause=error is ItemFailedException item ? item.Cause : TextKey.ReadLocalFailed;
                await p.store.SaveNoticeAsync(p.pair.PairId,new(0,NoticeKind.Failure,cause,path,
                    new([new(Side.Local,path,id)],Side.Local),DateTimeOffset.UtcNow,false),p.ct).ConfigureAwait(false);
                p.failedUsn??=record.Usn;
            }
            // The queue orders footprints, not notices. Preserve the original ID and operation for its decisive retry.
            p.result.Add(new(Side.Local,pathlessKind,path,null,id,null,dirty,false,
                record.Usn,p.volume,parent,record.Name,JournalId:p.from.JournalId));
        }

        private void RecoveryEvidence(bool absent,bool incomplete)
        {
            if(!affected) return;
            p.result.Add(new Change(Side.Local,pathlessKind,null,null,id,null,(flags&Content)!=0,
                pathlessKind==ChangeKind.Deleted && absent,record.Usn,p.volume,parent,record.Name,JournalId:p.from.JournalId)
                { AffectedPath=p.victim!.Expected.Path });
            if(incomplete && !p.firstOnly) p.failedUsn??=record.Usn;
        }

        // Exact surviving aliases cannot be the predecessor of a different new link.
        private void MatchPaths()
        {
            paths=new HashSet<string>(StringComparer.Ordinal);
            foreach(var full in loc.Links ?? (loc.Path is { } only ? [only] : Array.Empty<string>()))
                if(PathNames.RelativeUnderLocalRoot(p.pair.LocalRoot,full) is { } path) paths.Add(path);
            savedPaths=rows.Select(row=>row.LocalPath).ToHashSet(StringComparer.Ordinal);
            if (namedHop)
            {
                // If the physical destination is still present, its other live names are independent links.
                // If it has moved again, only exact surviving bindings distinguish links from the later hop.
                bool destinationPresent=paths.Contains(history!);
                paths.RemoveWhere(path=>path==prior.Path || path==history || !destinationPresent && !savedPaths.Contains(path));
            }
            missing=rows.Where(row=>!paths.Contains(row.LocalPath)).ToArray();
            var added=paths.Where(path=>!savedPaths.Contains(path)).ToArray();
            source=prior.Path!=null && !paths.Contains(prior.Path) ? prior.Path : missing.Length==1 ? missing[0].LocalPath : null;
            destination=history!=null && paths.Contains(history) ? history : added.Length==1 ? added[0] : null;
            matched=renaming && !namedHop && !outsideHop && source!=null && destination!=null && !savedPaths.Contains(destination);
            seen=new HashSet<string>(StringComparer.Ordinal);
        }

        private void Add(string? path,string? oldPath,ChangeKind kind,Entry? item,bool deleted)
        {
            if(path==null) { unresolved=true; return; }
            if (!seen.Add(path) || IsStagePath(path)) return;
            if (!affected && p.Excluded(path,folder) && (oldPath is null || p.Excluded(oldPath,folder))) return;
            p.result.Add(new Change(Side.Local,kind,path,oldPath,id,item,dirty,deleted,record.Usn,p.volume,parent,record.Name,kind==ChangeKind.Renamed?prior.Parent:null,kind==ChangeKind.Renamed?prior.Name:null,p.from.JournalId));
        }

        private void DeferMissing(string? path)
        {
            if(p.firstOnly) { Add(path ?? history,null,ChangeKind.Changed,null,false); return; }
            if(p.old.TryGetValue(record.FileId,out var held)) { if(dirty) p.old[record.FileId]=held with {Dirty=true}; return; }
            p.old[record.FileId]=new(path,parent,record.Name,record.Usn,dirty,true); p.starts!.Add(record.Usn); p.defer!(record.FileId,record.Usn);
        }

        // Not a name operation: only a moved parent or newer metadata explains a saved binding missing from the links.
        private bool UnmovedChange()
        {
            bool parentMoved=p.movedParents.ContainsKey(record.ParentId)||p.physical.ContainsKey(record.ParentId);
            if (parentMoved && history!=null)
            {
                Add(history,null,ChangeKind.Changed,null,false);
                if(loc.Links==null) return true;
                // Keep independent hard links, never substitute the later spelling of this same binding.
                paths.RemoveWhere(path=>!savedPaths.Contains(path));
            }
            if (missing.Length!=0 && !parentMoved)
            {
                // Metadata is newer than this record. Only a name operation can remove/move a saved binding.
                DeferMissing(missing.Length==1?missing[0].LocalPath:null);
                paths.RemoveWhere(path=>!savedPaths.Contains(path) && ((flags&Links)==0 || path!=history));
            }
            return false;
        }

        private async Task CurrentPathsAsync()
        {
            foreach(var path in paths)
            {
                if (IsStagePath(path))
                {
                    // Both names are known stages: the arm has no unresolved user binding at this alias.
                    if (history!=null && IsStagePath(history)) seen.Add(path);
                    continue;
                }
                Entry? entry;
                try { entry=p.m.Entry!(path); }
                catch(Exception error) when (MetadataFailure(error) && !BoundRootChanged(error,p.pair))
                {
                    if(!p.Excluded(path,folder) || affected)
                    {
                        if(affected && history!=path && !savedPaths.Contains(path)) unresolved=true;
                        else { await RetainFailureAsync(path,error).ConfigureAwait(false); seen.Add(path); }
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
        }

        // Deletions, an unbound name and the arm's evidence that the current links did not cover.
        private async Task RemainderAsync()
        {
            var loc=this.loc;
            if ((flags&Delete)!=0 || outsideHop) foreach(var row in missing.Where(b=>outsideHop ? b.LocalPath==source : loc.Path==null || b.LocalPath==history))
                Add(row.LocalPath,null,ChangeKind.Deleted,null,(flags&Delete)!=0 && loc.Path is null);
            if (!namedHop && !affected && history!=null && paths.Count==0 && rows.Count==0 && (flags&Delete)==0)
                Add(history,null,ChangeKind.Changed,null,false);
            if ((flags&Delete)!=0 && history!=null && paths.Count==0) Add(history,null,ChangeKind.Deleted,null,loc.Path is null);
            if (!namedHop && affected && (seen.Count==0 || unresolved) &&
                (record.Name==null || !IsStagePath(record.Name) || rows.Any(row=>!IsStagePath(row.LocalPath)) || loc.Path!=null && !IsStagePath(Path.GetFileName(loc.Path)) || loc.Links?.Any(full=>!IsStagePath(Path.GetFileName(full)))==true))
                RecoveryEvidence(absent:loc.Path is null && loc.Links is not {Count:>0},incomplete:unresolved);
            if (history==null && rows.Count==0 && paths.Count==0 && !affected &&
                (record.ParentId==p.root || (await p.BindingsAsync(parent).ConfigureAwait(false)).Count!=0))
            {
                if (p.firstOnly) p.result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,false,false,record.Usn,p.volume,parent,JournalId:p.from.JournalId));
                else p.failedUsn??=record.Usn;
            }
        }
    }
}
