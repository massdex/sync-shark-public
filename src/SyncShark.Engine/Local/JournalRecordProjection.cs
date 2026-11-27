using static Massdex.SyncShark.Engine.Local.JournalProjection;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>One record: its name halves, its physical hop and the links it changed against the saved bindings.</summary>
internal sealed class JournalRecordProjection
{
    // The record's current links against its saved bindings; only the steps after the location step use them.
    private readonly record struct Aliases(HashSet<string> Paths,HashSet<string> Saved,SyncedEntry[] Missing,string? Source,string? Destination,bool Matched,HashSet<string> Seen);
    private readonly JournalProjection p;
    private readonly UsnRecord record;
    private readonly string id, parent;
    private readonly List<SyncedEntry> rows;
    private readonly string? history;
    private readonly uint flags;
    private readonly bool folder, affected, oldOnly, renaming, hasPrior, dirty, namedHop;
    private readonly ChangeKind pathlessKind;
    private readonly JournalReader.Half prior;
    private bool outsideHop, unresolved;
    private (string? Path,IReadOnlyList<string>? Links) loc;

    public JournalRecordProjection(JournalProjection p,UsnRecord record,string id,string parent,List<SyncedEntry> rows,string? history)
    {
        (this.p,this.record,this.id,this.parent,this.rows,this.history)=(p,record,id,parent,rows,history);
        flags=record.Reason; folder=(record.Attributes&FileAttributes.Directory)!=0;
        var victim=p.victim;
        affected=victim!=null && victim.VolumeId==p.volume && (victim.Expected.Id==id || victim.Expected.IsFolder && victim.Expected.Id==parent);
        pathlessKind=(flags&Delete)!=0 ? ChangeKind.Deleted : ChangeKind.Changed;
        oldOnly=(flags&Old)!=0 && (flags&New)==0;
        if (oldOnly) return;
        renaming=(flags&New)!=0;
        hasPrior=p.old.TryGetValue(record.FileId,out prior);
        dirty=(flags&Content)!=0 || renaming && hasPrior && prior.Dirty;
        namedHop=renaming && hasPrior && prior.Path!=null && history!=null;
    }

