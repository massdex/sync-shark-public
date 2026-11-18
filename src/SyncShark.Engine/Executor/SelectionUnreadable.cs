namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Matches acquired directory identities to unreadable registrations before releasing their retry footprint.</summary>
internal sealed class SelectionUnreadable(IReadOnlyList<string> recorded)
{
    private readonly HashSet<string> _recorded = recorded.ToHashSet(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Path, string Id)> _acquired = new(StringComparer.Ordinal);

    internal (string Path, string Id) this[string key] => _acquired[key];

    internal void Acquire(string path, string id)
    {
        var key = PathNames.Key(path);
        if (_recorded.Contains(key)) _acquired[key] = (path, id);
    }

    internal List<(Side Side, string Key)> Release(IStateStore store, string pairId, long generation,
        CancellationToken ct)
    {
        var released = new List<(Side Side, string Key)>();
        foreach (var (key, proof) in _acquired)
        {
            ct.ThrowIfCancellationRequested();
            if (store.ReleaseUnreadable(pairId, generation, key, proof.Id)) released.Add((Side.Local, key));
        }
        return released;
    }
}
