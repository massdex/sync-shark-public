namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Remove the selected identity without giving away held child evidence.</summary>
public sealed class FolderDelete
{
    /// <summary>The binding overload receives the side where deletion was observed.</summary>
    public async Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncedEntry folder, Side deleting, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct,Row? covered=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,Func<Scope,bool>? effectHeld=null)
    {
        if(await store.FindAsync(pair.PairId,deleting,folder.KeyOn(deleting),ct)!=folder) return new(RemovalOutcome.Retained,null,TextKey.FailChanged);
        var change=new Change(deleting,ChangeKind.Deleted,folder.PathOn(deleting),null,folder.IdOn(deleting),null,false,deleting==Side.Local);
        bool retained=false;
        try
        {
            var settled=await ItemSync.ChangeAsync(pair,settings,events,paths,change,local,dropbox,store,populate,compareOldBinding,ct,heldInput,(_,reproducible)=>retained=!reproducible,coversThrough:coversThrough,effectHeld:effectHeld);
            return new(settled?RemovalOutcome.Removed:RemovalOutcome.Retained,null,null);
        }
        catch(InventoryIncompleteException e) when(retained && e.Message==nameof(RemovalOutcome))
        {
            // RequireRemovalAsync signals a non-removal to queue callers; this overload returns that disposition.
            return new(RemovalOutcome.Retained,null,null);
        }
    }

    /// <summary>Choose the missing ancestor before the caller acquires its exact footprint.</summary>
    public static async Task<SyncedEntry?> HighestMissingAsync(PairConfig pair,PathScope paths,SyncedEntry folder,ISide source,IStore store,CancellationToken ct)
    {
        var deleting=source.Side;
        if(await source.ReadByIdAsync(folder.IdOn(deleting),ct) is {InScope:true}) return null;
        foreach(var ancestor in PathNames.Ancestors(folder.PathOn(deleting)))
        {
            var parent=await store.FindAsync(pair.PairId,deleting,PathNames.KeyFor(deleting,ancestor),ct);
            if(parent is not {IsFolder:true} || paths.ExcludesBinding(parent)) break;
            if(await source.ReadByIdAsync(parent.IdOn(deleting),ct) is {InScope:true}) break;
            folder=parent;
        }
        return folder;
    }

    /// <summary>Returns the actual removal result. Dispatcher saves Pending; no binding is retired on acceptance.</summary>
    public async Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Entry folder, SyncedEntry? binding, Side deleting, ActionKind origin, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct,bool splitSelected=false,Row? covered=null,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,bool userSelected=false,Func<Scope,bool>? effectHeld=null)
    {
        var target=deleting==Side.Local?local:dropbox; var peer=deleting==Side.Local?dropbox:local;
        var scope=new Scope(binding?.LocalPath??PathNames.LocalSpelling(folder.Path),binding?.DropboxPath??folder.Path);
        if(splitSelected && (deleting!=Side.Dropbox || origin!=ActionKind.DeleteDropbox || coversThrough==null)) return new(RemovalOutcome.Retained,folder,TextKey.FailChanged);
        if(!folder.IsFolder || folder.Path.Length==0 || binding!=null && (binding.IdOn(deleting)!=folder.Id || !PathNames.Same(binding.PathOn(deleting),folder.Path))) return new(RemovalOutcome.Unknown,folder,TextKey.FailChanged);
        return await new FolderRemoval(pair,paths,folder,binding,deleting,origin,local,dropbox,target,peer,scope,store,heldInput,populate,compareOldBinding,ct,splitSelected,covered,resolveConflict,coversThrough,userSelected,effectHeld).RunAsync();
    }

    internal static async Task<string> MapAsync(PairConfig pair,IStore store,Side side,string path,CancellationToken ct)
    {
        var other=PathNames.Other(side);
        if(await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,path),ct) is {} exact) return exact.PathOn(other);
        return PathNames.Parent(path).Length==0?PathNames.SpellingOn(other,path):await ItemSync.CounterpartAsync(pair,side,path,store,ct);
    }
}
