namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Settle one subject's latest held version: restore it, hand a competing name to the conflict owner, or keep it held.</summary>
internal sealed class HeldCandidate(HeldRun run,Entry version)
{
    private SyncedEntry? saved;
    private bool foreign,substitute;
    private Notice? owner;
    private IdentityLocation? exact;
    private List<NoticeItem> items=null!;
    private Scope endpoints=null!;
    private (IdentityLocation Location,bool InScope)? located;
    private IdentityLocation? location;
    private (RemovalSelection Victim,StageProof? Stage)? armed;
    private bool sameSelection,sameId,freeDropbox;
    private Recovery.File? provenFile;
    private Stream? verifiedBytes;

    /// <summary>True when the subject settled; false keeps it held for Retry.</summary>
    internal async Task<bool> SettleAsync()
    {
        // Preparation stays before the try, so its failures stay unclassified, and runs in this frame: no separate preparation Task.
        saved=await run.Store.FindAsync(run.Pair.PairId,run.Side,PathNames.KeyFor(run.Side,version.Path),run.Ct);
        foreign=saved!=null && saved.IdOn(run.Side)!=version.Id;
        substitute=run.ActualVictim?.Entry.Id==version.Id;
        // A loop, not Any(lambda): a lambda over version would add a closure object per subject.
        if(!substitute && foreign && run.Retry!=null)
            foreach(var i in run.Retry.Subject.Items) if(i.Side==run.Side && i.Id==version.Id && PathNames.Same(i.Path,version.Path)) {substitute=true;break;}
        owner=run.Retry;
        if(owner==null) await foreach(var open in run.Store.NoticesAtAsync(run.Pair.PairId,PathNames.Key(version.Path),run.Ct))
            if(open.Kind==NoticeKind.PutBack && open.Subject.Incoming==run.Side && open.Subject.Items.FirstOrDefault() is {} original && original.Id==version.Id && PathNames.Same(original.Path,version.Path)) owner=open;
        exact=owner?.Subject.DropboxVersion;
        if(exact!=null && (exact.Entry!=version || exact.Domain!=run.Pair.Roots?.Dropbox.RootNamespaceId)) return false;
        items=owner?.Subject.Items.ToList()??[new(run.Side,version.Path,version.Id)];
        var tail=version.Path[run.Root.Length..]; endpoints=new Scope(saved?.LocalPath??PathNames.LocalSpelling(run.Scope.LocalPath+tail),saved?.DropboxPath??run.Scope.DropboxPath+tail);
        try {return await AttemptAsync();}
        catch(Exception ex) when(ClassifiedFailure(ex))
        {
            if(owner==null || await run.Store.NoticeAsync(run.Pair.PairId,owner.Id,run.Ct) is {} activeFailure && activeFailure.Matches(owner)) await Notice(NoticeKind.PutBack,ex is ItemFailedException failure?failure.Cause:ex is PathOccupiedException?TextKey.ClashNameTaken:TextKey.FailChanged);
            return false;
        }
    }

    private async Task<bool> AttemptAsync()
    {
        if(!run.PreRemoval && run.Side==Side.Dropbox && !version.IsFolder && version.Skip==null && !string.IsNullOrEmpty(version.Revision) && ContentHash.IsValid(version.ContentHash) && run.Pair.Roots?.Dropbox.RootNamespaceId is {Length:>0} nsId)
            exact??=new(nsId,PathNames.RootedDropboxPath(run.Pair.DropboxRoot,version.Path),version);
        if(exact!=null && owner?.Subject.DropboxVersion!=exact) {if(run.SelectedCloudWindow) return false; await Checkpoint();}
        if(run.Side==Side.Dropbox && (!foreign || substitute) && (run.Origin==ActionKind.ReplaceKind && !run.SelectedCloudWindow || await Competing())) return await Resolve();
        await LocateAsync();
        if(foreign && !substitute) return false;
        await ProveBinAsync();
        // Copy recovery needs the stream; whole-item restore needs only the version before it opens a DELETE handle.
        await using var verified=substitute?null:provenFile?.Bytes;
        verifiedBytes=verified;
        if(provenFile!=null)
        {
            await using var proofOnly=substitute?provenFile.Bytes:null;
            var candidateVersion=location!.Entry with {Path=version.Path};
            if(!ContentHash.IsValid(provenFile.Version.ContentHash) || (provenFile.Version with {ContentHash=candidateVersion.ContentHash})!=candidateVersion || ContentHash.IsValid(candidateVersion.ContentHash) && provenFile.Version.ContentHash!=candidateVersion.ContentHash) throw new ChangedUnderneathException(version.Path);
            version=provenFile.Version;
        }
        if(location==null && run.Side==Side.Dropbox && !version.IsFolder && !string.IsNullOrEmpty(version.Revision) && run.Pair.Roots?.Dropbox.RootNamespaceId is {Length:>0} ns) location=new(ns,PathNames.RootedDropboxPath(run.Pair.DropboxRoot,version.Path),version);
        if(substitute) return await SubstituteAsync();
        if(run.Side==Side.Local && (run.Origin==ActionKind.ReplaceKind && !run.SelectedCloudWindow || await Competing())) return await Resolve();
        if((run.SelectedCloudWindow || exact!=null) && (version.IsFolder || version.Skip!=null || await run.Source.ReadByIdAsync(version.Id,run.Ct) is not null)) throw new ChangedUnderneathException(version.Path);
        return version.IsFolder?await RestoreFolderAsync():await RestoreFileAsync();
    }

