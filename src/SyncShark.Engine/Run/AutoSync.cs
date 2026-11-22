namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns finite live opportunities and session-only pause;</summary>
public static class AutoSync
{
    /// <summary>Run operation on the supplied facts.</summary>
    public static Task RunAsync(PairConfig pair, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>One add-only population pass on both mapped endpoints consumes the verified listing from recovery or acquisition; never re-list a potentially reused live path.</summary>
    public static Task PopulateAsync(PairConfig pair, Scope scope, Side source, Recovery.Folder folder, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Settles one acquired page, including an empty page, without waiting for another source read.</summary>
    public static Task ApplyPageAsync(PairConfig pair, Side source, ChangePage page, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
