namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retries operations for the executor module.</summary>
public sealed class Retries
{
    /// <summary>Use the existing pair owner for accepted-job custody; do not start another checker.</summary>
    public Retries(Unfinished? jobOwner = null) => throw new NotImplementedException();

    /// <summary>For the PutBack kind retry the identified version create-only using the retained input and binding; never guess a version or overwrite an occupant.</summary>
    public Task RetryAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice failure, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough = null) => throw new NotImplementedException();
}
