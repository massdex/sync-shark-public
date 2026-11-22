using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Apply selected effects; never settle a binding from acceptance or an uncertain receipt.</summary>
public sealed class Effects
{
    /// <summary>ItemSync/Retries own failure turnover. Dispatcher saves accepted jobs and signals the per-pair held-news checker.</summary>
    public async Task<EffectOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncAction action, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput = null)
    {
        ct.ThrowIfCancellationRequested();
        var row = action.Row; var saved = row.Synced; var path = Row.Path(row);
        SyncedEntry? after = null; JournalPosition? boundary = null;
        RemovalSelection? armed = null; StageProof? proof = null;
        var written = action.Kind is ActionKind.None or ActionKind.Record or ActionKind.ForgetOnly or ActionKind.Notice or ActionKind.KeepBoth ? Side.Local : action.WrittenSide();
        var to = written == Side.Local ? local : dropbox; var from = written == Side.Local ? dropbox : local;
        var expected = row.EntryOn(written); var source = row.EntryOn(from.Side);
        var target = action.Target ?? expected?.Path ?? row.Context.Destination ?? saved?.PathOn(written) ?? PathNames.SpellingOn(written,source?.Path ?? path);
        RemovalResult result;
        switch (action.Kind)
        {
            case ActionKind.None: return action.IsInSync() ? EffectOutcome.Applied : EffectOutcome.Pending;
            case ActionKind.Notice: await Raise(action.NoticeKind,action.Cause); return EffectOutcome.Refused;
            case ActionKind.ForgetOnly: break;
            case ActionKind.Record:
                if (row.Local is not {} pc || row.Dropbox is not {} db) throw new ChangedUnderneathException(path);
                after = saved != null && (saved.LocalPath != pc.Path || saved.DropboxPath != db.Path) ? SyncedEntries.Relink(saved,pc,db) : SyncedEntries.From(pc,db);
                if (pc.IsFolder && saved == null) await Populate(local,pc,db);
                else if (pc.IsFolder && saved != null && (saved.LocalPath != pc.Path || saved.DropboxPath != db.Path))
                    await compareOldBinding(saved,new(saved.LocalPath,saved.DropboxPath),new(pc.Path,db.Path),ct);
                break;
            case ActionKind.KeepBoth: return await new KeepBoth().ApplyAsync(pair,settings,events,paths,row,local,dropbox,store,populate,ct,action.Side);
            case ActionKind.DeleteLocal: case ActionKind.DeleteDropbox:
                result = await Remove(expected ?? throw new ChangedUnderneathException(target));
                if (result.Outcome != RemovalOutcome.Removed) return Incomplete(result,target);
                break;
            case ActionKind.MoveLocal: case ActionKind.MoveDropbox: case ActionKind.MoveUnlinked:
                if (expected == null || action.Target == null || expected.Skip != null) throw new ChangedUnderneathException(target);
                var incumbent = await to.ReadOneAsync(target,ct);
                bool competing = incumbent != null && !(PathNames.Same(incumbent.Path,expected.Path) && incumbent.Id == expected.Id);
                result = new(RemovalOutcome.NoEffect,null,null);
                if (!competing)
                {
                    var before = written == Side.Local ? new Scope(expected.Path,source?.Path ?? saved?.DropboxPath ?? target) : new Scope(source?.Path ?? saved?.LocalPath ?? target,expected.Path);
                    try { result = await new CaseRename().MoveAsync(to,expected,target,settings.CaseRenameSuffix,saved,before,compareOldBinding,ct,events); }
                    catch (PathOccupiedException ex) when (AtTarget(ex,written,target))
                    {
                        incumbent = await to.ReadOneAsync(target,ct);
                        if (incumbent == null || incumbent.Id == expected.Id) throw;
                        // If the first case leg succeeded the selected source now lives at the temporary name.
                        if (written==Side.Dropbox && PathNames.CaseOnly(expected.Path,target))
                        {
                            var physical = await to.ReadByIdAsync(expected.Id,ct);
                            if (physical is not {InScope:true} at || !PathNames.Same(at.Location.Entry.Path,PathNames.CaseRenamePath(target,settings.CaseRenameSuffix))) throw;
                            expected = at.Location.Entry;
                        }
                        competing = true;
                    }
                    catch (ItemFailedException ex) when (written==Side.Dropbox && expected.IsFolder && ex.Disposition==FailureDisposition.Permanent && ex.Cause==TextKey.FailDropboxTooManyFiles && PathNames.Same(ex.Path,expected.Path) && !PathNames.CaseOnly(expected.Path,target))
                    { result = new(RemovalOutcome.NoEffect,null,ex.Cause); }
                    if (written==Side.Dropbox && expected.IsFolder && !PathNames.CaseOnly(expected.Path,target) && result is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.FailDropboxTooManyFiles})
                    {
                        var created = await to.EnsureFolderAsync(target,ct);
                        if (created.Outcome != RemovalOutcome.Published) return Incomplete(created,target);
                        if (created.Final is not {IsFolder:true} folder || folder.Path!=target) throw new ChangedUnderneathException(target);
                        incumbent = folder; competing = true;
                    }
                }
                if (competing && incumbent is {} atTarget)
                {
                    if (!expected.IsFolder || !atTarget.IsFolder) return await CollisionAsync(pair,settings,events,paths,written,expected,atTarget,source,saved,local,dropbox,store,Held,populate,compareOldBinding,ct);
                    result = await new FolderMerge().ApplyAsync(pair,settings,events,paths,expected,atTarget,written,local,dropbox,store,Held,populate,compareOldBinding,ct,
                        collide:(child,occupant,peer,old,token)=>CollisionAsync(pair,settings,events,paths,written,child,occupant,peer,old,local,dropbox,store,Held,populate,compareOldBinding,token));
                    if (result.Outcome != RemovalOutcome.Removed) return Incomplete(result,target);
                    if (!KeepBoth.SameObservation(written,atTarget,await to.ReadOneAsync(target,ct))) throw new ChangedUnderneathException(target);
                    result = new(RemovalOutcome.Published,atTarget,null);
                }
                if (result.Outcome != RemovalOutcome.Published) return Incomplete(result,target,new(expected.Id,expected.Path,target,PathNames.CaseOnly(expected.Path,target) ? PathNames.CaseRenamePath(target,settings.CaseRenameSuffix) : null,expected.IsFolder));
                if (result.Final is not {} moved || moved.Path != target || moved.Id != (incumbent?.Id ?? expected.Id)) throw new ChangedUnderneathException(target);
                if (action.Kind == ActionKind.MoveUnlinked) return EffectOutcome.Applied;
                if (source == null) throw new ChangedUnderneathException(target);
                after = saved == null ? SyncedEntries.From(written,moved,source) : SyncedEntries.Relink(saved,written == Side.Local ? moved : source,written == Side.Dropbox ? moved : source);
                break;
            case ActionKind.Upload: case ActionKind.Download: case ActionKind.ReplaceKind: case ActionKind.EnsureLocalFolder: case ActionKind.EnsureDropboxFolder:
                if (source == null || source.Skip != null || expected != null && !PathNames.Same(expected.Path,target)) throw new ChangedUnderneathException(target);
                if (source.IsFolder)
                {
                    try
                    {
                        if (expected is { IsFolder:false })
                        {
                            if (action.Kind != ActionKind.ReplaceKind) throw new ChangedUnderneathException(target);
                            result = await Remove(expected);
                            if (result.Outcome != RemovalOutcome.Removed) return Incomplete(result,target);
                            result = await to.CreateFolderExclusiveAsync(target,ct);
                        }
                        else result = await to.EnsureFolderAsync(target,ct);
                    }
                    catch (PathOccupiedException ex) when (AtTarget(ex,written,target))
                    {
                        var present = await to.ReadOneAsync(target,ct);
                        if (present==null || present.Skip!=null || !KeepBoth.SameObservation(from.Side,source,await from.ReadOneAsync(source.Path,ct))) throw;
                        if (!present.IsFolder)
                        {
                            var conflict = written == Side.Local ? row with {Local=present} : row with {Dropbox=present};
                            return await new KeepBoth().ApplyAsync(pair,settings,events,paths,conflict,local,dropbox,store,populate,ct);
                        }
                        result = new(RemovalOutcome.Published,present,null);
                    }
                }
                else
                {
                    if (expected is {IsFolder:true} && (action.Kind != ActionKind.ReplaceKind || row.Context.ProtectedContents == true || row.Context.UnreadableCause != null))
                    {
                        await Raise(NoticeKind.Failure,row.Context.UnreadableCause ?? TextKey.FailReplacementRetained);
                        return EffectOutcome.Refused;
                    }
                    await using var bytes = await from.OpenReadAsync(source,ct);
                    await using var stage = await to.StageAsync(target,bytes,source,expected,ct);
                    if (expected is {IsFolder:true})
                    {
                        if (stage.ConsumesSourceOnPublish) throw new ChangedUnderneathException(target);
                        if (await from.ReadOneAsync(source.Path,ct) != source) throw new ChangedUnderneathException(source.Path);
                        result = await Remove(expected);
                        if (result.Outcome != RemovalOutcome.Removed) return Incomplete(result,target);
                        if (await from.ReadOneAsync(source.Path,ct) != source || await to.ReadOneAsync(target,ct) != null) throw new ChangedUnderneathException(target);
                        expected = null;
                    }
                    var selection = expected == null ? null : await SelectAsync(pair,to,expected,saved,row.LocalHash,ct);
                    try { result = await to.PublishAsync(stage,expected,selection,ct); }
                    catch (PathOccupiedException ex) when (expected == null && AtTarget(ex,written,target))
                    {
                        var competitor = await to.ReadOneAsync(target,ct);
                        if (competitor == null || competitor.Skip != null || await from.ReadOneAsync(source.Path,ct) != source) throw;
                        if (competitor.IsFolder || await to.ContentHashAsync(competitor,ct) != await from.ContentHashAsync(source,ct))
                        {
                            var conflict = written == Side.Local ? row with {Local=competitor} : row with {Dropbox=competitor};
                            return await new KeepBoth().ApplyAsync(pair,settings,events,paths,conflict,local,dropbox,store,populate,ct);
                        }
                        result = new(RemovalOutcome.Published,competitor,null);
                    }
                    if (selection != null)
                    {
                        armed = selection; proof = stage.Proof;
                        result = await CheckReceiptAsync(pair,new(expected!.Path,source.Path),selection,result,local,dropbox,store,populate,ct);
                    }
                }
                if (result.Outcome != RemovalOutcome.Published) return Incomplete(result,target);
                if (result.Final is not {} final || final.Path != target || final.IsFolder != source.IsFolder) throw new ChangedUnderneathException(target);
                after = SyncedEntries.From(written,final,source); boundary = result.LocalPublished;
                if (source.IsFolder && saved?.IsFolder != true) await Populate(from,source,final);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
        if (after is {IsFolder:true} && saved is {IsFolder:true} && (saved.LocalPath != after.LocalPath || saved.DropboxPath != after.DropboxPath))
            await store.ProjectSubtreeAsync(pair.PairId,new(saved.LocalPath,saved.DropboxPath),new(after.LocalPath,after.DropboxPath),ct);
        var spent = row.Deletion is DeletionEvidence.DeletedFile or DeletionEvidence.DeletedPath ? PathNames.DropboxKey(row.Deletion.Path) : null;
        await store.CommitAsync(pair.PairId,saved,after,boundary,spent,ct);
        if (action.InfoAfter is {} info) await Raise(NoticeKind.Info,info);
        if (armed != null && await store.ArmedAsync(pair.PairId,ct) is {} note && note.Victim == armed && note.Stage == proof) await store.DisarmAsync(pair.PairId,note.Victim,note.Stage,ct);
        events(new(RunEventKind.Effect,action.Kind.ToString(),path,string.Empty,nameof(EffectOutcome.Applied)) { Action = action.Kind, Side = action.Side });
        return EffectOutcome.Applied;

        IEnumerable<Change> Held(Scope scope) => heldInput?.Invoke(scope) ?? [];
        Task Populate(ISide side, Entry item, Entry dest) => populate(side.Side == Side.Local ? new(item.Path,dest.Path) : new(dest.Path,item.Path),side.Side,new(item,side.ReadAllAsync(item.Path,new(true,false),ct)),ct);
        async Task<RemovalResult> Remove(Entry victim)
        {
            if (victim.IsFolder) return await new FolderDelete().ApplyAsync(pair,settings,events,paths,victim,saved,written,action.Kind,local,dropbox,store,Held,populate,compareOldBinding,ct,covered:row,
                resolveConflict:(scope,side,version,token)=>RecoverConflictAsync(pair,settings,events,paths,scope,side,version,local,dropbox,store,populate,compareOldBinding,token));
            armed = await SelectAsync(pair,to,victim,saved,row.LocalHash,ct);
            var removed = await to.DeleteAsync(victim,armed,ct);
            return await CheckReceiptAsync(pair,new(victim.Path,saved?.DropboxPath ?? source?.Path ?? victim.Path),armed,removed,local,dropbox,store,populate,ct);
        }
        Task Raise(NoticeKind kind, TextKey cause)
        {
            List<NoticeItem> items = [];
            if (row.Local is {} a) items.Add(new(Side.Local,a.Path,a.Id));
            if (row.Dropbox is {} b) items.Add(new(Side.Dropbox,b.Path,b.Id));
            return store.SaveNoticeAsync(pair.PairId,new(0,kind,cause,path,new(items,action.Side),DateTimeOffset.UtcNow,false),ct);
        }
    }

    /// <summary>Observe old and mapped endpoints before asking the pure grouped move planner. Hash only on its explicit request.</summary>
    public static async Task<SyncAction> DecideMoveAsync(PairConfig pair, EngineSettings settings, PathScope paths, IReadOnlyList<RenameCandidate> group, ISide local, ISide dropbox, IStore store, bool dirty, CancellationToken ct)
    {
        if (group.Count == 0) throw new ArgumentException(nameof(group));
        var saved = group[0].Synced;
        var pc = group.FirstOrDefault(c => c.Side == Side.Local)?.Moved;
        var db = group.FirstOrDefault(c => c.Side == Side.Dropbox)?.Moved;
        var oldPc = await local.ReadOneAsync(saved.LocalPath,ct);
        var oldDb = await dropbox.ReadOneAsync(saved.DropboxPath,ct);
        RequireReadable(Side.Local,oldPc); RequireReadable(Side.Dropbox,oldDb);
        foreach (var c in group)
        {
            var observed = await (c.Side == Side.Local ? local : dropbox).ReadOneAsync(c.Moved.Path,ct);
            RequireReadable(c.Side,observed);
            if (c.Synced != saved || c.Moved.Id != saved.IdOn(c.Side) || observed != c.Moved) throw new ChangedUnderneathException(c.Moved.Path);
        }
        var pcPath = pc?.Path ?? await MapAsync(pair,store,Side.Local,db!.Path,ct);
        var dbPath = pc != null ? await MapAsync(pair,store,Side.Dropbox,pc.Path,ct) : db!.Path;
        var newPc = pc ?? await local.ReadOneAsync(pcPath,ct);
        var newDb = db ?? await dropbox.ReadOneAsync(dbPath,ct);
        RequireReadable(Side.Local,newPc); RequireReadable(Side.Dropbox,newDb);
        var destination = new Row(PathNames.Key(pcPath),newPc,newDb,saved,[],null,RowContext.Neutral);
        bool held = false;
        var carries = false;
        foreach (var (side,path) in new[]{(Side.Local,saved.LocalPath),(Side.Dropbox,saved.DropboxPath),(Side.Local,pcPath),(Side.Dropbox,dbPath)}.Distinct())
        {
            if (saved.IsFolder && paths.HoldsNestedRoot(side,path)) carries = true;
            await foreach (var n in store.NoticesAtAsync(pair.PairId,PathNames.KeyFor(side,path),ct))
                if (n.Kind == NoticeKind.Tie) held = true;
        }
        var moved = group[0]; var endpoint = moved.Side == Side.Local ? local : dropbox;
        Moves.DestinationFacts? owner = null;
        var occupant = await store.FindAsync(pair.PairId,moved.Side,PathNames.KeyFor(moved.Side,moved.Moved.Path),ct);
        if (occupant != null && occupant != saved)
        {
            var seen = await endpoint.ReadByIdAsync(occupant.IdOn(moved.Side),ct);
            var identity = seen is null or {InScope:false} ? Moves.DestinationIdentity.Gone : PathNames.Same(seen.Value.Location.Entry.Path,moved.Moved.Path) ? Moves.DestinationIdentity.Here : Moves.DestinationIdentity.Moving;
            owner = new(occupant,moved.Side,identity);
        }
        var skipped = oldPc?.Skip != null || oldDb?.Skip != null || newPc?.Skip != null || newDb?.Skip != null;
        var outside = paths.Excludes(saved.LocalPath,saved.DropboxPath,saved.IsFolder) || paths.Excludes(pcPath,dbPath,saved.IsFolder) || paths.Excludes(pc?.Path,db?.Path,saved.IsFolder);
        // Recover an interrupted two-step from the saved identity, never from the suffix alone.
        if (!skipped && !held && !outside && !carries && db != null && oldDb == null && db.Id == saved.DropboxId && PathNames.Same(db.Path,PathNames.CaseRenamePath(saved.DropboxPath,settings.CaseRenameSuffix)) && (pc ?? oldPc) is {} livePc && livePc.Id == saved.LocalId && (pc == null || PathNames.Same(pc.Path,saved.LocalPath)))
        {
            var goal = await MapAsync(pair,store,Side.Dropbox,livePc.Path,ct);
            return new(ActionKind.MoveDropbox,new Row(saved.LocalKey,livePc,db,saved,[],null,RowContext.Neutral),Target:goal,Side:Side.Dropbox);
        }
        var facts = new Moves.Observations(oldPc?.Id == saved.LocalId ? oldPc : null,oldDb?.Id == saved.DropboxId ? oldDb : null,skipped,held,outside,carries,null,pcPath,dbPath,owner);
        var decision = Moves.For(group,destination,facts,dirty);
        if (decision.Kind == ActionKind.NeedLocalHash && facts.LocalAtSynced is {} file)
            decision = Moves.For(group,destination,facts with {LocalHash=await local.ContentHashAsync(file,ct)},dirty);
        return decision;

        static void RequireReadable(Side side,Entry? entry)
        {
            if (entry is {Skip:nameof(TextKey.SkipUnreadable)}) throw new ItemFailedException(entry.Path,side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed,null);
        }


    }

    private static async Task<string> MapAsync(PairConfig pair,IStore store,Side targetSide,string path,CancellationToken ct)
        {
            var sourceSide = PathNames.Other(targetSide); Dictionary<string,SyncedEntry?> parents = new(StringComparer.Ordinal);
            foreach (var parent in PathNames.Ancestors(path))
            {
                var key = PathNames.KeyFor(sourceSide,parent);
                if ((parents[key] = await store.FindAsync(pair.PairId,sourceSide,key,ct)) != null) break;
            }
            return PathNames.MappedDestination(targetSide,path,parents,static (map,_,key) => map.GetValueOrDefault(key));
        }

    /// <summary>Preserve a competing live recovered version at its mapped endpoints. Missing or unattributable bytes remain pending.</summary>
    public async Task<EffectOutcome> RecoverConflictAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Scope scope,Side side,Entry version,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding,CancellationToken ct)
    {
        var pc = await local.ReadOneAsync(scope.LocalPath,ct);
        var db = await dropbox.ReadOneAsync(scope.DropboxPath,ct);
        if (pc?.Skip != null || db?.Skip != null || paths.Excludes(scope.LocalPath,scope.DropboxPath,version.IsFolder)) return EffectOutcome.Pending;
        var adapter=side==Side.Local?local:dropbox; var other=side==Side.Local?dropbox:local;
        var original=side==Side.Local?pc:db; var peer=side==Side.Local?db:pc;
        var originalPath=side==Side.Local?scope.LocalPath:scope.DropboxPath;
        var peerPath=side==Side.Local?scope.DropboxPath:scope.LocalPath;
        var binding=await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,originalPath),ct);
        Recovery.Folder? recoveredFolder=null;
        if (version.IsFolder || !await Matches(adapter,original) && !await Matches(other,peer))
        {
            var found=await adapter.ReadByIdAsync(version.Id,ct);
            var location=found?.Location;
            if (location==null && side==Side.Dropbox && !version.IsFolder && version.Revision.Length!=0 && pair.Roots?.Dropbox.RootNamespaceId is {Length:>0} ns)
                location=new(ns,PathNames.RootedDropboxPath(pair.DropboxRoot,version.Path),version);
            if (location==null) return EffectOutcome.Pending;
            var recovered=await adapter.RecoverAsync(location,version,ct);
            if (recovered is Recovery.Folder folder && folder.Root.Id==version.Id && folder.Root.IsFolder) recoveredFolder=folder;
            else if (recovered is Recovery.File file)
            {
                await using var bytes=file.Bytes;
                if (file.Version!=version || !ContentHash.IsValid(version.ContentHash)) return EffectOutcome.Pending;
                // If both names are occupied retain both; reserve a new recovery label, never overwrite either name.
                var dest=original==null?adapter:peer==null?other:adapter;
                var at=dest==adapter?originalPath:peerPath;
                if (original!=null && peer!=null) at=await KeepBoth.FreeLabelAsync(dest,at,side,version,settings,ct);
                await using var stage=await dest.StageAsync(at,bytes,version,null,ct);
                var published=await dest.PublishAsync(stage,null,null,ct);
                if (published.Outcome!=RemovalOutcome.Published) return Incomplete(published,at);
                if (published.Final is not {} fresh || fresh.Path!=at || fresh.IsFolder || fresh.Size!=version.Size || !StringComparer.OrdinalIgnoreCase.Equals(fresh.ContentHash,version.ContentHash)) throw new ChangedUnderneathException(at);
                if (dest.Side==Side.Local) pc=fresh; else db=fresh;
            }
            else return EffectOutcome.Pending;
        }
        if (recoveredFolder!=null)
        {
            // A restored folder's root is not proof of its children. Run M4 with the attributable listing.
            var at=peerPath;
            if (peer is {IsFolder:false}) at=await KeepBoth.FreeLabelAsync(other,at,side,version,settings,ct);
            var dest=peer is {IsFolder:true} ? new RemovalResult(RemovalOutcome.Published,peer,null) : await other.CreateFolderExclusiveAsync(at,ct);
            if (dest.Outcome!=RemovalOutcome.Published) return Incomplete(dest,at);
            if (dest.Final is not {IsFolder:true} placed || placed.Path!=at) throw new ChangedUnderneathException(at);
            var populated=side==Side.Local?scope with {DropboxPath=at}:scope with {LocalPath=at};
            await populate(populated,side,recoveredFolder,ct);
            if (side==Side.Local) db=placed; else pc=placed;
        }
        if (pc==null && db==null) return EffectOutcome.Pending;
        var row=new Row(PathNames.Key(scope.LocalPath),pc,db,binding,[],null,RowContext.Neutral);
        if (pc==null || db==null)
        {
            var present=pc??db!; var missing=pc==null?Side.Local:Side.Dropbox;
            return await ApplyAsync(pair,settings,events,paths,new(SyncAction.CopyOn(present.IsFolder,missing),row,Target:missing==Side.Local?scope.LocalPath:scope.DropboxPath),local,dropbox,store,populate,compareOldBinding,ct);
        }
        if (pc.IsFolder && db.IsFolder && recoveredFolder!=null || !pc.IsFolder && !db.IsFolder && ContentHash.IsValid(db.ContentHash) && StringComparer.OrdinalIgnoreCase.Equals(await local.ContentHashAsync(pc,ct),db.ContentHash))
            return await ApplyAsync(pair,settings,events,paths,new(ActionKind.Record,row),local,dropbox,store,populate,compareOldBinding,ct);
        return await new KeepBoth().ApplyAsync(pair,settings,events,paths,row,local,dropbox,store,populate,ct);

        async Task<bool> Matches(ISide endpoint,Entry? observed)
        {
            if (observed==null || observed.IsFolder || observed.Size!=version.Size || !ContentHash.IsValid(version.ContentHash)) return false;
            bool attributable=endpoint.Side==side && observed.Id==version.Id;
            if (!attributable)
                await foreach (var notice in store.NoticesAtAsync(pair.PairId,PathNames.Key(originalPath),ct))
                    if (notice.Kind==NoticeKind.PutBack && notice.Subject.Incoming==side && notice.Subject.Items.FirstOrDefault() is {} selected && selected.Id==version.Id && PathNames.Same(selected.Path,version.Path) && notice.Subject.Items.Any(i=>i.Side==endpoint.Side && i.Id==observed.Id && PathNames.Same(i.Path,observed.Path))) { attributable=true; break; }
            return attributable && StringComparer.OrdinalIgnoreCase.Equals(await endpoint.ContentHashAsync(observed,ct),version.ContentHash);
        }
    }

    internal static bool AtTarget(PathOccupiedException error,Side side,string target) => PathNames.Same(error.Path,target) || side==Side.Local && PathNames.Same(error.Path,PathNames.Name(target));

    internal static EffectOutcome Incomplete(RemovalResult result,string path,IssueSubject.UnknownMove? move=null)
    {
        if (result.Outcome == RemovalOutcome.Pending || result.Outcome == RemovalOutcome.Unknown && result.Accepted != null) return EffectOutcome.Pending;
        if (result.Outcome==RemovalOutcome.NoEffect && result.ObservedLocal is {} observed) throw new ChangedUnderneathException(path){ObservedLocal=observed};
        throw new ItemFailedException(path,result.Cause ?? TextKey.FailChanged,null) {Disposition=result.Outcome==RemovalOutcome.Unknown?FailureDisposition.UnknownEffect:FailureDisposition.Permanent, Move=move};
    }

    private static async Task<RemovalResult> CheckReceiptAsync(PairConfig pair,Scope footprint,RemovalSelection? selection,RemovalResult result,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct)
    {
        if (selection==null || result.RecycledVictim is not {} receipt) return result;
        var proof = selection.Proof;
        if (proof.VolumeId==receipt.Domain && proof.Expected.Id==receipt.Entry.Id && proof.Expected.IsFolder==receipt.Entry.IsFolder) return result;
        if (!await FolderDelete.PutBackSubstituteAsync(pair,footprint,receipt,local,dropbox,store,populate,ct))
            throw new ItemFailedException(footprint.LocalPath,TextKey.RetryRecoveryRequired,null);
        // Restoring the substitute says nothing about whether the selected victim was removed.
        return result with { Outcome=RemovalOutcome.Retained, Cause=TextKey.FailChanged };
    }

    private static async Task<RemovalSelection?> SelectAsync(PairConfig pair, ISide side, Entry expected, SyncedEntry? saved, string? hash, CancellationToken ct)
    {
        if (side.Side != Side.Local) return null;
        var found = await side.ReadByIdAsync(expected.Id,ct);
        if (found is not {InScope:true} hit || hit.Location.Entry != expected || string.IsNullOrEmpty(hit.Location.Domain) || pair.Roots != null && pair.Roots.LocalVolumeKey() != hit.Location.Domain) throw new ChangedUnderneathException(expected.Path);
        var digest = expected.IsFolder || expected.Skip == nameof(TextKey.SkipOnlineOnly) ? null : hash ?? await side.ContentHashAsync(expected,ct);
        return new(new(expected,hit.Location.Domain,digest),saved);
    }

    /// <summary>Resolve the selected final-name collision without deleting a third version or a replacement at the obsolete path.</summary>
    public async Task<EffectOutcome> CollisionAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Side written, Entry selected, Entry incumbent, Entry? counterpart, SyncedEntry? saved, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var to = written == Side.Local ? local : dropbox; var from = written == Side.Local ? dropbox : local;
        if (selected.Skip != null || incumbent.Skip != null || selected.Id == incumbent.Id || !KeepBoth.SameObservation(written,selected,await to.ReadOneAsync(selected.Path,ct)) || !KeepBoth.SameObservation(written,incumbent,await to.ReadOneAsync(incumbent.Path,ct))) return EffectOutcome.Refused;
        if (counterpart == null)
        {
            var peerPath = await MapAsync(pair,store,from.Side,incumbent.Path,ct);
            if (paths.Excludes(written==Side.Local?incumbent.Path:peerPath,written==Side.Dropbox?incumbent.Path:peerPath,selected.IsFolder)) return EffectOutcome.Refused;
            counterpart = await from.ReadOneAsync(peerPath,ct);
            if (counterpart==null)
            {
                RemovalResult copy;
                if (selected.IsFolder) copy = await from.CreateFolderExclusiveAsync(peerPath,ct);
                else
                {
                    await using var bytes = await to.OpenReadAsync(selected,ct);
                    await using var staged = await from.StageAsync(peerPath,bytes,selected,null,ct);
                    copy = await from.PublishAsync(staged,null,null,ct);
                }
                if (copy.Outcome!=RemovalOutcome.Published) return Incomplete(copy,peerPath);
                if (copy.Final is not {} fresh || fresh.Path!=peerPath || fresh.IsFolder!=selected.IsFolder) throw new ChangedUnderneathException(peerPath);
                counterpart=fresh;
                if (selected.IsFolder) await populate(written==Side.Local?new(selected.Path,peerPath):new(peerPath,selected.Path),written,new(selected,to.ReadAllAsync(selected.Path,new(true,false),ct)),ct);
            }
        }
        if (counterpart.Skip != null || selected.IsFolder!=counterpart.IsFolder || !KeepBoth.SameObservation(from.Side,counterpart,await from.ReadOneAsync(counterpart.Path,ct))) return EffectOutcome.Refused;
        string? selectedHash = selected.IsFolder ? null : await to.ContentHashAsync(selected,ct);
        if (selectedHash != null && selectedHash != await from.ContentHashAsync(counterpart,ct)) return EffectOutcome.Refused;
        var row = new Row(PathNames.Key(incumbent.Path),written == Side.Local ? incumbent : counterpart,written == Side.Dropbox ? incumbent : counterpart,saved,[],null,RowContext.Neutral);
        bool equal = !incumbent.IsFolder && selectedHash != null && await to.ContentHashAsync(incumbent,ct) == selectedHash;
        RemovalSelection? cleanupSelection = null;
        if (!equal)
        {
            var kept = await new KeepBoth().ApplyAsync(pair,settings,events,paths,row,local,dropbox,store,populate,ct,beforeCommit:Cleanup);
            if (kept == EffectOutcome.Applied) await Disarm();
            return kept;
        }
        var cleaned = await Cleanup(ct);
        if (cleaned != EffectOutcome.Applied) return cleaned;
        await store.CommitAsync(pair.PairId,saved,SyncedEntries.From(written,incumbent,counterpart),null,null,ct);
        await Disarm();
        return EffectOutcome.Applied;

        async Task Disarm()
        {
            if (cleanupSelection != null) await store.DisarmAsync(pair.PairId,cleanupSelection,null,ct);
        }

        async Task<EffectOutcome> Cleanup(CancellationToken token)
        {
            if (!KeepBoth.SameObservation(written,selected,await to.ReadOneAsync(selected.Path,token)) || !KeepBoth.SameObservation(from.Side,counterpart,await from.ReadOneAsync(counterpart.Path,token)) || !KeepBoth.SameObservation(written,incumbent,await to.ReadOneAsync(incumbent.Path,token))) throw new ChangedUnderneathException(selected.Path);
            var removal = selected.IsFolder
                ? await new FolderDelete().ApplyAsync(pair,settings,events,paths,selected,saved,written,written==Side.Local?ActionKind.MoveLocal:ActionKind.MoveDropbox,local,dropbox,store,heldInput,populate,compareOldBinding,token)
                : await to.DeleteAsync(selected,cleanupSelection=await SelectAsync(pair,to,selected,saved,selectedHash,token),token);
            var receipt = await CheckReceiptAsync(pair,new(selected.Path,saved?.DropboxPath ?? counterpart.Path),cleanupSelection,removal,local,dropbox,store,populate,token);
            return receipt.Outcome == RemovalOutcome.Removed ? EffectOutcome.Applied : Incomplete(receipt,selected.Path);
        }
    }
}

/// <summary>Only an applied effect permits its caller to settle a notice or remove a source.</summary>
public enum EffectOutcome { Applied, Pending, Refused }
