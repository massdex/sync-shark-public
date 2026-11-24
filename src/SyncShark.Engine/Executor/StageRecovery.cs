namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Recover the armed Local removal against its recorded identity and endpoints: finish, decide or retire its PC publication.</summary>
internal sealed class StageRecovery(Unfinished owner,PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct)
{
    /// <summary>Recover the armed removal if its endpoints overlap the footprint (null: any).</summary>
    internal async Task RunAsync(PrefixSet? footprint)
    {
        if(await store.ArmedAsync(pair.PairId,ct) is not {} note) return;
        var victim=note.Victim; var proof=victim.Proof;
        var saved=victim.Saved;
        var cloudPath=saved?.DropboxPath??await FolderDelete.MapAsync(pair,store,Side.Local,proof.Expected.Path,ct);
        if(footprint!=null && !footprint.Overlaps(PathNames.DropboxKey(cloudPath))) return;
        var location=await local.ReadByIdAsync(proof.Expected.Id,ct);
        if(location is not {InScope:false} actual || actual.Location.Domain!=proof.VolumeId || actual.Location.Entry.Id!=proof.Expected.Id)
        {
            if(location is not {InScope:true}) await Unfinished.Unchecked(store,pair,proof.Expected,ct);
            await EndNoteAsync(store,local,pair,victim,note.Stage,ct);return;
        }
        if(note.Stage==null && proof.Expected.IsFolder)
        {
            var pc=local.BoundaryAsync(ct);var db=dropbox.BoundaryAsync(ct);await Task.WhenAll(pc,db);
            if(owner.CoversThrough?.Invoke(((await pc).Position,(await db).Cursor))!=true) return;
        }
        await ResumeAsync(note,cloudPath);
    }

    // Compare the recorded endpoints with what both sides hold now.
    private async Task ResumeAsync((RemovalSelection Victim,StageProof? Stage) note,string cloudPath)
    {
        var victim=note.Victim; var proof=victim.Proof;
        var saved=victim.Saved;
        var current=await local.ReadOneAsync(proof.Expected.Path,ct);
        var other=await dropbox.ReadOneAsync(cloudPath,ct);
        var localKey=PathNames.Key(proof.Expected.Path);
        var currentBinding=await store.FindAsync(pair.PairId,Side.Local,saved?.LocalKey??localKey,ct);
        if(note.Stage==null) {await RecoverWithoutStageAsync(victim,cloudPath,current,other);return;}
        if(saved!=null)
        {
            var peer=await dropbox.ReadByIdAsync(saved.DropboxId,ct);
            if(peer is {} live && (!live.InScope || live.Location.Entry!=other)) return;
            // A stage at the arrival name is not proof that the original pair moved there.
            if(note.Stage!=null && localKey!=saved.LocalKey) return;
        }
        if(currentBinding!=saved)
        {
            if(note.Stage is {} committed && current!=null && currentBinding is {} linked && current.Id==committed.FileId && linked.LocalId==current.Id && linked.DropboxContentHash==committed.ContentHash && current.Size==committed.Size && await local.ContentHashAsync(current,ct)==committed.ContentHash)
            {
                if(other!=null && SyncedEntries.From(current,other)==(linked with {LocalPublished=null}))
                {await store.DisarmAsync(pair.PairId,victim,committed,ct);return;}
                saved=linked;
            }
            else return;
        }
        var stage=note.Stage!;
        // Another object holds the final name: create-only publication can never land.
        if(current!=null && (current.Id!=stage.FileId || current.Size!=stage.Size || await local.ContentHashAsync(current,ct)!=stage.ContentHash))
        {await EndNoteAsync(store,local,pair,victim,stage,ct);return;}
        await PublishStageAsync(note,stage,saved,cloudPath,current,other);
    }

