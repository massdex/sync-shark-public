namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Wraps a real plan and cancels its token after the given frozen entry has been yielded, so a test can
/// exercise cancellation at a real enumeration boundary rather than before the freeze starts.</summary>
internal sealed class CancellingFolderPlan(FolderPlan inner, CancellationTokenSource cancel, int after) : FolderPlan
{
    public bool EntryYielded { get; private set; }

    public Row Folder => inner.Folder;
    public Side DeletingFrom => inner.DeletingFrom;
    public bool DeleteWhole => inner.DeleteWhole;
    public Task CaptureAsync(IAsyncEnumerable<Listed> source, CancellationToken ct) => inner.CaptureAsync(source, ct);
    public void PrepareRemoval(Row observed) => inner.PrepareRemoval(observed);
    public IEnumerable<Entry> Entries(CancellationToken ct) => inner.Entries(ct);
    public IEnumerable<(string Key, string NativeKey, Entry? Entry, SyncedEntry? Synced)> Children(
        CancellationToken ct) => inner.Children(ct);

    public void MarkClassified(string key, string nativeKey, FolderChild child, bool followUp, CancellationToken ct) =>
        inner.MarkClassified(key, nativeKey, child, followUp, ct);

    public void PrepareBranches(CancellationToken ct) => inner.PrepareBranches(ct);
    public IEnumerable<(long Id, string Key, FolderChild Child)> RemovedChildren(CancellationToken ct) =>
        inner.RemovedChildren(ct);

    public bool HasSurvivorAtOrBelow(string key, CancellationToken ct) => inner.HasSurvivorAtOrBelow(key, ct);
    public bool HasSelectedAncestor(string key, CancellationToken ct) => inner.HasSelectedAncestor(key, ct);
    public void SelectBranch(long id, CancellationToken ct) => inner.SelectBranch(id, ct);
    public void Complete(bool deleteWhole, CancellationToken ct) => inner.Complete(deleteWhole, ct);
    public bool ForgetTouchesUnreadable(CancellationToken ct) => inner.ForgetTouchesUnreadable(ct);
    public IEnumerable<FolderChild> Branches(CancellationToken ct) => inner.Branches(ct);
    public IEnumerable<FolderChild> Survivors(CancellationToken ct) => inner.Survivors(ct);

    public IEnumerable<FrozenFolderEntry> FrozenEvidence(int pageSize, CancellationToken ct)
    {
        var seen = 0;
        foreach (var entry in inner.FrozenEvidence(pageSize, ct))
        {
            yield return entry;
            if (++seen != after) continue;
            EntryYielded = true;
            cancel.Cancel();
        }
    }

    public void Dispose() => inner.Dispose();
}
