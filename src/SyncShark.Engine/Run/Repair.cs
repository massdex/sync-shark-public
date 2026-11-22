namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns one Repair's acquisition, dependent work and source completion.</summary>
public static class Repair
{
    /// <summary>Run operation on the supplied facts.</summary>
    public static Task RunAsync(PairConfig pair, ISide local, ISide dropbox, IStore store, Scope? scope, CancellationToken ct) => throw new NotImplementedException();
}