    // No stage was written: recover a recycled folder's held reports or retire the dead file bindings, then disarm.
    private async Task RecoverWithoutStageAsync(RemovalSelection victim,string cloudPath,Entry? current,Entry? other)
    {
        var proof=victim.Proof; var saved=victim.Saved;
        var peer=saved==null?null:await dropbox.ReadByIdAsync(saved.DropboxId,ct);
        if(peer==null)
        {
            if(proof.Expected.IsFolder)
            {
                var range=new Scope(proof.Expected.Path,cloudPath); bool occupied=current!=null || other!=null;
                if(!occupied && !await HeldRecovery.RecoverHeldAsync(pair,range,Side.Local,local,dropbox,store,heldInput,populate,ct)) return;
                if(saved!=null) await compareOldBinding(saved,range,range,ct);
                if(occupied) await Unfinished.Unchecked(store,pair,proof.Expected,ct);
            }
            else if(saved!=null)
            {
                var dead=new List<SyncedEntry>();
                await foreach(var b in store.ByIdAsync(pair.PairId,Side.Local,saved.LocalId,ct)) if(b.DropboxId==saved.DropboxId && !b.IsFolder) dead.Add(b);
                foreach(var b in dead) await store.CommitAsync(pair.PairId,b,null,null,null,ct);
            }
        }
        await store.DisarmAsync(pair.PairId,victim,null,ct);
    }

    // Publish the stage under its final name and bind it, or settle the Dropbox side first.
    private async Task PublishStageAsync((RemovalSelection Victim,StageProof? Stage) note,StageProof stage,SyncedEntry? saved,string cloudPath,Entry? current,Entry? other)
    {
        var victim=note.Victim;
        try
        {
            if(other==null || other.IsFolder || other.Skip!=null || saved!=null && other.Id!=saved.DropboxId || other.Size!=stage.Size || other.ContentHash!=stage.ContentHash)
            {
                if(other?.Skip!=null) return;
                await using var obsolete=current==null?await local.ReopenStageAsync(victim,stage,ct):null;
                bool settled=other==null?await FinishFromHistoryAsync(note,stage,saved,cloudPath,current):await DecideSurvivorAsync(note,saved,cloudPath,current,other);
                if(!settled) return;
                await store.DisarmAsync(pair.PairId,victim,stage,ct);return;
            }
            if(current==null)
            {
                await using var reopened=await local.ReopenStageAsync(victim,stage,ct);
                var result=await local.PublishAsync(reopened,null,null,ct); current=result.Final;
                if(result.Outcome!=RemovalOutcome.Published || current?.Id!=stage.FileId || current.Size!=stage.Size || await local.ContentHashAsync(current,ct)!=stage.ContentHash) return;
            }
            await store.CommitAsync(pair.PairId,saved,SyncedEntries.From(current,other),null,null,ct);
            await store.DisarmAsync(pair.PairId,victim,stage,ct);
        }
        catch(ChangedUnderneathException) { /* An unverified stage stays armed. */ }
    }

    internal static async Task EndNoteAsync(IStore store,ISide local,PairConfig pair,RemovalSelection victim,StageProof? stage,CancellationToken ct)
    {
        IStaged? held=null;
        if(stage!=null) try {held=await local.ReopenStageAsync(victim,stage,ct);}
        catch(Exception e) when(e is ChangedUnderneathException or IOException) { /* Never discard an unverifiable stage. */ }
        await using(held) await store.DisarmAsync(pair.PairId,victim,stage,ct);
    }

