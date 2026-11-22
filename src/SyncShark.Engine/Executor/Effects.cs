namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Effects operations for the executor module.</summary>
public sealed class Effects
{
    /// <summary>Pass the observed target into staging. Finish reading a file that replaces a folder before removing that folder.</summary>
    public Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Planner.SyncAction action, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
