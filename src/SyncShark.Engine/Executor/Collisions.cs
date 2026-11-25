using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Resolve an occupied move or publish target without deleting a third version or a replacement at the obsolete path.</summary>
public static class Collisions
{
    // Effects holds no state, so one instance serves every nested apply.
    private static readonly Effects Nested = new();

    // Only the planner can vacate a binding. A changed prerequisite always causes a fresh decision, not a move in this call.
    internal static async Task<(bool Ready,SyncedEntry? Owner)> MoveReadyAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Side written,Entry selected,string target,Entry? peer,SyncedEntry? saved,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compare,CancellationToken ct,SyncedEntry? grantedIncumbent)
    {
        var other=PathNames.Other(written);
        var mapped=await store.FindAsync(pair.PairId,written,PathNames.KeyFor(written,target),ct);
        if (grantedIncumbent!=null && mapped!=grantedIncumbent) throw new ChangedUnderneathException(target);
        var peerPath=peer?.Path ?? await ItemSync.CounterpartAsync(pair,written,target,store,ct);
        var peerOwner=await store.FindAsync(pair.PairId,other,PathNames.KeyFor(other,peerPath),ct);
        if ((mapped==null || mapped==saved) && (peerOwner==null || peerOwner==saved)) return (true,null);
        if (saved==null || selected.Id!=saved.IdOn(written) || peer==null || peer.Id!=saved.IdOn(other)) throw new ItemFailedException(target,TextKey.ClashNameTaken,null);
        IReadOnlyList<RenameCandidate> candidates=written==Side.Dropbox && PathNames.Same(selected.Path,PathNames.CaseRenamePath(saved.DropboxPath,settings.CaseRenameSuffix))
            ? [new(other,peer,saved),new(written,selected,saved)] : [new(other,peer,saved)];
        var decision=await MoveDecision.DecideMoveAsync(pair,settings,paths,candidates,local,dropbox,store,false,ct,target);
        if (decision.Kind==ActionKind.ForgetOnly || decision.Kind==ActionKind.Notice)
            await Nested.ApplyAsync(pair,settings,events,paths,decision,local,dropbox,store,populate,compare,ct);
        else if (decision.Kind is ActionKind.MoveLocal or ActionKind.MoveDropbox)
        {
            if (mapped!=null && mapped!=saved && (peerOwner==null || peerOwner==saved) && decision.WrittenSide()==written && decision.Target==target && decision.Row.EntryOn(written)==selected && decision.Row.EntryOn(other)==peer)
            {
                if(mapped!=grantedIncumbent) throw new ChangedUnderneathException(target);
                return (true,mapped);
            }
            throw new ItemFailedException(target,TextKey.ClashNameTaken,null);
        }
        return (false,null);
    }

    /// <summary>Merge the selected folder into the folder already at its target; null when the merge settled.</summary>
    internal static async Task<EffectOutcome?> MergeAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Side written,ISide to,Entry expected,Entry atTarget,Entry? source,SyncedEntry? saved,string target,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> held,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough,SyncedEntry? grantedIncumbent,CancellationToken ct)
    {
        await ItemLocks.PromoteAsync(ct);
        var admission=await MoveReadyAsync(pair,settings,events,paths,written,expected,atTarget.Path,source,saved,local,dropbox,store,populate,compareOldBinding,ct,grantedIncumbent);
        if(!admission.Ready) return EffectOutcome.Pending;
        if(admission.Owner!=null) return await BoundCollisionAsync(admission.Owner,expected,source!,saved!,written,atTarget.Path,pair,settings,events,paths,local,dropbox,store,held,populate,compareOldBinding,coversThrough,ct);
        var result = await new FolderMerge().ApplyAsync(pair,settings,events,paths,expected,atTarget,written,local,dropbox,store,held,populate,compareOldBinding,ct,
            collide:(child,occupant,peer,old,token)=>CollisionAsync(pair,settings,events,paths,written,child,occupant,peer,old,local,dropbox,store,held,populate,compareOldBinding,token,coversThrough),coversThrough:coversThrough,
            merged:t=>CompareMergedAsync(saved!,source is {} peer ? (written==Side.Local ? new Scope(atTarget.Path,peer.Path) : new Scope(peer.Path,atTarget.Path)) : throw new ChangedUnderneathException(target),target,compareOldBinding,t));
        if (result.Outcome != RemovalOutcome.Removed) return RemovalReceipts.Incomplete(result,target);
        if (!KeepBoth.SameObservation(written,atTarget,await Effects.ReadOneAsync(to,target,ct))) throw new ChangedUnderneathException(target){FailedSide=to.Side};
        return null;
    }

    // The merged root owns comparison failures even when the unreadable item is a child.
    private static async Task CompareMergedAsync(SyncedEntry root,Scope merged,string target,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compare,CancellationToken ct)
    {
        try { await compare(root,merged,null,ct); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is (ItemFailedException or ChangedUnderneathException or InventoryIncompleteException or IOException))
        { throw new ItemFailedException(target,(e as ItemFailedException)?.Cause??TextKey.FailChanged,e); }
    }

    internal static async Task<EffectOutcome> BoundCollisionAsync(SyncedEntry owner,Entry selected,Entry peer,SyncedEntry saved,Side written,string target,PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> held,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compare,Func<(JournalPosition? Local,string? Dropbox),bool>? covers,CancellationToken ct)
    {
        var to=written==Side.Local?local:dropbox; var from=written==Side.Local?dropbox:local;
        var incumbent=await Effects.ReadOneAsync(to,target,ct); var partner=await Effects.ReadOneAsync(from,owner.PathOn(from.Side),ct);
        if (incumbent is not {Skip:null} || incumbent.Id!=owner.IdOn(written) || partner is not {Skip:null} || partner.Id!=owner.IdOn(from.Side) ||
            incumbent.IsFolder!=partner.IsFolder || paths.ExcludesBinding(owner) || paths.HoldsNestedRoot(from.Side,partner.Path) ||
            !KeepBoth.SameObservation(written,selected,await Effects.ReadOneAsync(to,selected.Path,ct)) || !KeepBoth.SameObservation(from.Side,peer,await Effects.ReadOneAsync(from,peer.Path,ct)) ||
            await store.FindAsync(pair.PairId,written,owner.KeyOn(written),ct)!=owner ||
            await store.FindAsync(pair.PairId,written,saved.KeyOn(written),ct)!=saved) throw new ChangedUnderneathException(target);
        var row=new Row(saved.LocalKey,written==Side.Local?selected:peer,written==Side.Dropbox?selected:peer,saved,[],null,RowContext.Neutral);
        if (selected.IsFolder!=peer.IsFolder) throw new ChangedUnderneathException(peer.Path);
        if (selected.IsFolder && incumbent.IsFolder) return await FolderCollisionAsync(pair,settings,events,paths,written,from,selected,peer,incumbent,partner,saved,local,dropbox,store,held,populate,compare,covers,ct);
        SyncAction? transfer=null;
        if(!selected.IsFolder && !incumbent.IsFolder)
        {
            EffectOutcome? settled;
            (settled,row,transfer)=await FileCollisionAsync(pair,settings,events,paths,written,to,from,row,selected,peer,incumbent,partner,saved,local,dropbox,store,held,populate,compare,covers,ct);
            if (settled is {} done) return done;
        }
        return await RelocateAsync(pair,settings,events,paths,written,to,from,row,selected,peer,incumbent,partner,saved,transfer,local,dropbox,store,held,populate,compare,covers,ct);
    }

    private static async Task<EffectOutcome> FolderCollisionAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Side written,ISide from,Entry selected,Entry peer,Entry incumbent,Entry partner,SyncedEntry saved,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> held,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compare,Func<(JournalPosition? Local,string? Dropbox),bool>? covers,CancellationToken ct)
    {
        if(covers==null) return EffectOutcome.Pending;
        foreach(var (side,source,dest) in new[]{(written,selected,incumbent),(from.Side,peer,partner)})
        {
            var merge=await new FolderMerge().ApplyAsync(pair,settings,events,paths,source,dest,side,local,dropbox,store,held,populate,compare,ct,
                collide:(child,occupied,mate,old,t)=>CollisionAsync(pair,settings,events,paths,side,child,occupied,mate,old,local,dropbox,store,held,populate,compare,t,covers),coversThrough:covers);
            if(merge.Outcome!=RemovalOutcome.Removed) return RemovalReceipts.Incomplete(merge,source.Path);
        }
        await store.CommitAsync(pair.PairId,saved,null,null,null,ct);
        return EffectOutcome.Applied;
    }

    private static async Task<(EffectOutcome? Done, Row Row, SyncAction? Transfer)> FileCollisionAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Side written,ISide to,ISide from,Row row,Entry selected,Entry peer,Entry incumbent,Entry partner,SyncedEntry saved,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> held,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compare,Func<(JournalPosition? Local,string? Dropbox),bool>? covers,CancellationToken ct)
    {
        SyncAction? transfer=null;
        var hash=await to.ContentHashAsync(selected,ct);
        var peerHash=await from.ContentHashAsync(peer,ct);
        row=row with {LocalHash=written==Side.Local?hash:peerHash};
        if(hash!=peerHash)
        {
            transfer=Decide.For(row,pair.FirstRun,DateTimeOffset.UtcNow,true);
            if(transfer.Kind is not (ActionKind.Upload or ActionKind.Download))
                return (await Nested.ApplyAsync(pair,settings,events,paths,transfer,local,dropbox,store,populate,compare,ct,held,covers),row,transfer);
            hash=transfer.WrittenSide()==written?peerHash:hash;
        }
        if(hash==await to.ContentHashAsync(incumbent,ct) && hash==await from.ContentHashAsync(partner,ct))
        {
            if(covers==null) return (EffectOutcome.Pending,row,transfer);
            var pc=row.Local!; var db=row.Dropbox!;
            var selection=await RemovalReceipts.SelectAsync(pair,local,pc,saved,row.LocalHash,ct);
            var recycled=await local.DeleteAsync(pc,selection,ct);
            var receipt=await RemovalReceipts.CheckReceiptAsync(pair,new(pc.Path,db.Path),selection,null,recycled,db,ActionKind.DeleteLocal,local,dropbox,store,held,populate,
                (scope,side,version,t)=>Nested.RecoverConflictAsync(pair,settings,events,paths,scope,side,version,local,dropbox,store,populate,compare,t),covers,ct);
            if(receipt.Outcome!=RemovalOutcome.Removed) return (RemovalReceipts.Incomplete(receipt,pc.Path),row,transfer);
            var retired=await dropbox.DeleteAsync(db,null,ActionKind.DeleteDropbox,ct);
            if(retired.Outcome!=RemovalOutcome.Removed) return (RemovalReceipts.Incomplete(retired,db.Path),row,transfer);
            await store.CommitAsync(pair.PairId,saved,null,null,null,ct);
            await store.DisarmAsync(pair.PairId,selection!,null,ct);
            return (EffectOutcome.Applied,row,transfer);
        }
        return (null,row,transfer);
    }

    private static async Task<EffectOutcome> RelocateAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Side written,ISide to,ISide from,Row row,Entry selected,Entry peer,Entry incumbent,Entry partner,SyncedEntry saved,SyncAction? transfer,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> held,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compare,Func<(JournalPosition? Local,string? Dropbox),bool>? covers,CancellationToken ct)
    {
        await ItemLocks.PromoteAsync(ct);
        var incoming=from.Side;
        var pc=row.Local!; var db=row.Dropbox!;
        var pcBase=written==Side.Local?incumbent.Path:pc.Path; var dbBase=written==Side.Dropbox?incumbent.Path:db.Path;
        for(int ordinal=1;;ordinal=checked(ordinal+1))
        {
            var left=ConflictName.For(pcBase,incoming,peer.Modified,settings.ComputerName,settings,ordinal);
            var right=ConflictName.For(dbBase,incoming,peer.Modified,settings.ComputerName,settings,ordinal);
            var locks=new ItemLocks();
            var outcome=await locks.RunAsync<EffectOutcome?>(pair,new(ActionKind.KeepBoth,row,Target:left,Side:Side.Local),[],()=>
                locks.RunAsync<EffectOutcome?>(pair,new(ActionKind.KeepBoth,row,Target:right,Side:Side.Dropbox),[],()=>
                    RelocatedAsync(pair,settings,events,paths,written,to,from,row,selected,peer,incumbent,partner,saved,transfer,left,right,local,dropbox,store,held,populate,compare,covers,ct),ct),ct);
            if(outcome.HasValue) return outcome.Value;
        }
    }

    // Runs under both name locks; null when either conflict name is already taken.
    private static async Task<EffectOutcome?> RelocatedAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Side written,ISide to,ISide from,Row row,Entry selected,Entry peer,Entry incumbent,Entry partner,SyncedEntry saved,SyncAction? transfer,string left,string right,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> held,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compare,Func<(JournalPosition? Local,string? Dropbox),bool>? covers,CancellationToken ct)
    {
        var incoming=from.Side; var pc=row.Local!; var db=row.Dropbox!; var endpoints=new Scope(pc.Path,db.Path);
        if(await Effects.ReadOneAsync(local,left,ct)!=null || await Effects.ReadOneAsync(dropbox,right,ct)!=null) return null;
        var mover=new CaseRename();
        // Move the already-renamed identity first: before the second hop its peer is still at the saved path.
        var first=await mover.MoveAsync(from,peer,incoming==Side.Local?left:right,settings.CaseRenameSuffix,saved,endpoints,compare,ct,events);
        if(first.Outcome!=RemovalOutcome.Published) return RemovalReceipts.Incomplete(first,peer.Path);
        var next=incoming==Side.Local?endpoints with {LocalPath=left}:endpoints with {DropboxPath=right};
        var second=await mover.MoveAsync(to,selected,written==Side.Local?left:right,settings.CaseRenameSuffix,saved,next,compare,ct,events);
        if(second.Outcome!=RemovalOutcome.Published) return RemovalReceipts.Incomplete(second,selected.Path);
        var a=incoming==Side.Local?first:second; var b=incoming==Side.Local?second:first;
        var localKept=written==Side.Local?incumbent:partner; var remoteKept=written==Side.Dropbox?incumbent:partner;
        var pcKept=new NoticeItem(Side.Local,localKept.Path,localKept.Id); var dbKept=new NoticeItem(Side.Dropbox,remoteKept.Path,remoteKept.Id);
        var pcFinal=a.Final!; var dbFinal=b.Final!;
        if(saved.IsFolder && pc.IsFolder)
            await store.ProjectSubtreeAsync(pair.PairId,new(saved.LocalPath,saved.DropboxPath),new(left,right),ct);
        SyncedEntry link;
        if(transfer!=null)
        {
            // A by-ID move confirms identity, not the edition used to choose the transfer.
            if(await local.ContentHashAsync(pcFinal,ct)!=row.LocalHash || pcFinal.Modified!=pc.Modified)
                throw new ChangedUnderneathException(left){FailedSide=Side.Local};
            if(dbFinal.ContentHash!=db.ContentHash || dbFinal.Modified!=db.Modified ||
                !KeepBoth.SameObservation(Side.Dropbox,dbFinal,await Effects.ReadOneAsync(dropbox,right,ct)))
                throw new ChangedUnderneathException(right){FailedSide=Side.Dropbox};
            link=SyncedEntries.Relink(saved,pcFinal,dbFinal);
            await store.CommitAsync(pair.PairId,saved,link,null,null,ct);
            var moved=row with {Key=link.LocalKey,Local=pcFinal,Dropbox=dbFinal,Synced=link};
            var applied=await Nested.ApplyAsync(pair,settings,events,paths,transfer with {Row=moved,Target=link.PathOn(transfer.WrittenSide())},local,dropbox,store,populate,compare,ct,held,covers);
            if(applied!=EffectOutcome.Applied) return applied;
            link=await store.FindAsync(pair.PairId,Side.Local,link.LocalKey,ct) ?? throw new ChangedUnderneathException(left);
            saved=link;
        }
        else link=SyncedEntries.From(pcFinal,dbFinal);
        List<NoticeItem> items=[pcKept,dbKept,new(Side.Local,left,link.LocalId),new(Side.Dropbox,right,link.DropboxId)];
        await store.CommitKeepBothAsync(pair.PairId,saved,link,new(0,NoticeKind.Info,TextKey.InfoKeptBothRetained,pcKept.Path,new(items,null),DateTimeOffset.UtcNow,false),ct);
        return EffectOutcome.Applied;
    }

    // A new publish found its name taken: adopt equal content, otherwise keep both; (null,null) when the occupant is gone or skipped, so the caller rethrows.
    internal static async Task<(EffectOutcome? Done, RemovalResult? Result)> OccupiedAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Row row, Side written, ISide to, ISide from, Entry source, string target, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Action<SyncedEntry>? collectCreate, CancellationToken ct)
    {
        var competitor = await Effects.ReadOneAsync(to,target,ct);
        if (competitor == null || competitor.Skip != null) return (null,null);
        if (await Effects.ReadOneAsync(from,source.Path,ct) != source) throw new ChangedUnderneathException(source.Path){FailedSide=from.Side};
        if (competitor.IsFolder || await to.ContentHashAsync(competitor,ct) != await from.ContentHashAsync(source,ct))
        {
            if(collectCreate!=null) throw new ItemFailedException(target,TextKey.ClashNameTaken,null);
            var conflict = written == Side.Local ? row with {Local=competitor} : row with {Dropbox=competitor};
            return (await new KeepBoth().ApplyAsync(pair,settings,events,paths,conflict,local,dropbox,store,populate,ct),null);
        }
        return (null,new RemovalResult(RemovalOutcome.Published,competitor,null));
    }

    /// <summary>Resolve the selected final-name collision without deleting a third version or a replacement at the obsolete path.</summary>
    public static async Task<EffectOutcome> CollisionAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Side written, Entry selected, Entry incumbent, Entry? counterpart, SyncedEntry? saved, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,SyncedEntry? grantedIncumbent=null)
    {
        await ItemLocks.PromoteAsync(ct);
        var to = written == Side.Local ? local : dropbox; var from = written == Side.Local ? dropbox : local;
        if (selected.Id==incumbent.Id) throw new ChangedUnderneathException(incumbent.Path){FailedSide=to.Side};
        if (selected.Skip!=null || incumbent.Skip!=null) throw new ItemFailedException(incumbent.Path,TextKey.ClashNameTaken,null);
        if (!KeepBoth.SameObservation(to.Side,selected,await Effects.ReadOneAsync(to,selected.Path,ct))) throw new ChangedUnderneathException(selected.Path){FailedSide=to.Side};
        if (!KeepBoth.SameObservation(to.Side,incumbent,await Effects.ReadOneAsync(to,incumbent.Path,ct))) throw new ChangedUnderneathException(incumbent.Path){FailedSide=to.Side};
        var admission=await MoveReadyAsync(pair,settings,events,paths,written,selected,incumbent.Path,counterpart,saved,local,dropbox,store,populate,compareOldBinding,ct,grantedIncumbent);
        if (!admission.Ready) return EffectOutcome.Pending;
        if (admission.Owner!=null) return await BoundCollisionAsync(admission.Owner,selected,counterpart!,saved!,written,incumbent.Path,pair,settings,events,paths,local,dropbox,store,heldInput,populate,compareOldBinding,coversThrough,ct);
        var peerPath=counterpart?.Path ?? await ItemSync.CounterpartAsync(pair,written,incumbent.Path,store,ct);
        if (counterpart == null)
        {
            if (paths.Excludes(written==Side.Local?incumbent.Path:peerPath,written==Side.Dropbox?incumbent.Path:peerPath,selected.IsFolder)) throw new ItemFailedException(incumbent.Path,TextKey.ClashNameTaken,null);
            var (created,fresh) = await CounterpartMissingAsync(written,selected,peerPath,to,from,populate,ct);
            if (created is {} stopped) return stopped;
            counterpart = fresh!;
        }
        if (counterpart.Skip!=null || !KeepBoth.SameObservation(from.Side,counterpart,await Effects.ReadOneAsync(from,peerPath,ct))) throw new ChangedUnderneathException(peerPath){FailedSide=from.Side};
        var (decided,selectedHash) = await SameContentAsync(pair,settings,events,paths,written,selected,incumbent,counterpart,saved,peerPath,to,from,local,dropbox,store,heldInput,populate,compareOldBinding,ct);
        if (decided is {} fresher) return fresher;
        var row = new Row(PathNames.Key(incumbent.Path),written == Side.Local ? incumbent : counterpart,written == Side.Dropbox ? incumbent : counterpart,saved,[],null,RowContext.Neutral);
        bool equal = !incumbent.IsFolder && selectedHash != null && await to.ContentHashAsync(incumbent,ct) == selectedHash;
        if (written==Side.Local && coversThrough==null) return EffectOutcome.Pending;
        if (!equal)
            return await KeepBoth.RunCopiesAsync(pair,settings,events,row,local,dropbox,store,populate,null,async (first,second,notice)=>
            {
                var (removed,selection)=await CleanupAsync(pair,settings,events,paths,written,selected,incumbent,counterpart,saved,selectedHash,to,from,local,dropbox,store,heldInput,populate,compareOldBinding,coversThrough,ct);
                if (removed!=EffectOutcome.Applied) return removed;
                await store.CommitKeepBothAsync(pair.PairId,row.Synced,first,second!,notice,ct);
                await Disarm(pair,store,selection,ct);
                return EffectOutcome.Applied;
            },ct);
        var (cleaned,cleanup) = await CleanupAsync(pair,settings,events,paths,written,selected,incumbent,counterpart,saved,selectedHash,to,from,local,dropbox,store,heldInput,populate,compareOldBinding,coversThrough,ct);
        if (cleaned != EffectOutcome.Applied) return cleaned;
        await store.CommitAsync(pair.PairId,saved,SyncedEntries.From(written,incumbent,counterpart),null,null,ct);
        await Disarm(pair,store,cleanup,ct);
        return EffectOutcome.Applied;
    }

    private static async Task<(EffectOutcome? Done, Entry? Counterpart)> CounterpartMissingAsync(Side written, Entry selected, string peerPath, ISide to, ISide from, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, CancellationToken ct)
    {
        var counterpart = await Effects.ReadOneAsync(from,peerPath,ct);
        if (counterpart!=null) return (null,counterpart);
        RemovalResult copy;
        if (selected.IsFolder) copy = await from.CreateFolderExclusiveAsync(peerPath,ct);
        else
        {
            await using var bytes = await Effects.OpenReadAsync(to,selected,ct);
            await using var staged = await from.StageAsync(peerPath,bytes,selected,null,ct);
            copy = await from.PublishAsync(staged,null,null,ct);
        }
        if (copy.Outcome!=RemovalOutcome.Published) return (RemovalReceipts.Incomplete(copy,peerPath),null);
        if (copy.Final is not {} fresh || fresh.Path!=peerPath || fresh.IsFolder!=selected.IsFolder) throw new ChangedUnderneathException(peerPath);
        if (selected.IsFolder) await populate(written==Side.Local?new(selected.Path,peerPath):new(peerPath,selected.Path),written,new(selected,to.ReadAllAsync(selected.Path,new(true,false),ct)),ct);
        return (null,fresh);
    }

    private static async Task<(EffectOutcome? Done, string? SelectedHash)> SameContentAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Side written, Entry selected, Entry incumbent, Entry counterpart, SyncedEntry? saved, string peerPath, ISide to, ISide from, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        string? selectedHash=selected.IsFolder?null:await to.ContentHashAsync(selected,ct);
        if (selected.IsFolder!=counterpart.IsFolder || selectedHash!=null && !StringComparer.OrdinalIgnoreCase.Equals(selectedHash,await from.ContentHashAsync(counterpart,ct)))
        {
            if (saved==null || selected.Id!=saved.IdOn(written) || counterpart.Id!=saved.IdOn(from.Side)) throw new ItemFailedException(peerPath,TextKey.ClashNameTaken,null);
            var decision=await MoveDecision.DecideMoveAsync(pair,settings,paths,[new(from.Side,counterpart,saved)],local,dropbox,store,written==Side.Local,ct);
            if (decision.Kind is not (ActionKind.MoveLocal or ActionKind.MoveDropbox))
            {
                var outcome=await Nested.ApplyAsync(pair,settings,events,paths,decision,local,dropbox,store,populate,compareOldBinding,ct,heldInput);
                // This was a fresh planner decision, not proof that the merge child was removed.
                return (outcome==EffectOutcome.Applied?EffectOutcome.Pending:outcome,selectedHash);
            }
            if (decision.WrittenSide()!=written || decision.Target!=incumbent.Path || decision.Row.EntryOn(written)?.Id!=selected.Id) throw new ChangedUnderneathException(selected.Path);
        }
        return (null,selectedHash);
    }

    private static async Task<(EffectOutcome Outcome, RemovalSelection? Selection)> CleanupAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Side written, Entry selected, Entry incumbent, Entry counterpart, SyncedEntry? saved, string? selectedHash, ISide to, ISide from, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough, CancellationToken token)
    {
        RemovalSelection? cleanupSelection = null;
        if (!KeepBoth.SameObservation(written,selected,await Effects.ReadOneAsync(to,selected.Path,token))) throw new ChangedUnderneathException(selected.Path){FailedSide=to.Side};
        if (!KeepBoth.SameObservation(from.Side,counterpart,await Effects.ReadOneAsync(from,counterpart.Path,token))) throw new ChangedUnderneathException(counterpart.Path){FailedSide=from.Side};
        if (!KeepBoth.SameObservation(written,incumbent,await Effects.ReadOneAsync(to,incumbent.Path,token))) throw new ChangedUnderneathException(incumbent.Path){FailedSide=to.Side};
        var removal = selected.IsFolder
            ? await new FolderDelete().ApplyAsync(pair,settings,events,paths,selected,saved,written,written==Side.Local?ActionKind.MoveLocal:ActionKind.MoveDropbox,local,dropbox,store,heldInput,populate,compareOldBinding,token,coversThrough:coversThrough)
            : written==Side.Dropbox ? await to.DeleteAsync(selected,null,ActionKind.MoveDropbox,token) : await to.DeleteAsync(selected,cleanupSelection=await RemovalReceipts.SelectAsync(pair,to,selected,saved,selectedHash,token),token);
        var receipt = await RemovalReceipts.CheckReceiptAsync(pair,new(selected.Path,counterpart.Path),cleanupSelection,null,removal,counterpart,written==Side.Local?ActionKind.MoveLocal:ActionKind.MoveDropbox,local,dropbox,store,heldInput,populate,(scope,side,version,t)=>Nested.RecoverConflictAsync(pair,settings,events,paths,scope,side,version,local,dropbox,store,populate,compareOldBinding,t),coversThrough,token);
        return (receipt.Outcome == RemovalOutcome.Removed ? EffectOutcome.Applied : RemovalReceipts.Incomplete(receipt,selected.Path),cleanupSelection);
    }

    private static async Task Disarm(PairConfig pair, IStore store, RemovalSelection? selection, CancellationToken ct)
    {
        if (selection != null) await store.DisarmAsync(pair.PairId,selection,null,ct);
    }
}
