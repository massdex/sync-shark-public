namespace Massdex.SyncShark.V2;

public sealed partial class DropboxInventory
{
    internal Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, PilotRemoteEntry?>>>? ExactMetadataForTest { get; set; }
    internal async Task<Delta> ReadExactAsync(IReadOnlyList<string> paths, string cursor, long gen,
        Func<string, bool> include, CancellationToken ct)
    {
        var exact = paths.Where(include).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<Change>();
        var missingFolders = new List<ItemState>();
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in exact.Chunk(DropboxPilotClient.MetadataPageLimit))
        {
            // Existing metadata owner verifies the account/root and uses the existing HTTP limiter.
            // Only a present dictionary key with a null value is proven provider not_found.
            var metadata = ExactMetadataForTest is { } read ? await read(page, ct) :
                await _client.ReadMetadataPageAsync(page, ct, (path, error) => failures[path] = error.Message);
            var before = _known.Live(_root.RootId, page);
            foreach (var path in page)
            {
                if (!metadata.TryGetValue(path, out var entry))
                { failures.TryAdd(path, MessageCatalog.Default.Message("guardedInventoryIncomplete")); continue; }
                if (entry is not null)
                {
                    var item = DropboxItems.ToItem(entry);
                    changes.Add(new(ChangeKind.Upsert, item.ObjectId, item.Path, item, null));
                }
                else
                    foreach (var item in before.Where(item => PairPath.Same(item.Path, path)))
                        if (item.Kind == ItemKind.Folder)
                            missingFolders.Add(item);
                        else changes.Add(new(ChangeKind.Delete, item.ObjectId, item.Path, null, null));
            }
        }
        CompleteMissingFolders(missingFolders, changes, failures);
        var incomplete = failures.Keys.ToArray();
        return new(_root.RootId, _side, gen, cursor, cursor,
            changes.Where(change => PairPath.Observed(change.Path, incomplete)).ToArray(), false, incomplete)
        {
            IncludesPath = path => exact.Contains(path) && PairPath.Observed(path, incomplete),
            ExactPaths = exact,
            IncompleteReasons = failures
        };
    }

    private static void CompleteMissingFolders(IReadOnlyList<ItemState> folders, IReadOnlyList<Change> changes,
        System.Collections.Concurrent.ConcurrentDictionary<string, string> failures)
    {
        // A positive same-identity observation at its new alias proves a move, not subtree absence.
        bool added;
        do
        {
            added = false;
            foreach (var folder in folders.Where(folder => !failures.ContainsKey(folder.Path)))
                if (!changes.Any(change => change.Kind == ChangeKind.Upsert && change.ObjectId == folder.ObjectId &&
                    change.Path != folder.Path && !failures.Keys.Any(failed =>
                        PairPath.IsSelfOrUnder(failed, folder.Path) || PairPath.IsSelfOrUnder(folder.Path, failed) ||
                        PairPath.IsSelfOrUnder(failed, change.Path) || PairPath.IsSelfOrUnder(change.Path, failed))))
                    added |= failures.TryAdd(folder.Path, MessageCatalog.Default.Message("guardedInventoryIncomplete"));
        } while (added);
    }
}
