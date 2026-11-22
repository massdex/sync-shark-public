namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Prepares, observes and executes item decisions through the shared action executor.</summary>
public static class ItemSync
{
    /// <summary>Only a matching acquired journal ID permits comparing a change USN to the saved PC publication. Use the supplied scope and attach the Dropbox clock sample only to rows needing a server-deletion decision.</summary>
    public static Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change change, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope, IEnumerable<Change>>? heldInput = null, Action<string, bool>? failed = null) => throw new NotImplementedException();
    /// <summary>Decide the prepared comparison row (empty candidates) or the whole binding group (no synthetic row); reject a missing row with no candidates. Preserve acquisition evidence, recheck freshness before effects, and execute the one decision here.</summary>
    public static Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Row? prepared, IReadOnlyList<RenameCandidate> bindingCandidates, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope, IEnumerable<Change>>? heldInput = null, Action<string, bool>? failed = null, bool addOnly = false, Action<Notice>? failedNotice = null) => throw new NotImplementedException();
}
