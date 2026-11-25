using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Gather the observations the pure grouped move planner needs for one saved binding.</summary>
public static class MoveDecision
{
    /// <summary>Observe old and mapped endpoints before asking the pure grouped move planner. Hash only on its explicit request.</summary>
    public static async Task<SyncAction> DecideMoveAsync(PairConfig pair, EngineSettings settings, PathScope paths, IReadOnlyList<RenameCandidate> group, ISide local, ISide dropbox, IStore store, bool dirty, CancellationToken ct, string? writtenTarget=null)
    {
        if (group.Count == 0) throw new ArgumentException(nameof(group));
        var saved = group[0].Synced;
        var pc = group.FirstOrDefault(c => c.Side == Side.Local)?.Moved;
        var db = group.FirstOrDefault(c => c.Side == Side.Dropbox)?.Moved;
        var oldPc = await Effects.ReadOneAsync(local,saved.LocalPath,ct);
        var oldDb = await Effects.ReadOneAsync(dropbox,saved.DropboxPath,ct);
        ItemFailedException.RequireReadable(Side.Local,oldPc); ItemFailedException.RequireReadable(Side.Dropbox,oldDb);
        foreach (var c in group)
        {
            var observed = await Effects.ReadOneAsync(c.Side==Side.Local?local:dropbox,c.Moved.Path,ct);
            ItemFailedException.RequireReadable(c.Side,observed);
            if (c.Synced != saved || c.Moved.Id != saved.IdOn(c.Side) || observed != c.Moved) throw new ChangedUnderneathException(c.Moved.Path){FailedSide=c.Side};
        }
        (pc,oldDb,group,var pcPath,var dbPath,var destination,var observedDropboxPath) = await ResolveDestinationAsync(pair,settings,paths,group,saved,pc,db,oldDb,local,dropbox,store,writtenTarget,ct);
        var (held,carries,owner) = await ObserveFootprintAsync(pair,paths,group[0],saved,local,dropbox,store,pcPath,dbPath,observedDropboxPath,ct);
        var skipped = oldPc?.Skip != null || oldDb?.Skip != null || destination.Local?.Skip != null || destination.Dropbox?.Skip != null;
        var savedOutside=paths.Excludes(saved.LocalPath,saved.DropboxPath,saved.IsFolder);
        var newOutside=paths.Excludes(pcPath,dbPath,saved.IsFolder) || paths.Excludes(pc?.Path,observedDropboxPath,saved.IsFolder);
        // An already-excluded binding stays out of scope.
        var leaves=!savedOutside && newOutside && group.Count==1;
        if (leaves && !held && !skipped && pc!=null && !saved.IsFolder && oldPc is {IsFolder:false,Skip:null} replacement && replacement.Id!=saved.LocalId)
            return Decide.For(new Row(saved.LocalKey,replacement,oldDb?.Id==saved.DropboxId?oldDb:null,saved,[],null,RowContext.Neutral),pair.FirstRun,DateTimeOffset.UtcNow,true);
        var outside=savedOutside || newOutside && !leaves;
        var facts = new Moves.Observations(oldPc?.Id == saved.LocalId ? oldPc : null,oldDb?.Id == saved.DropboxId ? oldDb : null,skipped,held,outside,carries,null,pcPath,dbPath,owner,leaves);
        var decision = Moves.For(group,destination,facts,dirty);
        if (decision.Kind == ActionKind.NeedLocalHash && facts.LocalAtSynced is {} file)
            decision = Moves.For(group,destination,facts with {LocalHash=await local.ContentHashAsync(file,ct)},dirty);
        return decision;
    }

