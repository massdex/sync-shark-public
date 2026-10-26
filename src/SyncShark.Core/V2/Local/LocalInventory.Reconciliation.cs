namespace Massdex.SyncShark.V2;

public sealed partial class LocalInventory
{
    internal Func<IReadOnlyList<SyncDiscoveryRoot>, CancellationToken, Task<SyncDiscoveryResult?>>? DiscoveryReadForTest { get; set; }

    internal async Task<bool> EnumerateMetadataAsync(
        Func<IReadOnlyList<(ItemState Item, long? Usn)>, Task> accept, CancellationToken ct)
    {
        using var lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        var root = new SyncDiscoveryRoot(lease.Path, _volume, _rootFileId);
        var checkpoint = ReadWalkCheckpoint();
        var discovered = DiscoveryReadForTest is { } read ? await read([root], ct) :
            await SyncDiscoveryClient.TryReadAsync([root], ct);
        var view = ConsistentDiscovery(discovered, checkpoint, root);
        if (view is not null)
        {
            RunTrace.Emit("reconciliation selection source: Discovery metadata");
            foreach (var page in view.Items.Values.Chunk(V2Settings.PlanningObservationBatchRows))
            {
                ct.ThrowIfCancellationRequested();
                await accept(page.Select(item => (DiscoveryItem(item), (long?)item.Usn)).ToArray());
            }
            return true;
        }
        RunTrace.Emit("reconciliation selection source: metadata walk with file USN fallback");
        var scan = await ScanAsync(lease, ct, _ => true, null, null);
        if (!scan.Complete || DecisionPaths(scan, _ => true).Count > 0)
            throw new IOException(string.Join("; ", DecisionReasons(scan, _ => true).Values));
        foreach (var page in scan.Entries.Values.Chunk(V2Settings.PlanningObservationBatchRows))
            await accept(page.Select(item => (LocalItems.FromScan(item), (long?)null)).ToArray());
        return false;
    }

    private static SyncDiscoveryView? ConsistentDiscovery(SyncDiscoveryResult? result,
        NtfsUsnCheckpoint? checkpoint, SyncDiscoveryRoot root)
    {
        if (result is null || checkpoint is null || result.Before.JournalId != checkpoint.JournalId ||
            result.After.JournalId != checkpoint.JournalId || checkpoint.LowestValidUsn > result.Before.NextUsn ||
            result.After.FirstUsn > checkpoint.NextUsn) return null;
        var view = result.Roots.FirstOrDefault(view => view.Root == root);
        // Discovery attributes cannot distinguish a Cloud Files placeholder from a link.
        // The ordinary scanner owns that classification, so retain it for such roots.
        return view?.Items.Values.Any(item => (item.Attributes & FileAttributes.ReparsePoint) != 0) == false ? view : null;
    }

    private ItemState DiscoveryItem(SyncDiscoveryItem item) => new(item.RelativePath,
        (item.Attributes & FileAttributes.Directory) != 0 ? ItemKind.Folder : ItemKind.File,
        SyncLocalEndpointStore.ObjectId(_volume, item.FileId), null, null, item.Size,
        new DateTimeOffset(DateTime.SpecifyKind(item.ModifiedUtc, DateTimeKind.Utc)),
        (item.Attributes & FileAttributes.ReparsePoint) != 0 ? ItemFlags.Link : ItemFlags.None);
}
