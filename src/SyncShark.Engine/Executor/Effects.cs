namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Effects operations for the executor module.</summary>
public sealed class Effects
{
    /// <summary>Check whether a collision identifies the selected target.</summary>
    internal static bool AtTarget(PathOccupiedException error, Side side, string target) => throw new NotImplementedException();

    /// <summary>Pass the observed target into staging. Finish reading a file that replaces a folder before removing that folder.</summary>
    public Task<EffectOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Planner.SyncAction action, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope, IEnumerable<Change>>? heldInput = null, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough = null, Action<Notice>? failedNotice = null, bool userSelected = false) => throw new NotImplementedException();

    /// <summary>Handle a collision within the selected endpoints and their verified coverage.</summary>
    public Task<EffectOutcome> CollisionAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Side written, Entry selected, Entry incumbent, Entry? counterpart, SyncedEntry? saved, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null) => throw new NotImplementedException();

    /// <summary>Decide a grouped move from its mapped endpoints and selected destination.</summary>
    public static Task<Planner.SyncAction> DecideMoveAsync(PairConfig pair, EngineSettings settings, PathScope paths, IReadOnlyList<RenameCandidate> group, ISide local, ISide dropbox, IStore store, bool dirty, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Recover the selected competing version in its mapped scope and report the effect outcome.</summary>
    public Task<EffectOutcome> RecoverConflictAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Scope scope,Side side,Entry version,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding,CancellationToken ct) => throw new NotImplementedException();
}

/// <summary>Only an applied effect permits its caller to settle the notice or remove the source.</summary>
public enum EffectOutcome { Applied, Pending, Refused }
