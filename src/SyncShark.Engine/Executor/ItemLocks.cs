namespace Massdex.SyncShark.Engine.Executor;

/// <summary>ItemLocks operations for the executor module.</summary>
public sealed class ItemLocks
{
    /// <summary>Run work under the selected action’s physical footprint.</summary>
    public Task<T> RunAsync<T>(PairConfig pair, Planner.SyncAction action, IReadOnlyList<RenameCandidate> candidates, Func<Task<T>> work, CancellationToken ct, string? caseSuffix = null) => throw new NotImplementedException();
    /// <summary>Acquire operation on the supplied facts.</summary>
    public ValueTask<IDisposable> AcquireAsync(IReadOnlyList<(Side Side, string Key)> footprint, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>AcquireAsync for the selected ItemLocks operation.</summary>
    public ValueTask<IDisposable> AcquireAsync(IReadOnlyList<(Side Side,string Key,bool Subtree)> footprint, CancellationToken ct) => throw new NotImplementedException();
}
