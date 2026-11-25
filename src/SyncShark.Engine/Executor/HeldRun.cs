namespace Massdex.SyncShark.Engine.Executor;

/// <summary>One recovery of the reports held under a scope: keep each subject's latest report, settle it, then acknowledge only settled reports.</summary>
internal sealed class HeldRun(PairConfig pair,Scope scope,Side side,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct,Notice? retry,ActionKind origin,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict,IdentityLocation? actualVictim,bool selectedCloudWindow,DateTimeOffset? capturedAt,bool preRemoval)
{
    internal readonly PairConfig Pair=pair;
    internal readonly Scope Scope=scope;
    internal readonly Side Side=side;
    internal readonly ISide Local=local,Dropbox=dropbox,Source=side==Side.Local?local:dropbox;
    internal readonly IStore Store=store;
    internal readonly Func<Scope,Side,Recovery.Folder,CancellationToken,Task> Populate=populate;
    internal readonly CancellationToken Ct=ct;
    internal readonly ActionKind Origin=origin;
    internal readonly Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? ResolveConflict=resolveConflict;
    internal readonly bool SelectedCloudWindow=selectedCloudWindow,PreRemoval=preRemoval;
    internal readonly string Root=side==Side.Local?scope.LocalPath:scope.DropboxPath;
    internal readonly DateTimeOffset IdentifiedAt=capturedAt??DateTimeOffset.UtcNow;
    /// <summary>The reviewed notice being retried; a candidate replaces it when it re-saves or releases that owner.</summary>
    internal Notice? Retry=retry;
    /// <summary>The positively observed bin occupant: supplied by the caller or found for a reviewed bin subject.</summary>
    internal IdentityLocation? ActualVictim=actualVictim;
    private NoticeItem? subject;
    private readonly IdentityLocation? pinned=retry?.Subject.DropboxVersion;
    private readonly Dictionary<(string Path,string Id),(long Order,Change Report)> latest=[];
    private readonly Dictionary<string,long> pathDeletes=new(PathNames.Comparer);
    private readonly List<((string Path,string Id) Key,Change Report)> acks=[];
    private readonly List<Change> superseded=[];
    private Entry? distinct,echoed;
    private bool complete=true;
    private long order;

    /// <summary>True only when every latest report settled and a retried notice closed.</summary>
    internal async Task<bool> RunAsync()
    {
        // Set here, not in an initializer: a lambda over constructor parameters would add a closure object per run.
        subject=Retry?.Subject.Items.FirstOrDefault(i=>i.Side==Side && PathNames.Same(i.Path,Retry!.Path));
        if(pinned!=null && (Side!=Side.Dropbox || subject==null || pinned.Domain!=Pair.Roots?.Dropbox.RootNamespaceId || pinned.Entry.Id!=subject.Id || pinned.Entry.Path!=subject.Path || pinned.NativePath!=PathNames.RootedDropboxPath(Pair.DropboxRoot,subject.Path))) return false;
        if(!Collect(heldInput(Scope))) return false;
        if(pinned!=null)
        {
            if(echoed!=null && MatchesHistory(await Source.DeletedAsync(pinned.Entry.Path,Ct),pinned.Domain,echoed)) return false;
            var key=(PathNames.Key(pinned.Entry.Path),pinned.Entry.Id);
            latest.TryAdd(key,(0,new(Side,ChangeKind.Changed,pinned.Entry.Path,null,pinned.Entry.Id,pinned.Entry,true,false)));
        }
        if(Retry!=null && latest.Count==0 && !await RecoverBinAsync()) return false;
        var settled=new HashSet<(string Path,string Id)>();
        foreach(var (key,candidate) in latest)
        {
            var last=candidate.Report;
            if(pathDeletes.GetValueOrDefault(key.Path)>candidate.Order || last.Kind==ChangeKind.Deleted || last.Observed is {} version &&
                PathNames.Same(last.Path,version.Path) && PathNames.IsSelfOrUnder(version.Path,Root) && await new HeldCandidate(this,version).SettleAsync()) settled.Add(key);
            else complete=false;
        }
        if(Retry!=null)
        {
            if(!complete) return false;
            if(Retry!=null && !await Store.CloseNoticeAsync(Pair.PairId,Retry,Ct)) return false;
        }
        List<Action>? releases=null;
        try
        {
            foreach(var (key,report) in acks)
            {
                if(!settled.Contains(key)) continue;
                var release=report.MarkHeld is {} mark ? mark() : report.ConsumeHeld;
                if(release!=null) (releases??=[]).Add(release);
            }
        }
        finally {if(releases!=null) foreach(var release in releases) release();}
        return complete;
    }

