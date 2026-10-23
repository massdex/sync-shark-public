namespace Massdex.SyncShark.V2;

/// <summary>One refusal classification: source subtrees may travel with their parent; retained counterparts may not.</summary>
internal sealed class ProviderRefusedPaths(IEnumerable<string> folders)
{
    private readonly HashSet<string> _folders = new(folders, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _retained = new(StringComparer.OrdinalIgnoreCase);

    internal static (ProviderRefusedPaths A, ProviderRefusedPaths B) Load(IStateStore store, PairDef pair)
    {
        var a = store.LiveFlaggedFolders(pair.A.RootId, ItemFlags.ProviderNameRefused);
        var b = store.LiveFlaggedFolders(pair.B.RootId, ItemFlags.ProviderNameRefused);
        var left = new ProviderRefusedPaths(a.Select(item => item.Path));
        var right = new ProviderRefusedPaths(b.Select(item => item.Path));
        Protect(a, Side.A, pair.B.RootId, right);
        Protect(b, Side.B, pair.A.RootId, left);
        return (left, right);

        void Protect(IReadOnlyList<ItemState> sources, Side side, string targetRoot, ProviderRefusedPaths target)
        {
            if (sources.Count == 0) return;
            foreach (var baseline in store.BaselinesTouching(pair.PairId, side, sources.Select(item => item.ObjectId), []))
            {
                var basis = side == Side.A ? baseline.B : baseline.A;
                // Bind to the counterpart entity, not an unrelated occupant of its old path.
                if (store.ByObject(targetRoot, basis.ObjectId) is { Kind: ItemKind.Folder } current)
                    target._retained.Add(current.Path);
            }
        }
    }

    internal static ProviderRefusedPaths Load(IStateStore store, PairDef pair, string rootId)
    {
        var both = Load(store, pair);
        return rootId == pair.A.RootId ? both.A : both.B;
    }

    private static bool Under(HashSet<string> folders, string path)
    {
        if (folders.Count == 0) return false;
        while (true)
        {
            if (folders.Contains(path)) return true;
            var slash = path.LastIndexOf('/');
            if (slash < 0) return false;
            path = path[..slash];
        }
    }

    internal const string RetainedNoticeKey = "provider-refused-retained-copy";
    internal const string RetainedNoticeDetail = "Folder deletion paused: it contains the retained copy of a refused move. Rename the refused source first.";
    public static bool IsRetained(ItemState item) => item.ProviderRefusalRetained;

    public bool BlocksDelete(string path) => _retained.Any(retained =>
        PairPath.IsSelfOrUnder(retained, path) || PairPath.IsSelfOrUnder(path, retained));

    public bool Retained(string path) => Under(_retained, path);
    public bool Contains(ItemState item) => item.Flags.HasFlag(ItemFlags.ProviderNameRefused) ||
        Under(_folders, item.Path) || Retained(item.Path);

    // Production writers observe rows without application flags. Read the source ancestors fresh because
    // a refusal may have been committed after the writer opened. Same source-subtree semantics as Contains.
    internal static bool SourceContains(IStateStore store, string rootId, ItemState item)
    {
        var path = item.Path;
        while (true)
        {
            if (store.LiveSpellings(rootId, path).Any(known =>
                known.Flags.HasFlag(ItemFlags.ProviderNameRefused) &&
                (path == item.Path ? known.Path == item.Path && known.ObjectId == item.ObjectId : known.Kind == ItemKind.Folder)))
                return true;
            var slash = path.LastIndexOf('/');
            if (slash < 0) return false;
            path = path[..slash];
        }
    }
}