    // Where the version is, and whether the armed selection names this subject.
    private async Task LocateAsync()
    {
        located=run.ActualVictim==null && exact==null?await run.Source.ReadByIdAsync(version.Id,run.Ct):null;
        if(run.ActualVictim==null && located==null && run.Side==Side.Local && items.All(i=>i.Side!=Side.Local || i.Id==version.Id)) throw new ChangedUnderneathException(version.Path);
        location=exact??run.ActualVictim??located?.Location;
        armed=run.Side==Side.Local && (substitute || owner!=null)?await run.Store.ArmedAsync(run.Pair.PairId,run.Ct):null;
        sameSelection=armed is {} n && n.Victim.Saved==saved && n.Victim.Proof.VolumeId==location?.Domain && PathNames.Same(n.Victim.Proof.Expected.Path,version.Path);
        sameId=sameSelection && armed!.Value.Victim.Proof.Expected.Id==version.Id;
        if(owner!=null && sameSelection && !sameId && (run.ActualVictim!=null || located is {InScope:false})) substitute=true;
    }

    // A recycled file with the subject's identity proves its bytes before anything is restored.
    private async Task ProveBinAsync()
    {
        if(run.Side==Side.Local && located is {InScope:false} bin && bin.Location.Entry.Id==version.Id && !bin.Location.Entry.IsFolder)
        {
            var candidateVersion=bin.Location.Entry with {Path=version.Path};
            provenFile=await run.Source.RecoverAsync(bin.Location,candidateVersion,run.Ct) as Recovery.File;
            if(provenFile==null) throw new ChangedUnderneathException(version.Path);
        }
        if(sameId && owner!=null && provenFile!=null && ContentHash.IsValid(provenFile.Version.ContentHash) && !string.Equals(armed!.Value.Victim.Proof.ContentHash,provenFile.Version.ContentHash,StringComparison.OrdinalIgnoreCase)) substitute=true;
    }

    private bool Keep(Side leg,string path)=>!run.Pair.Exclusions.Any(ex=>!ex.Keep && ex.Side==leg && PathNames.IsSelfOrUnder(path,ex.Path));

    // A substitute is restored whole under its original name, never as the planned replacement.
    private async Task<bool> SubstituteAsync()
    {
        if(run.Side!=Side.Local || location==null || !Keep(Side.Local,endpoints.LocalPath)) throw new ChangedUnderneathException(version.Path);
        if(await run.Local.ReadOneAsync(endpoints.LocalPath,run.Ct)!=null) throw new PathOccupiedException(endpoints.LocalPath);
        if(!version.IsFolder && !ContentHash.IsValid(version.ContentHash))
        {
            if(await run.Local.RecoverAsync(location,version,run.Ct) is not Recovery.File known) throw new ChangedUnderneathException(version.Path);
            await using(known.Bytes){if(!ContentHash.IsValid(known.Version.ContentHash) || (known.Version with {ContentHash=version.ContentHash})!=version) throw new ChangedUnderneathException(version.Path);version=known.Version;}
        }
        if(sameId && (version.IsFolder || !ContentHash.IsValid(version.ContentHash) || string.Equals(armed!.Value.Victim.Proof.ContentHash,version.ContentHash,StringComparison.OrdinalIgnoreCase))) throw new ChangedUnderneathException(version.Path);
        // The exact PutBack subject survives the native rename until its matching note is discharged.
        if(sameSelection && owner==null) await Checkpoint();
        var restored=await run.Local.RestoreRecycledAsync(location,version,endpoints.LocalPath,run.Ct);
        if(restored is not {Outcome:RemovalOutcome.Published,Final:{} final} || final.Id!=version.Id || final.IsFolder!=version.IsFolder || !PathNames.Same(final.Path,endpoints.LocalPath) ||
            !final.IsFolder && (final.Size!=version.Size || !ContentHash.IsValid(final.ContentHash) || ContentHash.IsValid(version.ContentHash) && !string.Equals(final.ContentHash,version.ContentHash,StringComparison.OrdinalIgnoreCase))) throw new ChangedUnderneathException(version.Path);
        if(armed is {Stage:not null}) return false;
        if(sameSelection) await run.Store.DisarmAsync(run.Pair.PairId,armed!.Value.Victim,null,run.Ct);
        return await ReleaseOwner();
    }

