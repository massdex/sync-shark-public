using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Decide and apply one prepared row or rename group under its item locks. ItemSync owns change intake.</summary>
public static class RowExecution
{
    private static readonly ItemLocks Locks = new();

    /// <summary>Keep acquisition context and decide a rename group as a unit. Population admits only additions.</summary>
    public static async Task<EffectOutcome?> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Row? prepared, IReadOnlyList<RenameCandidate> bindingCandidates, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput=null, Action<string,bool>? failed=null, bool addOnly=false, Action<Notice>? failedNotice=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,Action<Notice>? captureFailure=null)
    {
        try { return (await ExecuteWithOutcomeAsync(pair,settings,events,paths,prepared,bindingCandidates,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,addOnly,failedNotice,coversThrough,null,captureFailure)).Outcome; }
        catch(ItemFailedException error) when(error.Disposition!=FailureDisposition.TemporaryNoEffect)
        {
            var subject=Subject(prepared,bindingCandidates);
            bool reproducible=prepared?.Synced is {IsFolder:false} && bindingCandidates.Count==0 && subject.Items.Count>0 && error.Disposition==FailureDisposition.Permanent && heldInput?.Invoke(new(Row.Paths(prepared).Local,Row.Paths(prepared).Dropbox)).Any(c=>c.ConsumeHeld!=null)!=true;
            await ItemSync.ReportFailureAsync(pair,store,events,error,subject,prepared?.Key??PathNames.Key(error.Path),reproducible,failed,ct,failedNotice);
            if(!reproducible) throw;
        }
        return null;
    }

    /// <summary>The one item subject a row or move group names, with its first-run provenance.</summary>
    public static IssueSubject Subject(Row? prepared,IReadOnlyList<RenameCandidate> candidates)
    {
        var items=new List<NoticeItem>();
        if(prepared?.Local is {} pc) items.Add(new(Side.Local,pc.Path,pc.Id));
        if(prepared?.Dropbox is {} db) items.Add(new(Side.Dropbox,db.Path,db.Id));
        foreach(var c in candidates) items.Add(new(c.Side,c.Moved.Path,c.Moved.Id));
        return IssueSubject.FromDistinct(items,null) with {FirstRunDeletion=prepared?.Context.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None};
    }

    internal static async Task<bool> ExecuteAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Row? row,IReadOnlyList<RenameCandidate> candidates,bool dirty,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,bool addOnly,Action<Notice>? failedNotice=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,(Side Side,Entry? Occupant)? observedMissing=null,Action<Notice>? captureFailure=null)
        => (await ExecuteWithOutcomeAsync(pair,settings,events,paths,row,candidates,dirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,addOnly,failedNotice,coversThrough,observedMissing,captureFailure)).Settled;

    public static async Task<(bool Settled,EffectOutcome? Outcome,SyncAction Action)> ExecuteWithOutcomeAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Row? row,IReadOnlyList<RenameCandidate> candidates,bool dirty,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,bool addOnly,Action<Notice>? failedNotice=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,(Side Side,Entry? Occupant)? observedMissing=null,Action<Notice>? captureFailure=null)
    {
        if (row is null && candidates.Count==0) throw new ArgumentException(nameof(row));
        ItemFailedException.RequireReadable(Side.Local,row?.Local);
        ItemFailedException.RequireReadable(Side.Dropbox,row?.Dropbox);
        var now=DateTimeOffset.UtcNow;
        var hop=MappedChild(row,candidates);
        var priorBinding=row?.Synced;
        if(hop is {} mapped)
        {
            if(await ItemSync.CounterpartAsync(pair,Side.Local,mapped.Local,store,ct)!=mapped.Dropbox) ItemSync.Retain(row!.Key,failed);
            row=Mapped(row!,mapped);
        }
        SyncAction action;
        SyncedEntry? keepFollow=null;
        if(candidates.Count==0) action=Decide.For(row!,pair.FirstRun,now,dirty);
        else if(!addOnly && candidates.All(c=>paths.KeepCovers(c.Side,c.Synced.KeyOn(c.Side))))
        {
            var original=candidates[0].Synced;
            var footprint=candidates.Select(c=>(c.Side,c.Moved.Path)).Append((Side.Local,original.LocalPath)).Append((Side.Dropbox,original.DropboxPath))
                .Select(p=>(p.Item1,ItemLocks.Physical(pair,p.Item1,p.Item2),original.IsFolder)).ToArray();
            action=await Locks.RunAsync(footprint,async ()=>
            {
                var (plan,followed)=await MoveDecision.DecideWithFollowAsync(pair,settings,paths,candidates,local,dropbox,store,dirty,ct,null);
                if(followed!=null)
                {
                    var departure=candidates.FirstOrDefault(c=>!paths.KeepCovers(c.Side,followed.KeyOn(c.Side)));
                    await store.FollowKeepAsync(pair.PairId,original,followed,departure?.Side,ct);
                    keepFollow=followed;
                }
                return plan;
            },ct);
        }
        else action=await MoveDecision.DecideMoveAsync(pair,settings,paths,candidates,local,dropbox,store,dirty,ct);
        var decided=action.Row;
        if(candidates.Count==0 && action.Kind==ActionKind.NeedLocalHash && decided.Local is {} file)
        {
            decided=decided with {LocalHash=await local.ContentHashAsync(file,ct)};
            action=Decide.For(decided,pair.FirstRun,now,dirty);
        }
        bool classifyFolder=action.Kind==ActionKind.DeferFolder && decided.Synced is null;
        if(classifyFolder) action=Classify(decided,pair,now,dirty,failed);
        if(action.Kind==ActionKind.NeedDeletionEvidence)
        {
            action=Redecide(await WithDeletionAsync(action.Row,action.Target??Row.Paths(action.Row).Dropbox,action.Row.IsFolder(),pair.PairId,dropbox,store,ct),classifyFolder,pair,now,dirty,failed);
        }
        if(action.Kind==ActionKind.NeedLocalHash && action.Row.Local is {} requested && action.Row.LocalHash==null)
        {
            var hash=await local.ContentHashAsync(requested,ct);
            action=Redecide(action.Row with {LocalHash=hash},classifyFolder,pair,now,dirty,failed);
        }
        if(hop!=null) action=Rebound(action,priorBinding);
        if(action.Kind==ActionKind.None) return (keepFollow!=null && !addOnly || ItemSync.FinishDecision(action,failed),null,action);
        if(addOnly && !(action.Kind==ActionKind.Record || action.Kind is (ActionKind.Upload or ActionKind.EnsureDropboxFolder) && action.Row.Dropbox==null || action.Kind is (ActionKind.Download or ActionKind.EnsureLocalFolder) && action.Row.Local==null)) return (true,null,action);
        if(action.Kind==ActionKind.DeferFolder && action.Row.Synced is {} selectedFolder)
            return (await RemoveBoundAsync(pair,settings,events,paths,selectedFolder,action.Row.Local==null?Side.Local:Side.Dropbox,local,dropbox,store,heldInput,populate,compareOldBinding,failed,ct,coversThrough,captureFailure),null,action);
        var endpoints=Row.Paths(action.Row);
        Entry? successor=null;
        Side? missing=null;
        if(action.Row.Synced is {} prior && CreatesBoundLeg(action))
        {
            missing=action.WrittenSide();
            var leg=missing==Side.Local?local:dropbox;
            var occupant=observedMissing is {} seen && seen.Side==missing ? seen.Occupant : await leg.ReadOneAsync(action.Target ?? prior.PathOn(leg.Side),ct);
            if(occupant!=null)
            {
                if(occupant.Id==prior.IdOn(leg.Side)) throw new ChangedUnderneathException(occupant.Path);
                ItemFailedException.RequireReadable(leg.Side,occupant);
                successor=occupant;
                var present=leg.Side==Side.Local?action.Row with {Local=occupant}:action.Row with {Dropbox=occupant};
                var occupied=present with {Synced=null,Context=present.Context with {FirstRun=false}};
                action=Decide.For(occupied,pair.FirstRun,now,dirty);
                if(action.Kind==ActionKind.NeedLocalHash)
                {
                    occupied=occupied with {LocalHash=await local.ContentHashAsync(occupied.Local!,ct)};
                    action=Decide.For(occupied,pair.FirstRun,now,dirty);
                }
                action=action with {Row=occupied with {Synced=prior}};
            }
        }
        SyncedEntry? incumbent=null;bool occupiedTarget=false;
        if(MoveTarget(action) is {} m)
        {
            var occupant=await (m.Written==Side.Local?local:dropbox).ReadOneAsync(m.Target,ct);
            if(occupant!=null && occupant.Id!=m.Binding.IdOn(m.Written))
            {
                occupiedTarget=true;
                var found=await store.FindAsync(pair.PairId,m.Written,PathNames.KeyFor(m.Written,m.Target),ct);
                if(found!=m.Binding) incumbent=found;
            }
        }
        Task<(bool,EffectOutcome?)> Locked() => ApplyLockedAsync(pair,settings,events,paths,action,candidates,endpoints,successor,missing,incumbent,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,failedNotice,coversThrough,observedMissing,captureFailure);
        var applied=Moves.IsFileSplit(action) ? await Locks.RunAsync(await SplitKeysAsync(pair,action.Row,store,ct),Locked,ct) : await Locks.RunAsync(pair,action,candidates,Locked,ct,settings.CaseRenameSuffix,incumbent,occupiedTarget);
        if(applied.Item1 && candidates.Count!=0 && candidates[0] is {Moved.IsFolder:false} move)
        {
            var settled=await store.FindAsync(pair.PairId,move.Side,PathNames.KeyFor(move.Side,move.Moved.Path),ct);
            if(settled!=null && settled.LocalId==move.Synced.LocalId && settled.DropboxId==move.Synced.DropboxId && settled.PathOn(move.Side)==move.Moved.Path)
            {
                var final=await ItemSync.ObserveBindingAsync(paths,settled,local,dropbox,ct);
                if(final.Group.Count!=0 || final.Row.Local==null || final.Row.Dropbox==null) ItemSync.Retain(action.Row.Key,failed);
                if(await ExecuteWithOutcomeAsync(pair,settings,events,paths,final.Row,[],dirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,addOnly,failedNotice,coversThrough,null,captureFailure) is {Settled:false,Outcome:not EffectOutcome.Accepted}) ItemSync.Retain(action.Row.Key,failed);
            }
        }
        return (applied.Item1,applied.Item2,action);
    }

    // Use final mapped names for the content decision, but commit against the actual old binding.
    private static (string Local,string Dropbox,bool Relink)? MappedChild(Row? row,IReadOnlyList<RenameCandidate> candidates)
    {
        if(candidates.Count==0 && row?.Synced is {IsFolder:false} original && row.Local is {IsFolder:false} left && row.Dropbox is {IsFolder:false} right &&
            left.Id==original.LocalId && right.Id==original.DropboxId && left.Path!=original.LocalPath && right.Path!=original.DropboxPath)
            return (left.Path,right.Path,true);
        if(candidates.Count==0 && row?.Synced is {IsFolder:false} single && (row.Local==null)!=(row.Dropbox==null) && row.Context.Destination is {} far &&
            (row.Local??row.Dropbox) is {IsFolder:false} present && present.Id==single.IdOn(row.Local!=null?Side.Local:Side.Dropbox) &&
            present.Path!=single.PathOn(row.Local!=null?Side.Local:Side.Dropbox) && far!=single.PathOn(row.Local!=null?Side.Dropbox:Side.Local))
            return row.Local!=null?(present.Path,far,false):(far,present.Path,false); // A one-sided child at a confirmed hop: decide at both final names.
        return null;
    }

    private static Row Mapped(Row row,(string Local,string Dropbox,bool Relink) hop) =>
        row with {Synced=hop.Relink ? SyncedEntries.Relink(row.Synced!,row.Local!,row.Dropbox!) : row.Synced! with {LocalPath=hop.Local,LocalKey=PathNames.Key(hop.Local),DropboxPath=hop.Dropbox,DropboxKey=PathNames.DropboxKey(hop.Dropbox)}};

    /// <summary>Attach the live deletion evidence, its clock and, for a dated file deletion, the deletion this name already consumed.</summary>
    internal static async Task<Row> WithDeletionAsync(Row row,string target,bool folder,string pairId,ISide dropbox,IStore store,CancellationToken ct)
    {
        var deletion=await dropbox.DeletedAsync(target,folder,ct); var clock=dropbox.ClockOffset;
        var consumed=deletion is DeletionEvidence.DeletedFile{DeletedAt:{}} ? await store.ConsumedDeletionAsync(pairId,PathNames.DropboxKey(deletion.Path),ct) : null;
        return row with {Deletion=deletion,DeletionClock=clock,ConsumedDeletion=consumed};
    }

    private static SyncAction Redecide(Row row,bool classifyFolder,PairConfig pair,DateTimeOffset now,bool dirty,Action<string,bool>? failed) =>
        classifyFolder?Classify(row,pair,now,dirty,failed):Decide.For(row,pair.FirstRun,now,dirty);

    private static SyncAction Rebound(SyncAction action,SyncedEntry? priorBinding)
    {
        var row=action.Row with {Synced=priorBinding};
        return action.IsInSync()?new(ActionKind.Record,row):action with {Row=row};
    }

    private static SyncAction Classify(Row r,PairConfig pair,DateTimeOffset now,bool dirty,Action<string,bool>? failed)
    {
        var mark=r.Context.ProtectedContents;
        if(mark is null && r.Context.UnreadableCause is null) ItemSync.Retain(r.Key,failed);
        return FolderDeletes.For(r,mark==true,r.Context.UnreadableCause,pair.FirstRun,now,dirty);
    }

    private static bool CreatesBoundLeg(SyncAction action) =>
        action.Kind is (ActionKind.Download or ActionKind.Upload or ActionKind.ReplaceKind or ActionKind.EnsureLocalFolder or ActionKind.EnsureDropboxFolder) && action.Row.EntryOn(action.WrittenSide())==null;

    private static (Side Written,string Target,SyncedEntry Binding)? MoveTarget(SyncAction action) =>
        action.Kind is (ActionKind.MoveLocal or ActionKind.MoveDropbox) && action.Row.Synced is {} moving && action.Target is {} target ? (action.WrittenSide(),target,moving) : null;

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

    private static async Task<(bool,EffectOutcome?)> ApplyLockedAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,SyncAction action,IReadOnlyList<RenameCandidate> candidates,(string Local,string Dropbox) endpoints,Entry? successor,Side? missing,SyncedEntry? incumbent,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,Action<Notice>? failedNotice,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough,(Side Side,Entry? Occupant)? observedMissing,Action<Notice>? captureFailure)
    {
        var row=action.Row;
        var pcCheck=local.ReadOneAsync(endpoints.Local,ct); var dbCheck=dropbox.ReadOneAsync(endpoints.Dropbox,ct);
        await Task.WhenAll(pcCheck,dbCheck);
        ItemFailedException.RequireReadable(Side.Local,await pcCheck); ItemFailedException.RequireReadable(Side.Dropbox,await dbCheck);
        if(row.Synced!=null && row.Local!=null && await pcCheck==null) ItemSync.Retain(row.Key,failed);
        foreach(var side in new[]{Side.Local,Side.Dropbox})
        {
            var seen=side==Side.Local?await pcCheck:await dbCheck;var expected=row.EntryOn(side);
            if(row.Synced is {} owned && (expected==null || successor!=null && missing==side))
            {
                var leg=side==Side.Local?local:dropbox;
                if(await leg.ReadByIdAsync(owned.IdOn(side),ct) is {InScope:true} live)
                {
                    var entry=live.Location.Entry;
                    var outside=side==Side.Local ? paths.Excludes(entry.Path,null,entry.IsFolder) :
                        paths.Excludes(null,entry.Path,entry.IsFolder) || paths.Excludes(await ItemSync.CounterpartAsync(pair,side,entry.Path,store,ct),null,entry.IsFolder);
                    if(!outside && !(side==Side.Local && seen==null && await IsUnlinkAsync(pair,leg,owned,store,ct))) throw new ChangedUnderneathException(owned.PathOn(side));
                }
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
        if(action.Kind==ActionKind.DeleteLocal && row.Synced!=null && row.Dropbox==null && (await dropbox.RootAsync(ct)).Dropbox!=pair.Roots?.Dropbox)
            throw new ChangedUnderneathException(pair.DropboxRoot);
        // Effects owns physical-leg logging and passes the original hop callback between legs.
        var outcome=await new Effects().ApplyAsync(pair,settings,events,paths,action,local,dropbox,store,populate,compareOldBinding,ct,heldInput,coversThrough,failedNotice,grantedIncumbent:incumbent,captureFailure:captureFailure);
        if(outcome==EffectOutcome.Pending) ItemSync.Retain(row.Key,failed);
        // Vacating another binding only clears the destination; the arriving move still owns its input.
        if(outcome==EffectOutcome.Applied && action.Kind==ActionKind.ForgetOnly && candidates.Count!=0 && row.Synced!=candidates[0].Synced) ItemSync.Retain(row.Key,failed);
        if(outcome==EffectOutcome.Refused) return (false,outcome); // The notice is the decision, not a race.
        if(outcome==EffectOutcome.Accepted) return (false,outcome); // Released without a positive claim; the saved job settles the effect, a resend at worst.
        return (true,outcome);
    }

    // A removed local name is an unlink only when the old name is gone and every remaining in-scope name belongs to
    // another binding of that same file. Each remaining name must be bound by its local identity, not merely its key;
    // a name that is unbound or bound to a different file keeps the ordinary refusal. Non-local sides never answer.
    private static async Task<bool> IsUnlinkAsync(PairConfig pair,ISide local,SyncedEntry owned,IStore store,CancellationToken ct)
    {
        var names=await local.NamesByIdAsync(owned.LocalId,ct);
        if(names is null || names.Count==0) return false;
        foreach(var name in names)
        {
            var key=PathNames.Key(name);
            if(key==owned.LocalKey) return false;
            if(await store.FindAsync(pair.PairId,Side.Local,key,ct) is not { } binding || binding.LocalId!=owned.LocalId || binding.LocalKey==owned.LocalKey) return false;
        }
        return true;
    }

    // Ancestors are selected before acquisition. The selected-entry overload cannot widen after the grant.
    internal static async Task<bool> RemoveBoundAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,SyncedEntry folder,Side deleted,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>>? heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding,Action<string,bool>? failed,CancellationToken ct,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,Action<Notice>? captureFailure=null)
    {
        var source=deleted==Side.Local?local:dropbox; var target=deleted==Side.Local?dropbox:local;
        var highest=await FolderDelete.HighestMissingAsync(pair,paths,folder,source,store,ct);
        if(highest is null) { ItemSync.Retain(folder.KeyOn(deleted),failed); return false; }
        folder=highest;
        if(paths.ExcludesBinding(folder)) return false; // excluded: no effect, as for an excluded file; the binding stays for re-inclusion
        var row=new Row(folder.KeyOn(deleted),null,null,folder,[],null,RowContext.Neutral);
        Entry? movedPeer=null;
        var leased=await Locks.RunAsync(pair,new(ActionKind.DeferFolder,row),[],async ()=>
        {
            if(heldInput==null || await store.FindAsync(pair.PairId,deleted,folder.KeyOn(deleted),ct)!=folder || await source.ReadByIdAsync(folder.IdOn(deleted),ct) is {InScope:true}) ItemSync.Retain(row.Key,failed);
            var found=await target.ReadByIdAsync(folder.IdOn(target.Side),ct);
            if(found is not {InScope:true} at)
            {
                var range=new Scope(folder.LocalPath,folder.DropboxPath);
                await compareOldBinding(folder,range,range,ct);
                await store.CommitAsync(pair.PairId,folder,null,null,null,ct);
                return true;
            }
            if(at.Location.Entry is {IsFolder:true} peer && peer.Id==folder.IdOn(target.Side) && !PathNames.Same(peer.Path,folder.PathOn(target.Side))) { movedPeer=peer; return false; }
            if(!at.Location.Entry.IsFolder || at.Location.Entry.Id!=folder.IdOn(target.Side) || !PathNames.Same(at.Location.Entry.Path,folder.PathOn(target.Side))) { ItemSync.Retain(row.Key,failed); return false; }
            var selected=at.Location.Entry; ItemFailedException.RequireReadable(target.Side,selected);
            return await ItemSync.RequireRemovalAsync(new FolderDelete().ApplyAsync(pair,settings,events,paths,selected,folder,target.Side,SyncAction.DeleteOn(target.Side),local,dropbox,store,heldInput!,populate,compareOldBinding,ct,resolveConflict:(where,side,version,token)=>new Effects().RecoverConflictAsync(pair,settings,events,paths,where,side,version,local,dropbox,store,populate,compareOldBinding,token),coversThrough:coversThrough),()=>failed?.Invoke(row.Key,false),folder.LocalPath);
        },ct);
        if(movedPeer!=null) return await ExecuteAsync(pair,settings,events,paths,null,[new(target.Side,movedPeer,folder)],false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false,null,coversThrough,null,captureFailure);
        return leased;
    }
}
