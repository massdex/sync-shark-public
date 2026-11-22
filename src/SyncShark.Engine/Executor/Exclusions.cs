namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Exclusions operations for the executor module.</summary>
public sealed class Exclusions
{
    /// <summary>For folder copies and kept-side conflict labels, await the population callback from Run; no second child walk.</summary>
    public Task ApplyAsync(PairConfig pair, PathScope paths, Exclusion exclusion, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
