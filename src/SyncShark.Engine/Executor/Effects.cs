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
                break;
            case ActionKind.KeepBoth: return await new KeepBoth().ApplyAsync(pair,settings,events,paths,row,local,dropbox,store,populate,compareOldBinding,ct,action.Side);
            case ActionKind.DeleteLocal: case ActionKind.DeleteDropbox:
                result = await Remove(expected ?? throw new ChangedUnderneathException(target));
                if (result.Outcome != RemovalOutcome.Removed) return Incomplete(result);
                break;
            case ActionKind.MoveLocal: case ActionKind.MoveDropbox: case ActionKind.MoveUnlinked:
                if (expected == null || action.Target == null || expected.Skip != null) throw new ChangedUnderneathException(target);
                var incumbent = await to.ReadOneAsync(target,ct);
                if (incumbent != null && !(PathNames.Same(incumbent.Path,expected.Path) && incumbent.Id == expected.Id))
                {
                    if (!expected.IsFolder || !incumbent.IsFolder) return await CollisionAsync(pair,settings,events,paths,written,expected,incumbent,source,saved,local,dropbox,store,Held,populate,compareOldBinding,ct);
                    result = await new FolderMerge().ApplyAsync(pair,settings,events,paths,expected,incumbent,written,local,dropbox,store,Held,populate,compareOldBinding,ct);
                    if (result.Outcome != RemovalOutcome.Removed) return Incomplete(result);
                    if (await to.ReadOneAsync(target,ct) != incumbent) throw new ChangedUnderneathException(target);
                    result = new(RemovalOutcome.Published,incumbent,null);
                }
                else if (saved != null)
                {
                    var before = written == Side.Local ? new Scope(expected.Path,source?.Path ?? saved.DropboxPath) : new Scope(source?.Path ?? saved.LocalPath,expected.Path);
                    result = await new CaseRename().MoveAsync(to,expected,target,settings.CaseRenameSuffix,saved,before,compareOldBinding,ct,events);
                }
                else result = await to.MoveAsync(expected,target,ct);
                if (result.Outcome != RemovalOutcome.Published) return Incomplete(result);
                if (result.Final is not {} moved || moved.Path != target || moved.Id != (incumbent?.Id ?? expected.Id)) throw new ChangedUnderneathException(target);
                if (action.Kind == ActionKind.MoveUnlinked) return EffectOutcome.Applied;
                if (source == null) throw new ChangedUnderneathException(target);
                after = saved == null ? SyncedEntries.From(written,moved,source) : SyncedEntries.Relink(saved,written == Side.Local ? moved : source,written == Side.Dropbox ? moved : source);
                break;
            case ActionKind.Upload: case ActionKind.Download: case ActionKind.ReplaceKind: case ActionKind.EnsureLocalFolder: case ActionKind.EnsureDropboxFolder:
                if (source == null || source.Skip != null || expected != null && !PathNames.Same(expected.Path,target)) throw new ChangedUnderneathException(target);
                if (source.IsFolder)
                {
                    if (expected is { IsFolder:false })
                    {
                        if (action.Kind != ActionKind.ReplaceKind) throw new ChangedUnderneathException(target);
                        result = await Remove(expected);
                        if (result.Outcome != RemovalOutcome.Removed) return Incomplete(result);
                        result = await to.CreateFolderExclusiveAsync(target,ct);
                    }
                    else result = await to.EnsureFolderAsync(target,ct);
                }
                else
                {
                    if (expected is {IsFolder:true} && (action.Kind != ActionKind.ReplaceKind || row.Context.ProtectedContents == true || row.Context.UnreadableCause != null)) return EffectOutcome.Refused;
                    await using var bytes = await from.OpenReadAsync(source,ct);
                    await using var stage = await to.StageAsync(target,bytes,source,expected,ct);
                    if (expected is {IsFolder:true})
                    {
                        if (stage.ConsumesSourceOnPublish) throw new ChangedUnderneathException(target);
                        result = await Remove(expected);
                        if (result.Outcome != RemovalOutcome.Removed) return Incomplete(result);
                        expected = null;
                    }
                    var selection = expected == null ? null : await SelectAsync(pair,to,expected,saved,row.LocalHash,ct);
                    result = await to.PublishAsync(stage,expected,selection,ct);
                    if (selection != null) { armed = selection; proof = stage.Proof; }
                }
                if (result.Outcome != RemovalOutcome.Published) return Incomplete(result);
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
            if (victim.IsFolder) return await new FolderDelete().ApplyAsync(pair,settings,events,paths,victim,saved,written,action.Kind,local,dropbox,store,Held,populate,compareOldBinding,ct);
            armed = await SelectAsync(pair,to,victim,saved,row.LocalHash,ct);
            return await to.DeleteAsync(victim,armed,ct);
        }
        Task Raise(NoticeKind kind, TextKey cause)
        {
            List<NoticeItem> items = [];
            if (row.Local is {} a) items.Add(new(Side.Local,a.Path,a.Id));
            if (row.Dropbox is {} b) items.Add(new(Side.Dropbox,b.Path,b.Id));
            return store.SaveNoticeAsync(pair.PairId,new(0,kind,cause,path,new(items,action.Side),DateTimeOffset.UtcNow,false),ct);
        }
    }

    internal static EffectOutcome Incomplete(RemovalResult result) => result.Outcome == RemovalOutcome.Pending ? EffectOutcome.Pending : EffectOutcome.Refused;

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
        if (counterpart == null || counterpart.Skip != null || selected.Skip != null || incumbent.Skip != null || selected.Id == incumbent.Id || selected.IsFolder != counterpart.IsFolder || await to.ReadOneAsync(selected.Path,ct) != selected || await to.ReadOneAsync(incumbent.Path,ct) != incumbent || await from.ReadOneAsync(counterpart.Path,ct) != counterpart) return EffectOutcome.Refused;
        if (selected.IsFolder || await to.ContentHashAsync(selected,ct) != await from.ContentHashAsync(counterpart,ct)) return EffectOutcome.Refused;
        var row = new Row(PathNames.Key(incumbent.Path),written == Side.Local ? incumbent : counterpart,written == Side.Dropbox ? incumbent : counterpart,saved,[],null,RowContext.Neutral);
        bool equal = !incumbent.IsFolder && await to.ContentHashAsync(incumbent,ct) == await from.ContentHashAsync(counterpart,ct);
        if (!equal)
        {
            var kept = await new KeepBoth().ApplyAsync(pair,settings,events,paths,row,local,dropbox,store,populate,compareOldBinding,ct);
            if (kept != EffectOutcome.Applied) return kept;
        }
        if (await to.ReadOneAsync(selected.Path,ct) != selected || await from.ReadOneAsync(counterpart.Path,ct) != counterpart || await to.ReadOneAsync(incumbent.Path,ct) != incumbent) throw new ChangedUnderneathException(selected.Path);
        var removal = await to.DeleteAsync(selected,await SelectAsync(pair,to,selected,saved,null,ct),ct);
        if (removal.Outcome != RemovalOutcome.Removed) return Incomplete(removal);
        if (equal) await store.CommitAsync(pair.PairId,saved,SyncedEntries.From(written,incumbent,counterpart),null,null,ct);
        return EffectOutcome.Applied;
    }
}

/// <summary>Only an applied effect permits its caller to settle a notice or remove a source.</summary>
public enum EffectOutcome { Applied, Pending, Refused }