    // No Dropbox copy. True: nothing is left to publish and the selection may be disarmed.
    private async Task<bool> FinishFromHistoryAsync((RemovalSelection Victim,StageProof? Stage) note,StageProof stage,SyncedEntry? saved,string cloudPath,Entry? current)
    {
        var victim=note.Victim;
        if(saved!=null && await dropbox.ReadByIdAsync(saved.DropboxId,ct) is not null) return false;
        // A published stage is not a user's Local edit. Never replay it as an upload.
        if(current!=null)
        {
            // Finish the interrupted publication from actual source history first. Its durable binding makes the later deletion restartable.
            if(await dropbox.DeletedAsync(cloudPath,ct) is not DeletionEvidence.DeletedFile history || history.Path!=cloudPath || history.Domain!=pair.Roots?.Dropbox.RootNamespaceId || history.ContentHash!=stage.ContentHash || saved!=null && history.RemoteId!=saved.DropboxId) return false;
            var historical=current with {Path=cloudPath,Id=history.RemoteId,ContentHash=history.ContentHash};
            var published=SyncedEntries.From(current,historical);
            var action=new Planner.SyncAction(ActionKind.DeleteLocal,new(PathNames.Key(current.Path),current,null,published,[],stage.ContentHash,RowContext.Neutral));
            await new ItemLocks().RunAsync(pair,action,[],async ()=>
            {
                if(await store.ArmedAsync(pair.PairId,ct)!=note || await store.FindAsync(pair.PairId,Side.Local,published.LocalKey,ct)!=saved || await local.ReadOneAsync(current.Path,ct)!=current || await local.ContentHashAsync(current,ct)!=stage.ContentHash || await dropbox.ReadOneAsync(cloudPath,ct)!=null || await dropbox.ReadByIdAsync(history.RemoteId,ct)!=null) return false;
                if(saved==null || published!=(saved with {LocalPublished=null})) await store.CommitAsync(pair.PairId,saved,published,null,null,ct);
                await store.DisarmAsync(pair.PairId,victim,stage,ct);
                return await ItemSync.ChangeAsync(pair,settings,events,paths,new(Side.Dropbox,ChangeKind.Deleted,cloudPath,null,history.RemoteId,null,false,false),local,dropbox,store,populate,compareOldBinding,ct,heldInput,coversThrough:owner.AcquiredThrough);
            },ct);
            return false;
        }
        if(saved!=null) await store.CommitAsync(pair.PairId,saved,null,null,null,ct);
        return true;
    }

    // The missing PC was recycled by this operation. Decide the surviving source as a create, not a user's deletion.
    private async Task<bool> DecideSurvivorAsync((RemovalSelection Victim,StageProof? Stage) note,SyncedEntry? saved,string cloudPath,Entry? current,Entry other)
    {
        var proof=note.Victim.Proof;
        if(current!=null || paths.Excludes(proof.Expected.Path,cloudPath,other.IsFolder)) return false;
        var row=new Row(PathNames.Key(proof.Expected.Path),null,other,null,[],null,RowContext.Neutral with {Destination=proof.Expected.Path});
        var action=Planner.Decide.For(row,pair.FirstRun,DateTimeOffset.UtcNow,false) with {Row=row with {Synced=saved}};
        if(action.Kind is not (ActionKind.Download or ActionKind.EnsureLocalFolder)) return false;
        if(!await new ItemLocks().RunAsync(pair,action,[],async ()=>
        {
            if(await local.ReadOneAsync(proof.Expected.Path,ct)!=null || await dropbox.ReadOneAsync(cloudPath,ct)!=other || await store.FindAsync(pair.PairId,Side.Local,row.Key,ct)!=saved || await store.ArmedAsync(pair.PairId,ct) is not {} active || active!=note) return false;
            return await new Effects().ApplyAsync(pair,settings,events,paths,action,local,dropbox,store,populate,compareOldBinding,ct,heldInput,owner.AcquiredThrough)==EffectOutcome.Applied;
        },ct)) return false;
        var pc=await local.ReadOneAsync(proof.Expected.Path,ct);var db=await dropbox.ReadOneAsync(cloudPath,ct);
        if(pc==null || db==null || pc.IsFolder!=db.IsFolder || !pc.IsFolder && await local.ContentHashAsync(pc,ct)!=db.ContentHash || await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(pc.Path),ct) is not {} final || SyncedEntries.From(pc,db)!=(final with {LocalPublished=null})) return false;
        return true;
    }
}
