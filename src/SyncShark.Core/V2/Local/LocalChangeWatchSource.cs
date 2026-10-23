namespace Massdex.SyncShark.V2;

/// <summary>Creates one <see cref="LocalChangeWatch"/> per local pair root over its product-owned exclusion set.</summary>
internal sealed class LocalChangeWatchSource(IReadOnlySet<string> productOwnedPaths) : IChangeWatchSource
{
    /// <inheritdoc/>
    public Task<IChangeWatch> WatchAsync(RootDef root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();
        var relative = LocalItems.ProductOwnedRelative(root.RootPath, productOwnedPaths);
        // A missing or replaced root faults the returned task, as the watch-source contract expects.
        try
        {
            return Task.FromResult<IChangeWatch>(LocalChangeWatch.Open(root, relative));
        }
        catch (Exception error)
        {
            return Task.FromException<IChangeWatch>(error);
        }
    }
}