    private async Task<bool> Resolve()
    {
        if(run.ResolveConflict is not {} resolve || await resolve(endpoints,run.Side,version,run.Ct)!=EffectOutcome.Applied) throw new ItemFailedException(version.Path,TextKey.RetryRecoveryRequired,null);
        return await ReleaseOwner();
    }

    // A retried subject whose names hold different content goes to the conflict owner.
    private async Task<bool> Competing()
    {
        if(run.Retry==null || run.ResolveConflict==null && exact==null) return false;
        var db=await run.Dropbox.ReadOneAsync(endpoints.DropboxPath,run.Ct);
        freeDropbox=exact!=null && db==null && Keep(Side.Dropbox,endpoints.DropboxPath);
        if(freeDropbox) return false; // The old-ID fence and create-only Restore govern this leg.
        var pc=await run.Local.ReadOneAsync(endpoints.LocalPath,run.Ct);
        return pc!=null && (pc.IsFolder!=version.IsFolder || !pc.IsFolder && await run.Local.ContentHashAsync(pc,run.Ct)!=version.ContentHash) || db!=null && (db.IsFolder!=version.IsFolder || !db.IsFolder && db.ContentHash!=version.ContentHash);
    }

    private async Task<bool> RestoreFolderAsync()
    {
        if(location==null || await run.Source.RecoverAsync(location,version,run.Ct) is not Recovery.Folder folder || folder.Root.Id!=version.Id) throw new ChangedUnderneathException(version.Path);
        var keepPc=Keep(Side.Local,endpoints.LocalPath);var keepDb=Keep(Side.Dropbox,endpoints.DropboxPath);
        if(!(run.Side==Side.Local?keepDb:keepPc)) throw new ItemFailedException(version.Path,TextKey.RetryRecoveryRequired,null);
        await run.Populate(endpoints,run.Side,folder,run.Ct);
        return true;
    }

    // Both legs get the exact version; an occupied leg goes to the conflict owner.
    private async Task<bool> RestoreFileAsync()
    {
        Entry? a=null,b=null;Exception? localFailure=null;
        try {if(Keep(Side.Local,endpoints.LocalPath)) a=await Restore(run.Local,endpoints.LocalPath,null);}
        catch(Exception ex) when(Keep(Side.Dropbox,endpoints.DropboxPath) && (ex is PathOccupiedException || freeDropbox && ClassifiedFailure(ex))) {localFailure=ex;}
        if(localFailure!=null && freeDropbox && (owner==null || await run.Store.NoticeAsync(run.Pair.PairId,owner.Id,run.Ct) is not {} current || !current.Matches(owner))) return false;
        // An eligible empty Dropbox name gets the pinned revision, never a stream from mutable Local.
        try {if(Keep(Side.Dropbox,endpoints.DropboxPath)) b=await Restore(run.Dropbox,endpoints.DropboxPath,a);}
        catch(PathOccupiedException) when(run.ResolveConflict!=null && (a!=null || localFailure is PathOccupiedException)) { }
        if(localFailure!=null && localFailure is not PathOccupiedException) throw localFailure;
        if(localFailure!=null || b==null && Keep(Side.Dropbox,endpoints.DropboxPath)) return await Resolve();
        if(a==null && b==null) return false;
        if(a!=null && b!=null) await run.Store.CommitAsync(run.Pair.PairId,saved,SyncedEntries.From(a,b),null,null,run.Ct);

        await Notice(NoticeKind.Info,TextKey.InfoEditBeatsDelete);
        return run.Retry!=null || await ReleaseOwner();
    }

