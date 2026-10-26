namespace Massdex.SyncShark.V2;

// Exercises the real Delta -> SideScan -> completion path while the settle fixture drains.
internal sealed class SettleBacklogInventory(IInventorySource inner) : IInventorySource
{
    internal bool Backlog { get; set; } = true;
    public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct) =>
        inner.FullAsync(gen, include, p, ct);
    public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
        (await inner.ChangesSinceAsync(cursor, gen, include, ct)) with { JournalBacklog = Backlog };
    public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        inner.HashAsync(paths, ct);
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        inner.DeletionTimesAsync(paths, ct);
}
