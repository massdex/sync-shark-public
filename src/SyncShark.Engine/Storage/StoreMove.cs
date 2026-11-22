namespace Massdex.SyncShark.Engine.Storage;

/// <summary>StoreMove operations for the storage module.</summary>
public sealed class StoreMove
{
    /// <summary>After verifying the selected source image and before publishing the stage, verify its required connections. On a failed switch invoke the returned cleanup; leave pre-existing lists alone.</summary>
    public Task MoveAsync(string predecessorRoot, string productRoot, Func<string,string,IReadOnlyList<string>,Action?> verifyConnections, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>For the first explicit pair add only, build and verify an attempt-local empty image and publish its closed file create-only.</summary>
    public Task CreateAsync(string productRoot, CancellationToken ct) => throw new NotImplementedException();
}
