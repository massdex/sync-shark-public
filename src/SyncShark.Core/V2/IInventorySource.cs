namespace Massdex.SyncShark.V2;

/// <summary>Reads one root's complete or incremental inventory.</summary>
public interface IInventorySource
{
    /// <summary>Read only this folder entity and descendants, without advancing any root cursor.</summary>
    Task ReadSubtreeAsync(string path, Func<IReadOnlyList<ItemState>, Task> accept, CancellationToken ct,
        IProgress<string>? progress = null) =>
        throw new NotSupportedException("Subtree listing not implemented; a whole-root scan will not be used for removal review.");

    /// <summary>After reconciliation is stored, publish completed verification in bounded pages and report affected paths.
    /// Local content proof runs independently in indexing; this foreground hook never drains pending hashes.</summary>
    Task VerifyPendingAsync(Func<string, bool> include, Action<string> touched, CancellationToken ct) => Task.CompletedTask;

    Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct);
    Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>
    /// Proven deletion times, from this side's own history, for paths it no longer holds; a path without proof is
    /// left out. A first-sync preview reads these on every preview, since it persists no earlier scan (decision 4).
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct);
}