    // Not async: it returns the one awaiting step's task, so a suspended store call adds no frame for this dispatch.
    public Task ProjectAsync()
    {
        if (oldOnly) return OldNameAsync();
        // An unbound file whose OLD half has no path has no lineage to lose: its rename is a change at its current path (tmp renamed over name).
        if(renaming && history==null && (hasPrior && (prior.Path!=null || folder) || rows.Count!=0))
        {
            outsideHop=record.Name!=null && OutsideHop();
            if(!outsideHop && !AtBindings()) { UnclassifiedHop(); return Task.CompletedTask; }
        }
        if (hasPrior && (renaming || prior.BeforeOld && (flags&Delete)!=0))
        { p.old.Remove(record.FileId); p.paired?.Add(new(prior.Usn,record.Usn,record.FileId,renaming && folder?history:null)); }
        if(namedHop && NamedHop()) return Task.CompletedTask;
        if(Locate() is { } error) return LocateFailedAsync(error);
        var aliases=MatchPaths();
        if (!renaming && (flags&Delete)==0 && UnmovedChange(aliases)) return Task.CompletedTask;
        return CurrentPathsAsync(aliases);
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

    // A hop that already ends at exactly the id's saved names (a Repair relinked them) leaves nothing to reconstruct.
    private bool AtBindings()
    {
        if (rows.Count==0 || Locate() is not null) return false;
        var aliases=MatchPaths(); return aliases.Paths.SetEquals(aliases.Saved);
    }

    // A redacted intermediate destination cannot be reconstructed from today's final alias.
    private void UnclassifiedHop()
    {
        if(p.firstOnly) { p.result.Add(new(Side.Local,ChangeKind.Renamed,null,prior.Path,id,null,false,false,record.Usn,JournalId:p.from.JournalId)); return; }
        // A later acknowledgement cannot reconstruct the OLD half of an unclassified hop.
        var origin=hasPrior ? prior.Usn : record.Usn;
        p.Uncertain(origin,id,prior.Path ?? record.Name,nameof(UnclassifiedHop));
        p.old.Remove(record.FileId);
    }

    // A physical hop is known before lookup. Metadata errors cannot change it into independent path work.
    private bool NamedHop()
    {
        Entry? item=null;
        try { item=p.m.Entry!(history!); }
        catch(Exception e) when(MetadataFailure(e) && !BoundRootChanged(e,p.pair)) { /* The ordered rename owns the retry, including both endpoints. */ }
        if (folder) p.movedParents[record.FileId]=history!;
        if(!IsStagePath(history!) && (affected || rows.Any(r=>PathNames.Same(r.LocalPath,prior.Path) && p.scope.KeepCovers(Side.Local,r.LocalKey)) || !(p.Excluded(history!,folder) && p.Excluded(prior.Path!,folder))))
            p.result.Add(new(Side.Local,ChangeKind.Renamed,history,prior.Path,id,item?.Id==id?item:null,dirty,false,record.Usn,p.volume,parent,record.Name,prior.Parent,prior.Name,p.from.JournalId));
        return !dirty || folder;
    }

    // A metadata failure is returned, not thrown: the caller retains it without a forwarding frame.
    private Exception? Locate()
    {
        try
        {
            if (!p.locations.TryGetValue(record.FileId,out loc) || namedHop || (flags&Links)!=0)
                p.locations[record.FileId]=loc=p.m.Locate!(record.FileId,(flags&Links)!=0,p.ct);
            return null;
        }
        catch (Exception error) when (MetadataFailure(error) && !BoundRootChanged(error,p.pair)) { return error; }
    }

    // Unreadable is not absence. A named affected binding gets its normal typed Failure;
    // redacted input under a known parent needs Repair before this page can settle.
    private async Task LocateFailedAsync(Exception error)
    {
        var known=namedHop ? rows.Select(x=>x.LocalPath).Where(path=>path!=prior.Path && path!=history).ToArray() :
            rows.Select(x=>x.LocalPath).Concat(history is null ? [] : [history]).Distinct(StringComparer.Ordinal).ToArray();
        foreach(var path in known.Where(path=>affected || !p.Excluded(path,folder)))
        {
            await RetainFailure(path,error).ConfigureAwait(false);
        }
        if (known.Length==0 && (affected || record.ParentId==p.root || (await p.BindingsAsync(parent)).Count!=0))
        {
            if (affected) RecoveryEvidence(absent:false,incomplete:true);
            else if (p.firstOnly) p.result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,false,false,record.Usn,JournalId:p.from.JournalId));
            if(!p.firstOnly) p.Uncertain(record.Usn,id,history ?? record.Name,Failure(nameof(LocateFailedAsync),error));
        }
    }

    // Not async: the caller awaits the notice write directly. A failed write fails the whole page, so the order of the
    // in-memory updates against it is not observable.
    private Task RetainFailure(string path,Exception error)
    {
        if(IsStagePath(path)) return Task.CompletedTask;
        // The queue orders footprints, not notices. Preserve the original ID and operation for its decisive retry.
        p.result.Add(new(Side.Local,pathlessKind,path,null,id,null,dirty,false,
            record.Usn,p.volume,parent,record.Name,JournalId:p.from.JournalId));
        if(p.firstOnly) return Task.CompletedTask;
        p.Uncertain(record.Usn,id,path,Failure(nameof(RetainFailure),error));
        var cause=error is ItemFailedException item ? item.Cause : TextKey.ReadLocalFailed;
        return p.store.SaveNoticeAsync(p.pair.PairId,new(0,NoticeKind.Failure,cause,path,
            new([new(Side.Local,path,id)],Side.Local),DateTimeOffset.UtcNow,false),p.ct);
    }

    // The branch and the lookup error's type and code, for the uncertain record's run-log line.
    private static string Failure(string branch,Exception error) => branch+" "+error.GetType().Name+" 0x"+error.HResult.ToString("X8",System.Globalization.CultureInfo.InvariantCulture);

    private void RecoveryEvidence(bool absent,bool incomplete)
    {
        if(!affected) return;
        p.result.Add(new Change(Side.Local,pathlessKind,null,null,id,null,(flags&Content)!=0,
            pathlessKind==ChangeKind.Deleted && absent,record.Usn,p.volume,parent,record.Name,JournalId:p.from.JournalId)
            { AffectedPath=p.victim!.Expected.Path });
        if(incomplete && !p.firstOnly) p.Uncertain(record.Usn,id,record.Name,nameof(RecoveryEvidence));
    }

    // Exact surviving aliases cannot be the predecessor of a different new link.
    private Aliases MatchPaths()
    {
        var paths=new HashSet<string>(StringComparer.Ordinal);
        foreach(var full in loc.Links ?? (loc.Path is { } only ? [only] : Array.Empty<string>()))
            if(PathNames.RelativeUnderLocalRoot(p.pair.LocalRoot,full) is { } path) paths.Add(path);
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
        var source=prior.Path!=null && !paths.Contains(prior.Path) ? prior.Path : missing.Length==1 ? missing[0].LocalPath : null;
        var destination=history!=null && paths.Contains(history) ? history : added.Length==1 ? added[0] : null;
        var matched=renaming && !namedHop && !outsideHop && source!=null && destination!=null && !savedPaths.Contains(destination);
        return new(paths,savedPaths,missing,source,destination,matched,new HashSet<string>(StringComparer.Ordinal));
    }

    private void Add(Aliases aliases,string? path,string? oldPath,ChangeKind kind,Entry? item,bool deleted)
    {
        if(path==null) { unresolved=true; return; }
        if (!aliases.Seen.Add(path) || IsStagePath(path)) return;
        if (!affected && p.Excluded(path,folder) && (oldPath is null || p.Excluded(oldPath,folder))) return;
        p.result.Add(new Change(Side.Local,kind,path,oldPath,id,item,dirty,deleted,record.Usn,p.volume,parent,record.Name,kind==ChangeKind.Renamed?prior.Parent:null,kind==ChangeKind.Renamed?prior.Name:null,p.from.JournalId));
    }

    private void DeferMissing(Aliases aliases,string? path)
    {
        if(p.firstOnly)
        {
            var before=p.result.Count;
            if ((path ?? history) is { } known) Add(aliases,known,null,ChangeKind.Changed,null,false);
            if (p.result.Count==before && aliases.Missing.Any(row=>!IsStagePath(row.LocalPath) && (affected || !p.Excluded(row.LocalPath,folder))))
                // The probe needs the USN, not a guess at which saved link changed.
                p.result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,dirty,false,record.Usn,JournalId:p.from.JournalId));
            return;
        }
        if(p.old.TryGetValue(record.FileId,out var held)) { if(dirty) p.old[record.FileId]=held with {Dirty=true}; return; }
        p.old[record.FileId]=new(path,parent,record.Name,record.Usn,dirty,true); p.starts!.Add(record.Usn); p.defer!(record.FileId,record.Usn);
    }

    // A name now held by another file is a replacement: it keeps the ordinary deferral and compare.
    private bool Vacant(string path) { try { return p.m.Entry!(path)==null; } catch(Exception error) when (MetadataFailure(error)) { return false; } }

    // Not a name operation: only a moved parent or newer metadata explains a saved binding missing from the links.
    private bool UnmovedChange(Aliases aliases)
    {
        bool parentMoved=p.movedParents.ContainsKey(record.ParentId)||p.physical.ContainsKey(record.ParentId);
        if (parentMoved && history!=null)
        {
            Add(aliases,history,null,ChangeKind.Changed,null,false);
            if(loc.Links==null) return true;
            // Keep independent hard links, never substitute the later spelling of this same binding.
            aliases.Paths.RemoveWhere(path=>!aliases.Saved.Contains(path));
        }
        if (aliases.Missing.Length!=0 && !parentMoved)
        {
            // A link change of a known hard-linked file: a bound name missing from its exact current names was removed.
            if ((flags&Links)!=0 && rows.Count>1 && !affected && loc.Links!=null && aliases.Missing.All(gone=>Vacant(gone.LocalPath))) foreach(var gone in aliases.Missing) Add(aliases,gone.LocalPath,null,ChangeKind.Deleted,null,false);
            // Metadata is newer than this record. Only a name operation can remove/move a saved binding.
            else DeferMissing(aliases,aliases.Missing.Length==1?aliases.Missing[0].LocalPath:null);
            aliases.Paths.RemoveWhere(path=>!aliases.Saved.Contains(path) && ((flags&Links)==0 || path!=history));
        }
        return false;
    }

    // The current links, then the remainder in the same frame.
    private async Task CurrentPathsAsync(Aliases aliases)
    {
        foreach(var path in aliases.Paths)
        {
            if (IsStagePath(path))
            {
                // Both names are known stages: the arm has no unresolved user binding at this alias.
                if (history!=null && IsStagePath(history)) aliases.Seen.Add(path);
                continue;
            }
            Entry? entry;
            try { entry=p.m.Entry!(path); }
            catch(Exception error) when (MetadataFailure(error) && !BoundRootChanged(error,p.pair))
            {
                if(!p.Excluded(path,folder) || affected)
                {
                    if(affected && history!=path && !aliases.Saved.Contains(path)) unresolved=true;
                    else { await RetainFailure(path,error).ConfigureAwait(false); aliases.Seen.Add(path); }
                }
                continue;
            }
            if (entry?.Id!=id)
            {
                if(aliases.Saved.Contains(path) && !renaming && (flags&Delete)==0) DeferMissing(aliases,path);
                else if(affected) unresolved=true;
                continue;
            }
            var previous=aliases.Matched && path==aliases.Destination ? aliases.Source : null;
            Add(aliases,path,previous,previous!=null && previous!=path ? ChangeKind.Renamed : ChangeKind.Changed,entry,false);
        }
        await RemainderAsync(aliases).ConfigureAwait(false);
    }

    // Deletions, an unbound name and the arm's evidence that the current links did not cover.
    private async Task RemainderAsync(Aliases aliases)
    {
        if ((flags&Delete)!=0 || outsideHop) foreach(var row in aliases.Missing.Where(b=>outsideHop ? b.LocalPath==aliases.Source : loc.Path==null || b.LocalPath==history))
            Add(aliases,row.LocalPath,null,ChangeKind.Deleted,null,(flags&Delete)!=0 && loc.Path is null);
        if (!namedHop && !affected && history!=null && aliases.Paths.Count==0 && rows.Count==0 && (flags&Delete)==0)
            Add(aliases,history,null,ChangeKind.Changed,null,false);
        if ((flags&Delete)!=0 && history!=null && aliases.Paths.Count==0) Add(aliases,history,null,ChangeKind.Deleted,null,loc.Path is null);
        if (!namedHop && affected && (aliases.Seen.Count==0 || unresolved) &&
            (record.Name==null || !IsStagePath(record.Name) || rows.Any(row=>!IsStagePath(row.LocalPath)) || loc.Path!=null && !IsStagePath(Path.GetFileName(loc.Path)) || loc.Links?.Any(full=>!IsStagePath(Path.GetFileName(full)))==true))
            RecoveryEvidence(absent:loc.Path is null && loc.Links is not {Count:>0},incomplete:unresolved);
        if (history==null && rows.Count==0 && aliases.Paths.Count==0 && !affected &&
            (record.ParentId==p.root || (await p.BindingsAsync(parent).ConfigureAwait(false)).Count!=0))
        {
            if (p.firstOnly) p.result.Add(new(Side.Local,ChangeKind.Changed,null,null,id,null,false,false,record.Usn,p.volume,parent,JournalId:p.from.JournalId));
            // A retired id (no current path) has no lineage to lose; its successor's own records carry any later state.
            else if (loc.Path!=null) p.Uncertain(record.Usn,id,record.Name,nameof(RemainderAsync));
        }
    }
}
