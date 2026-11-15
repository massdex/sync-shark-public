namespace Massdex.SyncShark.V2;

/// <summary>Creates change watches for pair roots; production endpoint providers implement it.</summary>
public interface IChangeWatchSource
{
    /// <summary>Starts watching a root; fails when the root cannot be watched (missing, or replaced by another identity).</summary>
    Task<IChangeWatch> WatchAsync(RootDef root, CancellationToken ct);
}