    private static async Task<(Entry? Pc, Entry? OldDb, IReadOnlyList<RenameCandidate> Group, string PcPath, string DbPath, Row Destination, string? ObservedDropboxPath)> ResolveDestinationAsync(PairConfig pair, EngineSettings settings, PathScope paths, IReadOnlyList<RenameCandidate> group, SyncedEntry saved, Entry? pc, Entry? db, Entry? oldDb, ISide local, ISide dropbox, IStore store, string? writtenTarget, CancellationToken ct)
    {
        var observedDropboxPath=db?.Path;
        // A confirmed case intermediate is the physical counterpart, not an independently chosen destination.
        if (db!=null && oldDb?.Id!=saved.DropboxId && db.Id==saved.DropboxId && PathNames.Same(db.Path,PathNames.CaseRenamePath(saved.DropboxPath,settings.CaseRenameSuffix)))
        {
            var physical=await dropbox.ReadByIdAsync(saved.DropboxId,ct);
            var latest=await local.ReadByIdAsync(saved.LocalId,ct);
            if (physical is not {InScope:true} at || at.Location.Entry!=db) throw new ChangedUnderneathException(db.Path){FailedSide=Side.Dropbox};
            if (latest is {InScope:true} live && live.Location.Entry.Id==saved.LocalId && live.Location.Entry.IsFolder==saved.IsFolder)
            {
                pc=live.Location.Entry; oldDb=db; db=null;
                group=[new(Side.Local,pc,saved)];
            }
        }
        if (pc!=null && db!=null && !saved.IsFolder && paths.Excludes(await ItemSync.CounterpartAsync(pair,Side.Dropbox,db.Path,store,ct),db.Path,false))
        {
            group=[group.First(c=>c.Side==Side.Local)]; db=null; observedDropboxPath=null;
        }
        var pcPath = pc?.Path ?? writtenTarget ?? await ItemSync.CounterpartAsync(pair,Side.Dropbox,db!.Path,store,ct);
        var dbPath = pc != null ? writtenTarget ?? await ItemSync.CounterpartAsync(pair,Side.Local,pc.Path,store,ct) : db!.Path;
        var newPc = pc ?? await Effects.ReadOneAsync(local,pcPath,ct);
        var newDb = db ?? await Effects.ReadOneAsync(dropbox,dbPath,ct);
        ItemFailedException.RequireReadable(Side.Local,newPc); ItemFailedException.RequireReadable(Side.Dropbox,newDb);
        return (pc,oldDb,group,pcPath,dbPath,new Row(PathNames.Key(pcPath),newPc,newDb,saved,[],null,RowContext.Neutral),observedDropboxPath);
    }

    private static async Task<(bool Held, bool Carries, Moves.DestinationFacts? Owner)> ObserveFootprintAsync(PairConfig pair, PathScope paths, RenameCandidate moved, SyncedEntry saved, ISide local, ISide dropbox, IStore store, string pcPath, string dbPath, string? observedDropboxPath, CancellationToken ct)
    {
        bool held = false;
        var carries = false;
        foreach (var (side,path) in new[]{(Side.Local,saved.LocalPath),(Side.Dropbox,saved.DropboxPath),(Side.Local,pcPath),(Side.Dropbox,dbPath),(Side.Dropbox,observedDropboxPath ?? dbPath)}.Distinct())
        {
            if (saved.IsFolder && paths.HoldsNestedRoot(side,path)) carries = true;
            await foreach (var n in store.NoticesAtAsync(pair.PairId,PathNames.KeyFor(side,path),ct))
                if (n.Kind == NoticeKind.Tie) held = true;
        }
        var endpoint = moved.Side == Side.Local ? local : dropbox;
        Moves.DestinationFacts? owner = null;
        foreach (var (adapter,atPath) in new[]{(endpoint,moved.Moved.Path),(moved.Side==Side.Local?dropbox:local,moved.Side==Side.Local?dbPath:pcPath)})
        {
            var occupant=await store.FindAsync(pair.PairId,adapter.Side,PathNames.KeyFor(adapter.Side,atPath),ct);
            if (occupant==null || occupant==saved) continue;
            var seen=await adapter.ReadByIdAsync(occupant.IdOn(adapter.Side),ct);
            var identity=seen is null or {InScope:false}?Moves.DestinationIdentity.Gone:PathNames.Same(seen.Value.Location.Entry.Path,atPath)?Moves.DestinationIdentity.Here:Moves.DestinationIdentity.Moving;
            if (owner==null || identity!=Moves.DestinationIdentity.Here) owner=new(occupant,adapter.Side,identity);
            if (identity==Moves.DestinationIdentity.Moving) break;
        }
        return (held,carries,owner);
    }
}
