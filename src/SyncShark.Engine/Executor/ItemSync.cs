namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Prepares, observes and executes item decisions through the shared action executor.</summary>
public static class ItemSync
{
    /// <summary>Apply operation on the supplied facts.</summary>
    public static Task ApplyAsync(PairConfig pair, Change change, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, CancellationToken ct) => throw new NotImplementedException();
}
