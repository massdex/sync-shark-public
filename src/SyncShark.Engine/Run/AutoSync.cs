namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns finite live opportunities and session-only pause;</summary>
public static class AutoSync
{
    /// <summary>Run operation on the supplied facts.</summary>
    public static Task RunAsync(PairConfig pair, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>As a child of the current input (not a competitor), one add-only population pass on both mapped endpoints consumes the verified listing from recovery or acquisition; never re-list a potentially reused live path.</summary>
    public static Task PopulateAsync(PairConfig pair, PathScope paths, Scope scope, Side source, Recovery.Folder folder, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Admits journal or feed input through the pair queue and settles one acquired page, including an empty page, without waiting for another source read. Save only Dropbox cursors here; the per-volume Local reader saves the shared settled PC boundary.</summary>
    public static Task ApplyPageAsync(PairConfig pair, PathScope paths, WorkQueue queue, Side source, ChangePage page, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
