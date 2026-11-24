using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Decide and apply one prepared row or rename group under its item locks. ItemSync owns change intake.</summary>
public static class RowExecution
{
    private static readonly ItemLocks Locks = new();

    /// <summary>Keep acquisition context and decide a rename group as a unit. Population admits only additions.</summary>
    public static async Task<EffectOutcome?> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Row? prepared, IReadOnlyList<RenameCandidate> bindingCandidates, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput=null, Action<string,bool>? failed=null, bool addOnly=false, Action<Notice>? failedNotice=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null)
    {
        try { return (await ExecuteWithOutcomeAsync(pair,settings,events,paths,prepared,bindingCandidates,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,addOnly,failedNotice,coversThrough)).Outcome; }
        catch(ItemFailedException error) when(error.Disposition!=FailureDisposition.TemporaryNoEffect)
        {
            var items=new List<NoticeItem>();
            if(prepared?.Local is {} pc) items.Add(new(Side.Local,pc.Path,pc.Id));
            if(prepared?.Dropbox is {} db) items.Add(new(Side.Dropbox,db.Path,db.Id));
            foreach(var c in bindingCandidates) items.Add(new(c.Side,c.Moved.Path,c.Moved.Id));
            var subject=IssueSubject.FromDistinct(items,null) with {FirstRunDeletion=prepared?.Context.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None};
            bool reproducible=prepared?.Synced is {IsFolder:false} && bindingCandidates.Count==0 && items.Count>0 && error.Disposition==FailureDisposition.Permanent && heldInput?.Invoke(new(Row.Paths(prepared).Local,Row.Paths(prepared).Dropbox)).Any(c=>c.ConsumeHeld!=null)!=true;
            await ItemSync.ReportFailureAsync(pair,store,events,error,subject,prepared?.Key??PathNames.Key(error.Path),reproducible,failed,ct,failedNotice);
            if(!reproducible) throw;
        }
        return null;
    }

    internal static async Task<bool> ExecuteAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Row? row,IReadOnlyList<RenameCandidate> candidates,bool dirty,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,bool addOnly,Action<Notice>? failedNotice=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,(Side Side,Entry? Occupant)? observedMissing=null)
        => (await ExecuteWithOutcomeAsync(pair,settings,events,paths,row,candidates,dirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,addOnly,failedNotice,coversThrough,observedMissing)).Settled;

    private static async Task<(bool Settled,EffectOutcome? Outcome)> ExecuteWithOutcomeAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Row? row,IReadOnlyList<RenameCandidate> candidates,bool dirty,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,bool addOnly,Action<Notice>? failedNotice,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough,(Side Side,Entry? Occupant)? observedMissing=null)
    {
        if (row is null && candidates.Count==0) throw new ArgumentException(nameof(row));
        ItemSync.RequireReadable(Side.Local,row?.Local); ItemSync.RequireReadable(Side.Dropbox,row?.Dropbox);
        var now=DateTimeOffset.UtcNow;
        var action=await DecideAsync(pair,settings,paths,row,candidates,dirty,now,local,dropbox,store,failed,ct);
        if(action.Kind==ActionKind.None) return (ItemSync.FinishDecision(action,failed),null);
        if(addOnly && !(action.Kind==ActionKind.Record || action.Kind is (ActionKind.Upload or ActionKind.EnsureDropboxFolder) && action.Row.Dropbox==null || action.Kind is (ActionKind.Download or ActionKind.EnsureLocalFolder) && action.Row.Local==null)) return (true,null);
        if(action.Kind==ActionKind.DeferFolder && action.Row.Synced is {} selectedFolder)
        {
            var removed=await RemoveBoundAsync(pair,settings,events,paths,selectedFolder,action.Row.Local==null?Side.Local:Side.Dropbox,local,dropbox,store,heldInput,populate,compareOldBinding,failed,ct,coversThrough);
            return (removed,null);
        }
        var endpoints=Row.Paths(action.Row);
        var (admitted,successor,missing)=await OccupantAsync(pair,action,now,dirty,observedMissing,local,dropbox,ct);
        action=admitted;
        var incumbent=await IncumbentAsync(pair,action,local,dropbox,store,ct);
        Task<(bool,EffectOutcome?)> Locked() => ApplyLockedAsync(pair,settings,events,paths,action,candidates,endpoints,successor,missing,incumbent,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,failedNotice,coversThrough,observedMissing);
        var applied=Moves.IsFileSplit(action) ? await Locks.RunAsync(await SplitKeysAsync(pair,action.Row,store,ct),Locked,ct) : await Locks.RunAsync(pair,action,candidates,Locked,ct,settings.CaseRenameSuffix,incumbent);
        if(applied.Item1 && candidates.Count!=0 && candidates[0] is {Moved.IsFolder:false} move)
        {
            var settled=await store.FindAsync(pair.PairId,move.Side,PathNames.KeyFor(move.Side,move.Moved.Path),ct);
            if(settled!=null && settled.LocalId==move.Synced.LocalId && settled.DropboxId==move.Synced.DropboxId && settled.PathOn(move.Side)==move.Moved.Path)
            {
                var final=await ItemSync.ObserveBindingAsync(paths,settled,local,dropbox,ct);
                if(final.Group.Count!=0 || final.Row.Local==null || final.Row.Dropbox==null) ItemSync.Retain(action.Row.Key,failed);
                if(!await ExecuteAsync(pair,settings,events,paths,final.Row,[],dirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,addOnly,failedNotice,coversThrough)) ItemSync.Retain(action.Row.Key,failed);
            }
        }
        return applied;
    }

    private static async ValueTask<SyncAction> DecideAsync(PairConfig pair,EngineSettings settings,PathScope paths,Row? row,IReadOnlyList<RenameCandidate> candidates,bool dirty,DateTimeOffset now,ISide local,ISide dropbox,IStore store,Action<string,bool>? failed,CancellationToken ct)
    {
        var priorBinding=row?.Synced;
        (row,bool mappedChild)=await MapChildAsync(pair,row,candidates,store,failed,ct);
        var action=candidates.Count==0 ? Decide.For(row!,pair.FirstRun,now,dirty) :
            await Effects.DecideMoveAsync(pair,settings,paths,candidates,local,dropbox,store,dirty,ct);
        row=action.Row;
        if(candidates.Count==0 && action.Kind==ActionKind.NeedLocalHash && row.Local is {} file)
        { row=row with {LocalHash=await local.ContentHashAsync(file,ct)}; action=Decide.For(row,pair.FirstRun,now,dirty); }

        bool classifyFolder=action.Kind==ActionKind.DeferFolder && row.Synced is null;
        if(classifyFolder) action=Classify(row,pair,now,dirty,failed);
        if(action.Kind==ActionKind.NeedDeletionEvidence)
        {
            row=action.Row;
            row=row with {Deletion=await dropbox.DeletedAsync(action.Target??Row.Paths(row).Dropbox,row.IsFolder(),ct),DeletionClock=dropbox.ClockOffset};
            action=classifyFolder?Classify(row,pair,now,dirty,failed):Decide.For(row,pair.FirstRun,now,dirty);
        }
        if(action.Kind==ActionKind.NeedLocalHash && action.Row.Local is {} requested && action.Row.LocalHash==null)
        {
            row=action.Row with {LocalHash=await local.ContentHashAsync(requested,ct)};
            action=classifyFolder?Classify(row,pair,now,dirty,failed):Decide.For(row,pair.FirstRun,now,dirty);
        }
        row=action.Row;
        if(mappedChild)
        {
            row=row with {Synced=priorBinding};
            action=action.IsInSync()?new(ActionKind.Record,row):action with {Row=row};
        }
        return action;
    }

    // Use final mapped names for the content decision, but commit against the actual old binding.
    private static async ValueTask<(Row? Row,bool Mapped)> MapChildAsync(PairConfig pair,Row? row,IReadOnlyList<RenameCandidate> candidates,IStore store,Action<string,bool>? failed,CancellationToken ct)
    {
        if(candidates.Count==0 && row?.Synced is {IsFolder:false} original && row.Local is {IsFolder:false} left && row.Dropbox is {IsFolder:false} right &&
            left.Id==original.LocalId && right.Id==original.DropboxId && left.Path!=original.LocalPath && right.Path!=original.DropboxPath)
        {
            if(await ItemSync.CounterpartAsync(pair,Side.Local,left.Path,store,ct)!=right.Path) ItemSync.Retain(row.Key,failed);
            return (row with {Synced=SyncedEntries.Relink(original,left,right)},true);
        }
        if(candidates.Count==0 && row?.Synced is {IsFolder:false} single && (row.Local==null)!=(row.Dropbox==null) && row.Context.Destination is {} far &&
            (row.Local??row.Dropbox) is {IsFolder:false} present && present.Id==single.IdOn(row.Local!=null?Side.Local:Side.Dropbox) &&
            present.Path!=single.PathOn(row.Local!=null?Side.Local:Side.Dropbox) && far!=single.PathOn(row.Local!=null?Side.Dropbox:Side.Local))
        {
            // A one-sided child at a confirmed hop: decide at both final names, commit against the old binding.
            var (pcPath,dbPath)=row.Local!=null?(present.Path,far):(far,present.Path);
            if(await ItemSync.CounterpartAsync(pair,Side.Local,pcPath,store,ct)!=dbPath) ItemSync.Retain(row.Key,failed);
            return (row with {Synced=single with {LocalPath=pcPath,LocalKey=PathNames.Key(pcPath),DropboxPath=dbPath,DropboxKey=PathNames.DropboxKey(dbPath)}},true);
        }
        return (row,false);
    }

    private static SyncAction Classify(Row r,PairConfig pair,DateTimeOffset now,bool dirty,Action<string,bool>? failed)
    {
        var mark=r.Context.ProtectedContents;
        if(mark is null && r.Context.UnreadableCause is null) ItemSync.Retain(r.Key,failed);
        return FolderDeletes.For(r,mark==true,r.Context.UnreadableCause,pair.FirstRun,now,dirty);
    }

    // Only a create into the absent bound leg can collide with an independent successor.
    private static async ValueTask<(SyncAction Action,Entry? Successor,Side? Missing)> OccupantAsync(PairConfig pair,SyncAction action,DateTimeOffset now,bool dirty,(Side Side,Entry? Occupant)? observedMissing,ISide local,ISide dropbox,CancellationToken ct)
    {
        var row=action.Row;
        Entry? successor=null;Side? missing=null;
        if(row.Synced is {} prior && action.Kind is (ActionKind.Download or ActionKind.Upload or ActionKind.ReplaceKind or ActionKind.EnsureLocalFolder or ActionKind.EnsureDropboxFolder) && row.EntryOn(action.WrittenSide())==null)
        {
            missing=action.WrittenSide();var leg=missing==Side.Local?local:dropbox;
            var occupant=observedMissing is {} observed && observed.Side==missing ? observed.Occupant : await leg.ReadOneAsync(action.Target ?? prior.PathOn(leg.Side),ct);
            if(occupant!=null)
            {
                if(occupant.Id==prior.IdOn(leg.Side)) throw new ChangedUnderneathException(occupant.Path);
                ItemSync.RequireReadable(leg.Side,occupant);successor=occupant;
                row=leg.Side==Side.Local?row with {Local=occupant}:row with {Dropbox=occupant};
                var occupied=row with {Synced=null,Context=row.Context with {FirstRun=false}};
                action=Decide.For(occupied,pair.FirstRun,now,dirty);
                if(action.Kind==ActionKind.NeedLocalHash)
                {
                    occupied=occupied with {LocalHash=await local.ContentHashAsync(occupied.Local!,ct)};
                    action=Decide.For(occupied,pair.FirstRun,now,dirty);
                }
                row=occupied with {Synced=prior};action=action with {Row=row};
            }
        }
        return (action,successor,missing);
    }

    private static async Task<SyncedEntry?> IncumbentAsync(PairConfig pair,SyncAction action,ISide local,ISide dropbox,IStore store,CancellationToken ct)
    {
        var row=action.Row;
        SyncedEntry? incumbent=null;
        if(action.Kind is (ActionKind.MoveLocal or ActionKind.MoveDropbox) && row.Synced is {} moving && action.Target is {} target)
        {
            var written=action.WrittenSide();var leg=written==Side.Local?local:dropbox;
            if(await leg.ReadOneAsync(target,ct) is {} occupant && occupant.Id!=moving.IdOn(written))
                incumbent=await store.FindAsync(pair.PairId,written,PathNames.KeyFor(written,target),ct);
            if(incumbent==moving) incumbent=null;
        }
        return incumbent;
    }

    private static async ValueTask<List<(Side,string,bool)>> SplitKeysAsync(PairConfig pair,Row row,IStore store,CancellationToken ct)
    {
        var keys=new List<(Side,string,bool)>();
        foreach(var side in new[]{Side.Local,Side.Dropbox})
        {
            void Add(Side leg,string path) => keys.Add((leg,ItemLocks.Physical(pair,leg,path),false));
            Add(side,row.Synced!.PathOn(side));
            if(row.EntryOn(side) is not {} source) continue;
            Add(side,source.Path);
            Add(PathNames.Other(side),await ItemSync.CounterpartAsync(pair,side,source.Path,store,ct));
        }
        return keys;
    }

    private static async Task<(bool,EffectOutcome?)> ApplyLockedAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,SyncAction action,IReadOnlyList<RenameCandidate> candidates,(string Local,string Dropbox) endpoints,Entry? successor,Side? missing,SyncedEntry? incumbent,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,Action<Notice>? failedNotice,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough,(Side Side,Entry? Occupant)? observedMissing)
    {
        var row=action.Row;
        var pcCheck=local.ReadOneAsync(endpoints.Local,ct); var dbCheck=dropbox.ReadOneAsync(endpoints.Dropbox,ct);
        await Task.WhenAll(pcCheck,dbCheck);
        ItemSync.RequireReadable(Side.Local,await pcCheck); ItemSync.RequireReadable(Side.Dropbox,await dbCheck);
        if(row.Synced!=null && row.Local!=null && await pcCheck==null) ItemSync.Retain(row.Key,failed);
        foreach(var side in new[]{Side.Local,Side.Dropbox})
        {
            var seen=side==Side.Local?await pcCheck:await dbCheck;var expected=row.EntryOn(side);
            if(row.Synced is {} owned && (expected==null || successor!=null && missing==side))
            {
                var leg=side==Side.Local?local:dropbox;
                if(await leg.ReadByIdAsync(owned.IdOn(side),ct) is {InScope:true} live && !paths.Excludes(side==Side.Local?live.Location.Entry.Path:null,side==Side.Dropbox?live.Location.Entry.Path:null,live.Location.Entry.IsFolder)) throw new ChangedUnderneathException(owned.PathOn(side));
                // An unrelated occupant is not the missing bound leg. A state-only or peer-only operation never writes it.
                if(expected==null && seen!=null && (observedMissing is not {} observation || observation.Side!=side || observation.Occupant==seen) && seen.Id!=owned.IdOn(side) && (action.Kind==ActionKind.ForgetOnly || action.Kind is (ActionKind.DeleteLocal or ActionKind.DeleteDropbox) && action.WrittenSide()!=side)) continue;
            }
            if(seen!=expected) throw new ChangedUnderneathException(Row.Path(row));
        }
        if(row.Synced is {} linked && action.Kind is (ActionKind.DeleteLocal or ActionKind.DeleteDropbox) && row.EntryOn(action.WrittenSide())?.Id!=linked.IdOn(action.WrittenSide())) throw new ChangedUnderneathException(Row.Path(row));
        var currentBinding=await store.FindAsync(pair.PairId,Side.Local,row.Synced?.LocalKey??PathNames.Key(endpoints.Local),ct);
        if(currentBinding!=row.Synced) throw new ChangedUnderneathException(Row.Path(row));
        if(action.Kind is ActionKind.NeedLocalHash or ActionKind.NeedDeletionEvidence) throw new InventoryIncompleteException(action.Kind.ToString(),null);
        if(successor is {IsFolder:true} && action.Kind==ActionKind.Record && row.Local is {IsFolder:true} leftFolder && row.Dropbox is {IsFolder:true} rightFolder)
        {
            var scope=new Scope(leftFolder.Path,rightFolder.Path);
            await compareOldBinding(row.Synced!,scope,null,ct);
        }
        // Effects owns physical-leg logging and passes the original hop callback between legs.
        var outcome=await new Effects().ApplyAsync(pair,settings,events,paths,action,local,dropbox,store,populate,compareOldBinding,ct,heldInput,coversThrough,failedNotice,grantedIncumbent:incumbent);
        if(outcome==EffectOutcome.Pending) ItemSync.Retain(row.Key,failed);
        // Vacating another binding only clears the destination; the arriving move still owns its input.
        if(outcome==EffectOutcome.Applied && action.Kind==ActionKind.ForgetOnly && candidates.Count!=0 && row.Synced!=candidates[0].Synced) ItemSync.Retain(row.Key,failed);
        if(outcome==EffectOutcome.Refused) return (false,outcome); // The notice is the decision, not a race.
        return (true,outcome);
    }

    // Ancestors are selected before acquisition. The selected-entry overload cannot widen after the grant.
    internal static async Task<bool> RemoveBoundAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,SyncedEntry folder,Side deleted,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>>? heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,Action<string,bool>? failed,CancellationToken ct,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null)
    {
        var source=deleted==Side.Local?local:dropbox; var target=deleted==Side.Local?dropbox:local;
        var highest=await FolderDelete.HighestMissingAsync(pair,paths,folder,source,store,ct);
        if(highest is null) { ItemSync.Retain(folder.KeyOn(deleted),failed); return false; }
        folder=highest;
        var row=new Row(folder.KeyOn(deleted),null,null,folder,[],null,RowContext.Neutral);
        return await Locks.RunAsync(pair,new(ActionKind.DeferFolder,row),[],async ()=>
        {
            if(heldInput==null || paths.ExcludesBinding(folder) || await store.FindAsync(pair.PairId,deleted,folder.KeyOn(deleted),ct)!=folder || await source.ReadByIdAsync(folder.IdOn(deleted),ct) is {InScope:true}) ItemSync.Retain(row.Key,failed);
            var found=await target.ReadByIdAsync(folder.IdOn(target.Side),ct);
            // Both bound identities are gone (R57, R95 B1): the selected removal already settled; retire the binding as ForgetOnly does.
            if(found is not {InScope:true} at) { await store.CommitAsync(pair.PairId,folder,null,null,null,ct); return true; }
            if(!at.Location.Entry.IsFolder || at.Location.Entry.Id!=folder.IdOn(target.Side) || !PathNames.Same(at.Location.Entry.Path,folder.PathOn(target.Side))) { ItemSync.Retain(row.Key,failed); return false; }
            var selected=at.Location.Entry; ItemSync.RequireReadable(target.Side,selected);
            await ItemSync.RequireRemovalAsync(new FolderDelete().ApplyAsync(pair,settings,events,paths,selected,folder,target.Side,SyncAction.DeleteOn(target.Side),local,dropbox,store,heldInput!,populate,compareOldBinding,ct,resolveConflict:(where,side,version,token)=>new Effects().RecoverConflictAsync(pair,settings,events,paths,where,side,version,local,dropbox,store,populate,compareOldBinding,token),coversThrough:coversThrough),()=>failed?.Invoke(row.Key,false));
            return true;
        },ct);
    }
}
