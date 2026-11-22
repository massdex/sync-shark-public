namespace Massdex.SyncShark.Engine.Storage;

/// <summary>StoreMove operations for the storage module.</summary>
public sealed class StoreMove
{
    /// <summary>After verifying the selected source image and before publishing the stage, verify its required connections. On a failed switch invoke the returned cleanup; leave pre-existing lists alone.</summary>
    public Task MoveAsync(string predecessorRoot, string productRoot, Func<string,string,IReadOnlyList<string>,Action?> verifyConnections, CancellationToken ct) => throw new NotImplementedException();
}