    // Keep the latest report per subject. False: the pinned version itself was reported removed.
    private bool Collect(IEnumerable<Change> inputs)
    {
        foreach(var c in inputs)
        {
            if(c.Side!=Side) continue;
            if(c.Path==null || !PathNames.IsSelfOrUnder(c.Path,Root))
            {
                if((c.OldPath!=null && PathNames.IsSelfOrUnder(c.OldPath,Root) || c.AffectedPath!=null && PathNames.IsSelfOrUnder(c.AffectedPath,Root)) && (subject==null || subject.Id==c.Id)) complete=false;
                continue;
            }
            var key=(PathNames.Key(c.Path),c.Id??c.Observed?.Id??string.Empty);
            if(pinned!=null && PathNames.Same(c.Path,pinned.Entry.Path) && c.Kind==ChangeKind.Deleted)
            {
                // A path-only echo has no identity. Leave a corroborated independent removal to ordinary settlement.
                if(c.Id==pinned.Entry.Id) return false;
                if(c.Id==null && distinct!=null) echoed=distinct;
                continue;
            }
            if(subject!=null && (!PathNames.Same(subject.Path,c.Path) || key.Item2!=subject.Id && !(Side==Side.Dropbox && c.Kind==ChangeKind.Deleted && key.Item2.Length==0 && latest.ContainsKey((key.Item1,subject.Id))))) continue;
            if(pinned!=null && !Borrow(c,key,pinned)) continue;
            latest[key]=(++order,c);
            if(Side==Side.Dropbox && key.Item2.Length==0 && c.Kind==ChangeKind.Deleted) pathDeletes[key.Item1]=order;
            if((c.MarkHeld!=null || c.ConsumeHeld!=null)) acks.Add((key,c));
        }
        return true;
    }

    // Borrow only the pinned observation. An unrelated queue entry keeps its own acknowledgement.
    private bool Borrow(Change c,(string Path,string Id) key,IdentityLocation pin)
    {
        distinct=c.Observed is {} observed && observed.Id==pin.Entry.Id && observed.Revision!=pin.Entry.Revision?observed:null;
        if(distinct!=null && c.Kind!=ChangeKind.Deleted && !latest.ContainsKey(key) && (c.MarkHeld!=null || c.ConsumeHeld!=null)) superseded.Add(c);
        if(c.Observed!=pin.Entry || c.Kind==ChangeKind.Deleted) return false;
        foreach(var ack in superseded) acks.Add((key,ack));
        superseded.Clear();
        return true;
    }

    // A reviewed bin subject with no queued report is recovered from its own recycled version.
    private async Task<bool> RecoverBinAsync()
    {
        if(subject==null || Side!=Side.Local || await Source.ReadByIdAsync(subject.Id,Ct) is not {InScope:false} bin || bin.Location.Entry.Id!=subject.Id) return false;
        var baseline=await Store.FindAsync(Pair.PairId,Side,PathNames.Key(subject.Path),Ct);
        var selected=await Store.ArmedAsync(Pair.PairId,Ct);
        if(!(baseline!=null && baseline.LocalId!=subject.Id) && !(selected is {} note && note.Victim.Saved==baseline && PathNames.Same(note.Victim.Proof.Expected.Path,subject.Path) && note.Victim.Proof.VolumeId==bin.Location.Domain)) return false;
        ActualVictim=bin.Location;var recovered=bin.Location.Entry with {Path=subject.Path};
        latest[(PathNames.Key(subject.Path),subject.Id)]=(++order,new(Side,ChangeKind.Changed,subject.Path,null,subject.Id,recovered,true,false));
        return true;
    }

    private static bool MatchesHistory(DeletionEvidence evidence,string? domain,Entry version)=>evidence is DeletionEvidence.DeletedFile h && PathNames.Same(h.Path,version.Path) && h.Domain==domain && h.RemoteId==version.Id && h.Revision==version.Revision && h.ContentHash==version.ContentHash;
}
