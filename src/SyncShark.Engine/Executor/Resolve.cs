namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Resolve operations for the executor module.</summary>
public sealed class Resolve
{
    /// <summary>Apply operation on the supplied facts.</summary>
    public Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice expected, ResolutionChoice choice, string? name, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
