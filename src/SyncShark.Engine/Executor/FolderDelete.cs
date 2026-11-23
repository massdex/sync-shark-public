namespace Massdex.SyncShark.Engine.Executor;

/// <summary>FolderDelete operations for the executor module.</summary>
public sealed class FolderDelete
{
    /// <summary>Select a missing ancestor before acquiring its footprint.</summary>
    public static Task<SyncedEntry?> HighestMissingAsync(PairConfig pair, PathScope paths, SyncedEntry folder, ISide source, IStore store, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Dispatch a selected folder through ISide; propagate Pending and retain the binding until its member and evidence settle.</summary>
    public Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncedEntry folder, Side deleting, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Row? covered=null, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null) => throw new NotImplementedException();

    /// <summary>ApplyAsync for the selected FolderDelete operation.</summary>
    public Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Entry folder, SyncedEntry? binding, Side deleting, ActionKind origin, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct,bool splitSelected=false,Row? covered=null,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,bool userSelected=false) => throw new NotImplementedException();
    /// <summary>RecoverHeldAsync for the selected FolderDelete operation.</summary>
    internal static Task<bool> RecoverHeldAsync(PairConfig pair,Scope scope,Side side,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct,Notice? retry=null,ActionKind origin=ActionKind.None,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict=null,IdentityLocation? actualVictim=null) => throw new NotImplementedException();
    /// <summary>Retry recovery for the exact reviewed notice.</summary>
    public static Task<bool> RetryHeldAsync(PairConfig pair, Scope scope, Notice notice, ISide local, ISide dropbox, IStore store, Func<Scope, IEnumerable<Change>> heldInput, Func<Scope, Side, Recovery.Folder, CancellationToken, Task> populate, CancellationToken ct, Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict=null,Func<SyncedEntry,Scope,Scope,CancellationToken,Task>? compareOldBinding=null) => throw new NotImplementedException();
    /// <summary>Put back the identified substitute rather than treating it as the selected victim.</summary>
    public static Task<bool> PutBackSubstituteAsync(PairConfig pair,Scope scope,IdentityLocation receipt,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct) => throw new NotImplementedException();
}
