namespace Massdex.SyncShark.Engine.Run;

/// <summary>PairRunner operations for the run module.</summary>
public sealed class PairRunner
{
    /// <summary>Run operation on the supplied facts.</summary>
    public Task RunAsync(PairConfig pair, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
