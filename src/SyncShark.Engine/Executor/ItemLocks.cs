namespace Massdex.SyncShark.Engine.Executor;

/// <summary>ItemLocks operations for the executor module.</summary>
public sealed class ItemLocks
{
    /// <summary>Acquire operation on the supplied facts.</summary>
    public ValueTask<IDisposable> AcquireAsync(IReadOnlyList<(Side Side, string Key)> footprint, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>AcquireAsync for the selected ItemLocks operation.</summary>
    public ValueTask<IDisposable> AcquireAsync(IReadOnlyList<(Side Side,string Key,bool Subtree)> footprint, CancellationToken ct) => throw new NotImplementedException();
}
