namespace Massdex.SyncShark.V2;

/// <summary>One resolver page of entity metadata. Discarded before refresh; never deletion evidence.</summary>
internal sealed class LocalJournalLookup(IStateStore store, RootDef root, LocalCatchUpCounters? counters)
{
    private readonly uint _volume = LocalItems.ParseObjectId(root.RootObjectId).Volume;
    private readonly Dictionary<string, (ItemState? Item, bool MultipleNames)> _page = new(StringComparer.Ordinal);

    internal void Prepare(IReadOnlyList<NtfsUsnChange> records, CancellationToken ct)
    {
        _page.Clear();
        var ids = records.SelectMany(record => new[] { record.FileId, record.ParentFileId })
            .Distinct().Select(id => SyncLocalEndpointStore.ObjectId(_volume, id)).ToArray();
        for (var offset = 0; offset < ids.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var result = store.JournalObjects(root.RootId, ids[offset..], ct);
            counters?.ObjectBatch();
            if (result.Count == 0) throw new ArgumentOutOfRangeException(nameof(result.Count), result.Count, null);
            foreach (var entry in result) _page.Add(entry.Key, entry.Value);
            offset += result.Count;
        }
    }

    internal ItemState? ById(ulong id)
    {
        counters?.ObjectLookup();
        return _page[SyncLocalEndpointStore.ObjectId(_volume, id)].Item;
    }

    internal bool MultipleNames(string objectId) => _page.TryGetValue(objectId, out var entry)
        ? entry.MultipleNames : store.HasMultipleNames(root.RootId, objectId);

    internal void Clear() => _page.Clear();
}