    private async Task<Entry> Restore(ISide leg,string path,Entry? verifiedLocal)
    {
        var present=await leg.ReadOneAsync(path,run.Ct);
        if(present!=null)
        {
            bool known=items.Any(m=>m.Side==leg.Side && m.Id==present.Id && PathNames.Same(m.Path,path)) || saved?.IdOn(leg.Side)==present.Id;
            // A lost remote create reply cannot prove our authorship. It can prove ordinary equality against the exact checkpointed local file.
            bool equalCandidate=leg.Side==Side.Dropbox && verifiedLocal!=null && owner!=null && items.Any(i=>i.Side==Side.Local && i.Id==verifiedLocal.Id && PathNames.Same(i.Path,verifiedLocal.Path)) && await run.Local.ContentHashAsync(verifiedLocal,run.Ct)==version.ContentHash;
            if(present.IsFolder || !(known || equalCandidate) || present.Size!=version.Size || !ContentHash.IsValid(version.ContentHash)) throw new PathOccupiedException(path);
            if(!StringComparer.OrdinalIgnoreCase.Equals(await leg.ContentHashAsync(present,run.Ct),version.ContentHash)) throw new PathOccupiedException(path);
            return present;
        }
        await using var bytes=verifiedLocal!=null?await run.Local.OpenReadAsync(verifiedLocal,run.Ct):verifiedBytes??(location!=null && await run.Source.RecoverAsync(location,version,run.Ct) is Recovery.File file && file.Version==version?file.Bytes:throw new ChangedUnderneathException(path));
        foreach(var parent in PathNames.Ancestors(path).Reverse().Where(p=>p.Length>0)) if((await leg.EnsureFolderAsync(parent,run.Ct)).Outcome!=RemovalOutcome.Published) throw new ChangedUnderneathException(parent);
        await using var stage=await leg.StageAsync(path,bytes,version,null,run.Ct);
        if(stage.Proof is {} proof && leg.Side==Side.Local)
        {
            if(exact!=null && (proof.Size!=version.Size || !StringComparer.OrdinalIgnoreCase.Equals(proof.ContentHash,version.ContentHash))) throw new ChangedUnderneathException(path);
            // Persist the exact staged file identity before create-only publication: restart cannot infer ownership from equal bytes.
            items.Add(new(leg.Side,path,proof.FileId));
            await Checkpoint();
        }
        var published=await leg.PublishAsync(stage,null,null,run.Ct);
        if(published is {Outcome:RemovalOutcome.Published,Final:{} confirmed} && !items.Any(i=>i.Side==leg.Side && i.Id==confirmed.Id && PathNames.Same(i.Path,path)))
        {
            items.Add(new(leg.Side,path,confirmed.Id)); await Checkpoint();
        }
        if(published.Outcome!=RemovalOutcome.Published || published.Final is not {} entry || entry.Size!=version.Size || !ContentHash.IsValid(version.ContentHash)) throw new ChangedUnderneathException(path);
        return StringComparer.OrdinalIgnoreCase.Equals(await leg.ContentHashAsync(entry,run.Ct),version.ContentHash)?entry:throw new ChangedUnderneathException(path);
    }

    private async Task<bool> ReleaseOwner()
    {
        if(owner==null) return true;
        if(!await run.Store.CloseNoticeAsync(run.Pair.PairId,owner,run.Ct)) return false;
        if(owner==run.Retry) run.Retry=null;
        return true;
    }

    private async Task Checkpoint()
    {
        if(owner!=null && (await run.Store.NoticeAsync(run.Pair.PairId,owner.Id,run.Ct) is not {} active || !active.Matches(owner))) throw new ChangedUnderneathException(version.Path);
        await Notice(NoticeKind.PutBack,owner?.Cause??TextKey.RetryRecoveryRequired);
        await foreach(var n in run.Store.NoticesAtAsync(run.Pair.PairId,PathNames.Key(version.Path),run.Ct))
            if(n.Kind==NoticeKind.PutBack && n.Subject.Incoming==run.Side && n.Subject.DropboxVersion==exact && n.Subject.HasSameItems(IssueSubject.FromDistinct(items,run.Side))) {owner=n;if(run.Retry!=null) run.Retry=n;return;}
        throw new ChangedUnderneathException(version.Path);
    }

    private Task Notice(NoticeKind kind,TextKey cause)=>run.Store.SaveNoticeAsync(run.Pair.PairId,new(0,kind,cause,version.Path,IssueSubject.FromDistinct(items,run.Side) with {DropboxVersion=kind==NoticeKind.PutBack?exact:null},owner?.At??run.IdentifiedAt,false),run.Ct);

    private static bool ClassifiedFailure(Exception ex)=>ex is ItemFailedException or ChangedUnderneathException or PathOccupiedException or IOException;
}
