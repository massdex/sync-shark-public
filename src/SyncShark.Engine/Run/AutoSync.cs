namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns finite live opportunities and session-only pause;</summary>
public static class AutoSync
{
    /// <summary>Run operation on the supplied facts.</summary>
    public static Task RunAsync(PairConfig pair, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Populate operation on the supplied facts.</summary>
    public static Task PopulateAsync(PairConfig pair, Scope scope, ISide source, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
