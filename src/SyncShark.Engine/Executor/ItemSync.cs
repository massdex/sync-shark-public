namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Prepares, observes and executes item decisions through the shared action executor.</summary>
public static class ItemSync
{
    /// <summary>Use the supplied scope and attach the Dropbox clock sample only to rows needing a server-deletion decision.</summary>
    public static Task ApplyAsync(PairConfig pair, PathScope paths, Change change, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
