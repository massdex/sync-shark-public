namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns one Repair's acquisition, dependent work and source completion.</summary>
public static class Repair
{
    /// <summary>Read first-Repair state and deleted-name continuation separately from the feed; publish only completed history and revision-matched completion.</summary>
    public static Task RunAsync(PairConfig pair, ISide local, ISide dropbox, IStore store, Scope? scope, CancellationToken ct) => throw new NotImplementedException();
}
