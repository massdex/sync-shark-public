namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns finite live opportunities and session-only pause;</summary>
public static class AutoSync
{
    /// <summary>Run operation on the supplied facts.</summary>
    public static Task RunAsync(PairConfig pair, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Populate operation on the supplied facts.</summary>
    public static Task PopulateAsync(PairConfig pair, Scope scope, ISide source, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Settles one acquired page, including an empty page, without waiting for another source read.</summary>
    public static Task ApplyPageAsync(PairConfig pair, Side source, ChangePage page, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
