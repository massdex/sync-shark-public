namespace Massdex.SyncShark.V2;

public sealed partial class DropboxInventory
{
    internal async Task<Delta> ReadExactAsync(IReadOnlyList<string> paths, string cursor, long gen,
        Func<string, bool> include, CancellationToken ct)
    {
        var exact = paths.Where(include).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<Change>();
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in exact.Chunk(DropboxPilotClient.MetadataPageLimit))
        {
            // Existing metadata owner verifies the account/root and uses the existing HTTP limiter.
            // Only a present dictionary key with a null value is proven provider not_found.
            var metadata = await _client.ReadMetadataPageAsync(page, ct,
                (path, error) => failures[path] = error.Message);
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
                            failures.TryAdd(path, MessageCatalog.Default.Message("guardedInventoryIncomplete"));
                        else changes.Add(new(ChangeKind.Delete, item.ObjectId, item.Path, null, null));
            }
        }
        return new(_root.RootId, _side, gen, cursor, cursor, changes, false, failures.Keys.ToArray())
        {
            IncludesPath = path => exact.Contains(path) && !failures.ContainsKey(path),
            ExactPaths = exact,
            IncompleteReasons = failures
        };
    }
}
