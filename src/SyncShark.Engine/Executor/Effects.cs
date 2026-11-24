using Massdex.SyncShark.Engine.Planner;
using Step = (Massdex.SyncShark.Engine.Executor.EffectOutcome? Done, Massdex.SyncShark.Engine.SyncedEntry? After, Massdex.SyncShark.Engine.JournalPosition? Boundary, Massdex.SyncShark.Engine.RemovalSelection? Armed, Massdex.SyncShark.Engine.StageProof? Proof, Massdex.SyncShark.Engine.Entry? Merged);

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Apply selected effects; never settle a binding from acceptance or an uncertain receipt.</summary>
public sealed class Effects
{
    /// <summary>ItemSync/Retries own failure turnover. Dispatcher saves accepted jobs and signals the per-pair held-news checker.</summary>
    public async Task<EffectOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncAction action, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput = null, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough = null, Action<Notice>? failedNotice = null, bool userSelected = false, SyncedEntry? grantedIncumbent = null, Action<SyncedEntry>? collectCreate = null)
    {
        ct.ThrowIfCancellationRequested();
        var row = action.Row; var saved = row.Synced; var path = Row.Path(row);
        var written = action.Kind is ActionKind.None or ActionKind.Record or ActionKind.ForgetOnly or ActionKind.Notice or ActionKind.KeepBoth ? Side.Local : action.WrittenSide();
        var to = written == Side.Local ? local : dropbox; var from = written == Side.Local ? dropbox : local;
        var expected = row.EntryOn(written); var source = row.EntryOn(from.Side);
        var target = action.Target ?? expected?.Path ?? row.Context.Destination ?? saved?.PathOn(written) ?? PathNames.SpellingOn(written,source?.Path ?? path);
        var held = Held(heldInput);
        Step step = default;
        switch (action.Kind)
        {
            case ActionKind.None: return action.IsInSync() ? EffectOutcome.Applied : EffectOutcome.Pending;
            case ActionKind.Notice: await Raise(pair,store,events,action,failedNotice,action.NoticeKind,action.Cause,ct); return EffectOutcome.Refused;
            case ActionKind.ForgetOnly when Moves.IsFileSplit(action): return await SplitAsync(pair,settings,events,paths,action,local,dropbox,store,populate,compareOldBinding,ct);
            case ActionKind.ForgetOnly: break;
            case ActionKind.Record: step.After = await RecordAsync(row,local,populate,compareOldBinding,ct); break;
            case ActionKind.KeepBoth: return await new KeepBoth().ApplyAsync(pair,settings,events,paths,row,local,dropbox,store,populate,ct,action.Side);
            case ActionKind.DeleteLocal: case ActionKind.DeleteDropbox: { RemovalSelection? armed = null; var removed = await RemovalReceipts.RemoveAsync(pair,settings,events,paths,action,expected ?? throw new ChangedUnderneathException(target),written,to,source,local,dropbox,store,held,heldInput,populate,compareOldBinding,coversThrough,userSelected,selection=>armed=selection,ct); if (removed.Outcome != RemovalOutcome.Removed) return RemovalReceipts.Incomplete(removed,target); step.Armed = armed; break; }
            case ActionKind.MoveLocal: case ActionKind.MoveDropbox: case ActionKind.MoveUnlinked: step = await MoveAsync(pair,settings,events,paths,action,written,to,expected,source,target,local,dropbox,store,held,populate,compareOldBinding,coversThrough,grantedIncumbent,ct); break;
            case ActionKind.Upload: case ActionKind.Download: case ActionKind.ReplaceKind: case ActionKind.EnsureLocalFolder: case ActionKind.EnsureDropboxFolder: step = Admitted(expected,source,target).IsFolder ? await TransferFolderAsync(pair,settings,events,paths,action,written,to,from,expected,source!,target,local,dropbox,store,held,heldInput,populate,compareOldBinding,coversThrough,userSelected,ct) : await TransferFileAsync(pair,settings,events,paths,action,written,to,from,expected,source!,target,local,dropbox,store,held,heldInput,populate,compareOldBinding,coversThrough,failedNotice,userSelected,collectCreate,ct); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
        if (step.Done is {} done) return done;
        var (_,after,boundary,armed,proof,mergedSource) = step;
        if (after is {IsFolder:true} && saved is {IsFolder:true} && (saved.LocalPath != after.LocalPath || saved.DropboxPath != after.DropboxPath))
            await store.ProjectSubtreeAsync(pair.PairId,new(saved.LocalPath,saved.DropboxPath),new(after.LocalPath,after.DropboxPath),ct);
        if(collectCreate!=null) { collectCreate(after! with {LocalPublished=boundary}); return EffectOutcome.Applied; }
        var spent = row.Deletion is DeletionEvidence.DeletedFile or DeletionEvidence.DeletedPath ? PathNames.DropboxKey(row.Deletion.Path) : null;
        await store.CommitAsync(pair.PairId,saved,after,boundary,spent,ct);
        // A merged destination holds both sides' children; compare it once at its final names. Merge settlement owns the root.
        if (mergedSource!=null)
        {
            try { await compareOldBinding(after!,new(after!.LocalPath,after.DropboxPath),null,ct); }
            catch (Exception e) when (!ct.IsCancellationRequested && e is (ItemFailedException or ChangedUnderneathException or InventoryIncompleteException or IOException))
            { throw new ItemFailedException(target,(e as ItemFailedException)?.Cause??TextKey.FailChanged,e); }
        }
        if (action.InfoAfter is {} info) await Raise(pair,store,events,action,failedNotice,NoticeKind.Info,info,ct);
        if (armed != null && await store.ArmedAsync(pair.PairId,ct) is {} note && note.Victim == armed && note.Stage == proof) await store.DisarmAsync(pair.PairId,note.Victim,note.Stage,ct);
        if (mergedSource!=null) events(new(RunEventKind.Effect,nameof(ISide.MoveAsync),target,mergedSource.Path,nameof(RemovalOutcome.Published)){Action=action.Kind,Side=written});
        else if (action.Kind is not (ActionKind.MoveDropbox or ActionKind.MoveLocal)) events(new(RunEventKind.Effect,action.Kind.ToString(),path,string.Empty,nameof(EffectOutcome.Applied)) { Action = action.Kind, Side = action.Side });
        return EffectOutcome.Applied;
    }

    private static Step Stop(EffectOutcome done) => (done,null,null,null,null,null);

    private async Task<EffectOutcome> SplitAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncAction action, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var row = action.Row; var saved = row.Synced; var path = Row.Path(row);
        var links=new List<SyncedEntry>(2);
        var members=new List<NoticeItem>(2);
        foreach(var sender in new[]{local,dropbox})
        {
            if(row.EntryOn(sender.Side) is not {} original) continue;
            var receiver=sender==local?dropbox:local;
            var mapped=await ItemSync.CounterpartAsync(pair,sender.Side,original.Path,store,ct);
            if(original.Skip!=null || paths.Excludes(sender==local?original.Path:mapped,sender==local?mapped:original.Path,false)) return EffectOutcome.Pending;
            if(await ReadOneAsync(sender,original.Path,ct)!=original) throw new ChangedUnderneathException(original.Path);
            var owner=await store.FindAsync(pair.PairId,receiver.Side,PathNames.KeyFor(receiver.Side,mapped),ct);
            if(owner!=null && owner!=saved) throw new ChangedUnderneathException(mapped);
            var createRow=new Row(row.Key,sender==local?original:null,sender==dropbox?original:null,null,[],null,RowContext.Neutral);
            var create=new SyncAction(sender==local?ActionKind.Upload:ActionKind.Download,createRow,Target:mapped);
            var copied=await ApplyAsync(pair,settings,events,paths,create,local,dropbox,store,populate,compareOldBinding,ct,collectCreate:links.Add);
            if(copied!=EffectOutcome.Applied) return copied;
            members.Add(new(sender.Side,original.Path,original.Id));
        }
        foreach(var member in members)
            if(await ReadOneAsync(member.Side==Side.Local?local:dropbox,member.Path,ct)!=row.EntryOn(member.Side)) throw new ChangedUnderneathException(member.Path);
        var notice=new Notice(0,NoticeKind.Info,action.InfoAfter!.Value,path,new(members,null),DateTimeOffset.UtcNow,false);
        if(links.Count==2) await store.CommitKeepBothAsync(pair.PairId,saved,links[0],links[1],notice,ct);
        else await store.CommitKeepBothAsync(pair.PairId,saved,links.Single(),notice,ct);
        return EffectOutcome.Applied;
    }

    private static async ValueTask<SyncedEntry> RecordAsync(Row row, ISide local, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var saved = row.Synced;
        if (row.Local is not {} pc || row.Dropbox is not {} db) throw new ChangedUnderneathException(Row.Path(row));
        var after = saved != null && (saved.LocalPath != pc.Path || saved.DropboxPath != db.Path) ? SyncedEntries.Relink(saved,pc,db) : SyncedEntries.From(pc,db);
        if (pc.IsFolder && saved == null) await Populate(populate,local,pc,db,ct);
        else if (pc.IsFolder && saved != null && (saved.LocalPath != pc.Path || saved.DropboxPath != db.Path))
            await compareOldBinding(saved,new(saved.LocalPath,saved.DropboxPath),new(pc.Path,db.Path),ct);
        return after;
    }

    private async ValueTask<Step> MoveAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncAction action, Side written, ISide to, Entry? expected, Entry? source, string target, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> held, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough, SyncedEntry? grantedIncumbent, CancellationToken ct)
    {
        var saved = action.Row.Synced;
        if (expected == null || action.Target == null || expected.Skip != null) throw new ChangedUnderneathException(target);
        if (action.Kind==ActionKind.MoveUnlinked)
        {
            var unlinked=await new CaseRename().MoveAsync(to,expected,target,settings.CaseRenameSuffix,null,new(expected.Path,expected.Path),compareOldBinding,ct,events);
            if (unlinked.Outcome==RemovalOutcome.Published) return Stop(EffectOutcome.Applied);
            if (unlinked.Outcome==RemovalOutcome.NoEffect && unlinked.Cause==TextKey.ClashNameTaken)
                throw new ItemFailedException(unlinked.Final?.Path??expected.Path,TextKey.ClashNameTaken,null);
            return Stop(RemovalReceipts.Incomplete(unlinked,unlinked.Outcome==RemovalOutcome.NoEffect?unlinked.Final?.Path??expected.Path:target,written==Side.Dropbox?new(expected.Id,unlinked.Final?.Path??expected.Path,target,PathNames.CaseOnly(expected.Path,target)?PathNames.CaseRenamePath(target,settings.CaseRenameSuffix):null,expected.IsFolder):null));
        }
        var admission=await Collisions.MoveReadyAsync(pair,settings,events,paths,written,expected,target,source,saved,local,dropbox,store,populate,compareOldBinding,ct,grantedIncumbent);
        if (!admission.Ready) return Stop(EffectOutcome.Pending);
        if (admission.Owner!=null) return Stop(await Collisions.BoundCollisionAsync(admission.Owner,expected,source!,saved!,written,target,pair,settings,events,paths,local,dropbox,store,held,populate,compareOldBinding,coversThrough,ct));
        var incumbent = await ReadOneAsync(to,target,ct);
        bool competing = incumbent != null && !(PathNames.Same(incumbent.Path,expected.Path) && incumbent.Id == expected.Id);
        var result = new RemovalResult(RemovalOutcome.NoEffect,null,null);
        if (!competing)
        {
            var before = written == Side.Local ? new Scope(expected.Path,source?.Path ?? saved?.DropboxPath ?? target) : new Scope(source?.Path ?? saved?.LocalPath ?? target,expected.Path);
            result = await new CaseRename().MoveAsync(to,expected,target,settings.CaseRenameSuffix,saved,before,compareOldBinding,ct,events);
            if (result is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.ClashNameTaken or TextKey.FailDropboxTooManyFiles})
            {
                EffectOutcome? renamed;
                (renamed,expected,incumbent,competing) = await RenameRefusedAsync(settings,written,to,expected,target,incumbent,result,ct);
                if (renamed is {} stopped) return Stop(stopped);
            }
        }
        Entry? mergedSource = null;
        if (competing && incumbent is {} atTarget)
        {
            if (!expected.IsFolder || !atTarget.IsFolder) return Stop(await Collisions.CollisionAsync(pair,settings,events,paths,written,expected,atTarget,source,saved,local,dropbox,store,held,populate,compareOldBinding,ct,coversThrough,grantedIncumbent));
            if (await Collisions.MergeAsync(pair,settings,events,paths,written,to,expected,atTarget,source,saved,target,local,dropbox,store,held,populate,compareOldBinding,coversThrough,grantedIncumbent,ct) is {} unmerged) return Stop(unmerged);
            mergedSource = expected;
            result = new(RemovalOutcome.Published,atTarget,null);
        }
        if (result.Outcome != RemovalOutcome.Published) return Stop(RemovalReceipts.Incomplete(result,result.Outcome==RemovalOutcome.NoEffect?result.Final?.Path??expected.Path:target,written==Side.Dropbox?new(expected.Id,result.Final?.Path??expected.Path,target,PathNames.CaseOnly(expected.Path,target) ? PathNames.CaseRenamePath(target,settings.CaseRenameSuffix) : null,expected.IsFolder):null));
        if (result.Final is not {} moved || moved.Path != target || moved.Id != (incumbent?.Id ?? expected.Id)) throw new ChangedUnderneathException(target);
        if (source == null) throw new ChangedUnderneathException(target);
        var after = saved == null ? SyncedEntries.From(written,moved,source) : SyncedEntries.Relink(saved,written == Side.Local ? moved : source,written == Side.Dropbox ? moved : source);
        return (null,after,null,null,null,mergedSource);
    }

    // A refused rename either met a competitor at the target or hit the Dropbox folder-move limit.
    private static async ValueTask<(EffectOutcome? Done, Entry Expected, Entry? Incumbent, bool Competing)> RenameRefusedAsync(EngineSettings settings, Side written, ISide to, Entry expected, string target, Entry? incumbent, RemovalResult result, CancellationToken ct)
    {
        var competing = false;
        if (result is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.ClashNameTaken})
        {
            incumbent = await ReadOneAsync(to,target,ct);
            if (incumbent == null || incumbent.Id == expected.Id) throw new ChangedUnderneathException(target){FailedSide=to.Side};
            // If the first case leg succeeded the selected source now lives at the temporary name.
            if (written==Side.Dropbox && PathNames.CaseOnly(expected.Path,target))
            {
                var physical = await to.ReadByIdAsync(expected.Id,ct);
                if (physical is not {InScope:true} at || !PathNames.Same(at.Location.Entry.Path,PathNames.CaseRenamePath(target,settings.CaseRenameSuffix))) throw new ChangedUnderneathException(target);
                expected = at.Location.Entry;
            }
            competing = true;
        }
        if (written==Side.Dropbox && expected.IsFolder && !PathNames.CaseOnly(expected.Path,target) && result is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.FailDropboxTooManyFiles})
        {
            try
            {
                var created=await to.EnsureFolderAsync(target,ct);
                if (created.Outcome!=RemovalOutcome.Published) return (RemovalReceipts.Incomplete(created,target),expected,incumbent,competing);
                if (created.Final is not {IsFolder:true} folder || folder.Path!=target) throw new ChangedUnderneathException(target);
                incumbent=folder;
            }
            catch (PathOccupiedException error) when (RemovalReceipts.AtTarget(error,written,target))
            {
                incumbent=await ReadOneAsync(to,target,ct);
                if (incumbent==null) throw;
            }
            competing=true;
        }
        return (null,expected,incumbent,competing);
    }

    // The selected source must still be there, and an existing target must be at the planned name.
    private static Entry Admitted(Entry? expected, Entry? source, string target) => source == null || source.Skip != null || expected != null && !PathNames.Same(expected.Path,target) ? throw new ChangedUnderneathException(target) : source;

    // Only a published receipt at the target, of the source's kind, binds.
    private static Step Published(Side written, Entry source, string target, RemovalResult result, RemovalSelection? armed, StageProof? proof)
    {
        if (result.Outcome != RemovalOutcome.Published) return Stop(RemovalReceipts.Incomplete(result,target));
        if (result.Final is not {} final || final.Path != target || final.IsFolder != source.IsFolder) throw new ChangedUnderneathException(target);
        return (null,SyncedEntries.From(written,final,source),result.LocalPublished,armed,proof,null);
    }

    private async ValueTask<Step> TransferFolderAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncAction action, Side written, ISide to, ISide from, Entry? expected, Entry source, string target, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> held, Func<Scope,IEnumerable<Change>>? heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough, bool userSelected, CancellationToken ct)
    {
        RemovalSelection? armed = null; RemovalResult result;
        try
        {
            if (expected is { IsFolder:false })
            {
                if (action.Kind != ActionKind.ReplaceKind) throw new ChangedUnderneathException(target);
                result = await RemovalReceipts.RemoveAsync(pair,settings,events,paths,action,expected,written,to,source,local,dropbox,store,held,heldInput,populate,compareOldBinding,coversThrough,userSelected,selection=>armed=selection,ct);
                if (result.Outcome != RemovalOutcome.Removed) return Stop(RemovalReceipts.Incomplete(result,target));
                result = await to.CreateFolderExclusiveAsync(target,ct);
            }
            else result = await to.EnsureFolderAsync(target,ct);
        }
        catch (PathOccupiedException ex) when (RemovalReceipts.AtTarget(ex,written,target))
        {
            var present = await ReadOneAsync(to,target,ct);
            if (present==null || present.Skip!=null) throw;
            if (!KeepBoth.SameObservation(from.Side,source,await ReadOneAsync(from,source.Path,ct))) throw new ChangedUnderneathException(source.Path){FailedSide=from.Side};
            if (!present.IsFolder)
            {
                var conflict = written == Side.Local ? action.Row with {Local=present} : action.Row with {Dropbox=present};
                var settled=await new KeepBoth().ApplyAsync(pair,settings,events,paths,conflict,local,dropbox,store,populate,ct);
                if (settled==EffectOutcome.Applied && armed!=null) await store.DisarmAsync(pair.PairId,armed,null,ct);
                return Stop(settled);
            }
            result = new(RemovalOutcome.Published,present,null);
        }
        var step = Published(written,source,target,result,armed,null);
        if (step.Done == null && action.Row.Synced?.IsFolder != true) await Populate(populate,from,source,result.Final!,ct);
        return step;
    }

    private async ValueTask<Step> TransferFileAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncAction action, Side written, ISide to, ISide from, Entry? expected, Entry source, string target, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> held, Func<Scope,IEnumerable<Change>>? heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough, Action<Notice>? failedNotice, bool userSelected, Action<SyncedEntry>? collectCreate, CancellationToken ct)
    {
        var row = action.Row; RemovalSelection? armed = null; StageProof? proof = null; RemovalResult result;
        if (expected is {IsFolder:true} && (action.Kind != ActionKind.ReplaceKind || row.Context.ProtectedContents == true || row.Context.UnreadableCause != null))
        {
            await Raise(pair,store,events,action,failedNotice,NoticeKind.Failure,row.Context.UnreadableCause ?? TextKey.FailReplacementRetained,ct);
            return Stop(EffectOutcome.Refused);
        }
        await using var bytes = await OpenReadAsync(from,source,ct);
        var edition=from.Side==Side.Local && row.LocalHash is {} digest && source.ContentHash!=digest ? source with {ContentHash=digest} : source;
        await using var stage = await to.StageAsync(target,bytes,edition,expected,ct);
        if (expected is {IsFolder:true})
        {
            if (stage.ConsumesSourceOnPublish) throw new ChangedUnderneathException(target);
            if (await ReadOneAsync(from,source.Path,ct) != source) throw new ChangedUnderneathException(source.Path){FailedSide=from.Side};
            result = await RemovalReceipts.RemoveAsync(pair,settings,events,paths,action,expected,written,to,source,local,dropbox,store,held,heldInput,populate,compareOldBinding,coversThrough,userSelected,selection=>armed=selection,ct);
            if (result.Outcome != RemovalOutcome.Removed) return Stop(RemovalReceipts.Incomplete(result,target));
            if (await ReadOneAsync(from,source.Path,ct) != source) throw new ChangedUnderneathException(source.Path){FailedSide=from.Side};
            expected = null;
        }
        if (to.Side==Side.Local && expected!=null && !userSelected && (coversThrough==null || heldInput==null)) return Stop(EffectOutcome.Pending);
        if (expected!=null)
        {
            armed=await RemovalReceipts.SelectAsync(pair,to,expected,row.Synced,row.LocalHash,ct);
            result=await to.PublishAsync(stage,expected,armed,ct);
            proof=stage.Proof;
            result=await RemovalReceipts.CheckReceiptAsync(pair,new(expected.Path,source.Path),armed,proof,result,source,action.Kind,local,dropbox,store,held,populate,
                (scope,side,version,token)=>RecoverConflictAsync(pair,settings,events,paths,scope,side,version,local,dropbox,store,populate,compareOldBinding,token),coversThrough,ct,userSelected);
            return Published(written,source,target,result,armed,proof);
        }
        EffectOutcome? clash = null; RemovalResult? published;
        try { published = await to.PublishAsync(stage,null,null,ct); }
        catch (PathOccupiedException ex) when (RemovalReceipts.AtTarget(ex,written,target))
        {
            (clash,published) = await Collisions.OccupiedAsync(pair,settings,events,paths,row,written,to,from,source,target,local,dropbox,store,populate,collectCreate,ct);
            if (clash == null && published == null) throw;
        }
        return clash is {} stopped ? Stop(stopped) : Published(written,source,target,published!,armed,proof);
    }

    // The non-capturing fallback is cached by the compiler, so no path allocates a held-input delegate.
    private static Func<Scope,IEnumerable<Change>> Held(Func<Scope,IEnumerable<Change>>? heldInput) => heldInput ?? (_ => []);

    private static Task Populate(Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, ISide side, Entry item, Entry dest, CancellationToken ct) => populate(side.Side == Side.Local ? new(item.Path,dest.Path) : new(dest.Path,item.Path),side.Side,new(item,side.ReadAllAsync(item.Path,new(true,false),ct)),ct);

    private static async Task Raise(PairConfig pair, IStore store, Action<RunEvent> events, SyncAction action, Action<Notice>? failedNotice, NoticeKind kind, TextKey cause, CancellationToken ct)
    {
        var row = action.Row; var path = Row.Path(row);
        List<NoticeItem> items = [];
        if (row.Local is {} a) items.Add(new(Side.Local,a.Path,a.Id));
        if (row.Dropbox is {} b) items.Add(new(Side.Dropbox,b.Path,b.Id));
        bool plannedFailure=action.Kind==ActionKind.Notice && kind==NoticeKind.Failure;
        var notice=new Notice(0,kind,cause,path,new(items,action.Side,plannedFailure?row.Context.FirstRunDeletion:IssueSubject.FirstRunDeletionKind.None),DateTimeOffset.UtcNow,false);
        await store.SaveNoticeAsync(pair.PairId,notice,ct);
        if (plannedFailure)
        {
            failedNotice?.Invoke(notice);
            events(new(RunEventKind.Failed,kind.ToString(),path,cause.ToString(),kind.ToString()){Cause=cause});
        }
    }

    /// <summary>BC5 restores and checkpoints the selected version; decide only the live pair it hands back.</summary>
    public async Task<EffectOutcome> RecoverConflictAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Scope scope,Side side,Entry version,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct)
    {
        var pc=await ReadOneAsync(local,scope.LocalPath,ct); var db=await ReadOneAsync(dropbox,scope.DropboxPath,ct);
        if (pc==null || db==null || pc.Skip!=null || db.Skip!=null || paths.Excludes(scope.LocalPath,scope.DropboxPath,pc.IsFolder||db.IsFolder)) return EffectOutcome.Pending;
        if (version.IsFolder || !ContentHash.IsValid(version.ContentHash)) return EffectOutcome.Pending;
        var hash=pc.IsFolder?null:await local.ContentHashAsync(pc,ct);
        if (!StringComparer.OrdinalIgnoreCase.Equals(hash,version.ContentHash) && (db.IsFolder || !StringComparer.OrdinalIgnoreCase.Equals(db.ContentHash,version.ContentHash))) return EffectOutcome.Pending;
        var saved=await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,side==Side.Local?scope.LocalPath:scope.DropboxPath),ct);
        var row=new Row(PathNames.Key(scope.LocalPath),pc,db,saved,[],hash,RowContext.Neutral);
        var kind=hash!=null && !db.IsFolder && StringComparer.OrdinalIgnoreCase.Equals(hash,db.ContentHash)?ActionKind.Record:ActionKind.KeepBoth;
        return await ApplyAsync(pair,settings,events,paths,new(kind,row),local,dropbox,store,populate,compareOldBinding,ct);
    }

    internal static async Task<Entry?> ReadOneAsync(ISide endpoint,string path,CancellationToken ct)
    {
        try { return await endpoint.ReadOneAsync(path,ct); }
        catch (ChangedUnderneathException error) { error.FailedSide=endpoint.Side; throw; }
    }

    internal static async Task<Stream> OpenReadAsync(ISide endpoint,Entry source,CancellationToken ct)
    {
        try { return await endpoint.OpenReadAsync(source,ct); }
        catch (ChangedUnderneathException error) { error.FailedSide=endpoint.Side; throw; }
    }
}
