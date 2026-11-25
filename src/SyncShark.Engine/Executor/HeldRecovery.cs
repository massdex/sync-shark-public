namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Held-input recovery: Retry of one reviewed PutBack subject, and recovery of the reports held across a removal.</summary>
public static class HeldRecovery
{
    /// <summary>Recover the positively observed bin occupant without ever granting the planned replacement.</summary>
    public static Task<bool> PutBackSubstituteAsync(PairConfig pair,Scope scope,IdentityLocation receipt,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct)
    {
        var version=receipt.Entry with {Path=scope.LocalPath};
        var observed=new Change(Side.Local,ChangeKind.Changed,scope.LocalPath,null,version.Id,version,true,false);
        return RecoverHeldAsync(pair,scope,Side.Local,local,dropbox,store,_=>[observed],populate,ct,actualVictim:receipt);
    }

    /// <summary>Retry only the exact held subject; an empty replay is not settlement.</summary>
    public static async Task<bool> RetryHeldAsync(PairConfig pair,Scope scope,Notice notice,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict=null)
    {
        if(notice.Kind!=NoticeKind.PutBack || notice.Subject.Incoming is not {} side) return false;
        var subject=notice.Subject.Items.FirstOrDefault(m=>m.Side==side && PathNames.Same(m.Path,notice.Path));
        if(subject==null || !PathNames.IsSelfOrUnder(subject.Path,side==Side.Local?scope.LocalPath:scope.DropboxPath)) return false;
        var endpoint=side==Side.Local?local:dropbox;
        var entry=notice.Subject.DropboxVersion?.Entry ?? (await endpoint.ReadByIdAsync(subject.Id,ct))?.Location.Entry;
        var mapped=await FolderDelete.MapAsync(pair,store,side,subject.Path,ct);
        var range=side==Side.Local?new Scope(subject.Path,mapped):new Scope(mapped,subject.Path);
        var saved=await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,subject.Path),ct);
        var observed=entry;
        if(entry==null) entry=heldInput(range).LastOrDefault(c=>c.Side==side && c.Id==subject.Id && PathNames.Same(c.Path,subject.Path) && c.Observed?.Id==subject.Id)?.Observed;
        var locks=new ItemLocks();
        if(entry?.IsFolder==true)
            return await locks.RunAsync([(Side.Local,ItemLocks.Physical(pair,Side.Local,range.LocalPath),true),(Side.Dropbox,ItemLocks.Physical(pair,Side.Dropbox,range.DropboxPath),true)],Recover,ct);
        var row=Row.ForOneSide(side,subject.Path,entry==null?null:entry with {Path=subject.Path},saved,[],null,RowContext.Neutral with {Destination=mapped});
        return await locks.RunAsync(pair,new(side==Side.Local?ActionKind.DeleteLocal:ActionKind.DeleteDropbox,row,Target:subject.Path),[],Recover,ct);
        async Task<bool> Recover()
        {
            if(await store.NoticeAsync(pair.PairId,notice.Id,ct) is not {} current || !current.Matches(notice) || await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,subject.Path),ct)!=saved || await FolderDelete.MapAsync(pair,store,side,subject.Path,ct)!=mapped) return false;
            if(notice.Subject.DropboxVersion==null && (await endpoint.ReadByIdAsync(subject.Id,ct))?.Location.Entry!=observed) return false;
            return await RecoverHeldAsync(pair,range,side,local,dropbox,store,heldInput,populate,ct,notice,resolveConflict:resolveConflict);
        }
    }

    /// <summary>Keep failed reports in the queue so Retry can reconstruct the latest required version.</summary>
    internal static Task<bool> RecoverHeldAsync(PairConfig pair,Scope scope,Side side,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct,Notice? retry=null,ActionKind origin=ActionKind.None,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict=null,IdentityLocation? actualVictim=null,bool selectedCloudWindow=false,DateTimeOffset? capturedAt=null,bool preRemoval=false)
        => new HeldRun(pair,scope,side,local,dropbox,store,heldInput,populate,ct,retry,origin,resolveConflict,actualVictim,selectedCloudWindow,capturedAt,preRemoval).RunAsync();
}
