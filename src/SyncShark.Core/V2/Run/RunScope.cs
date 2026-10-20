namespace Massdex.SyncShark.V2;

/// <summary>Loads full inventories or touched rows; mapping context never becomes planner item rows.</summary>
internal sealed class RunScope
{
    private readonly IStateStore _store;
    private readonly IReadOnlyDictionary<string, Side>? _decisions;
    private HashSet<string> _downloadChoices = [];
    private readonly IReadOnlyList<SyncIgnoreRule> _rules;
    private readonly Func<string, bool> _includeA;
    private readonly Func<string, bool> _includeB;
    private readonly LinkExclusions _links;
    private readonly RebuildableSelection? _rebuildables;
    private ProviderRefusedPaths _refusedFoldersA = new([]);
    private ProviderRefusedPaths _refusedFoldersB = new([]);

    public RunScope(IStateStore store, Func<string, bool> includeA, Func<string, bool> includeB,
        LinkExclusions? links = null, RebuildableSelection? rebuildables = null, IReadOnlyList<SyncIgnoreRule>? rules = null, IReadOnlyDictionary<string, Side>? decisions = null)
    {
        _store = store;
        _decisions = decisions;
        _rules = rules ?? [];
        _includeA = includeA;
        _includeB = includeB;
        _links = links ?? LinkExclusions.None;
        _rebuildables = rebuildables is { Folders.Count: > 0 } ? rebuildables : null;
    }

    private void LoadRefusedFolders(PairDef pair)
    {
        (_refusedFoldersA, _refusedFoldersB) = ProviderRefusedPaths.Load(_store, pair);
        _downloadChoices = _decisions?.Where(entry =>
            (entry.Value == Side.A ? pair.A : pair.B).Provider == SyncProviders.Dropbox)
            .Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
    }

    public Scope LoadFull(PairDef pair) => Prepare(pair, _store.Live(pair.A.RootId), _store.Live(pair.B.RootId),
        _store.Baselines(pair.PairId), [], []);


    public Scope LoadTouched(PairDef pair, IReadOnlyCollection<string> touched,
        IReadOnlyCollection<string> changeIdsA, IReadOnlyCollection<string> changeIdsB)
    {
        LoadRefusedFolders(pair);
        var prefixes = new HashSet<string>(touched, StringComparer.OrdinalIgnoreCase);
        var ancestorsA = new List<ItemState>();
        var ancestorsB = new List<ItemState>();
        if (WindowsPair(pair))
        {
            // Empty folders have no child-directory row. Their exact linked paths still supply context.
            var ancestors = touched.SelectMany(PairPath.Ancestors).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var basis in _store.BaselinesAt(pair.PairId, Side.A, ancestors)
                .Concat(_store.BaselinesAt(pair.PairId, Side.B, ancestors)).Distinct())
            {
                if (basis.A.Kind != ItemKind.Folder || basis.B.Kind != ItemKind.Folder) continue;
                ancestorsA.AddRange(_store.LiveSpellings(pair.A.RootId, basis.A.Path));
                ancestorsB.AddRange(_store.LiveSpellings(pair.B.RootId, basis.B.Path));
            }
            AddAliases(pair.A.RootId, touched, prefixes, ancestorsA);
            AddAliases(pair.B.RootId, touched, prefixes, ancestorsB);
        }
        var scope = Load();
        var loadedA = scope.A.Select(i => i.ObjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loadedB = scope.B.Select(i => i.ObjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = scope.Baselines.SelectMany(b => new[] {
            loadedA.Contains(b.A.ObjectId) ? null : _store.ByObject(pair.A.RootId, b.A.ObjectId)?.Path ?? b.A.Path, loadedB.Contains(b.B.ObjectId) ? null : _store.ByObject(pair.B.RootId, b.B.ObjectId)?.Path ?? b.B.Path })
            .Where(p => p is not null).Cast<string>().ToArray();
        if (missing.Length == 0) return scope;
        prefixes.UnionWith(missing);
        return Load();

        Scope Load()
        {
            var a = _store.LiveUnder(pair.A.RootId, prefixes);
            var b = _store.LiveUnder(pair.B.RootId, prefixes);
            // Context ancestors must never discover baselines or widen the item scope.
            var baselines = _store.BaselinesTouching(pair.PairId, Side.A, a.Select(i => i.ObjectId).Concat(changeIdsA), prefixes)
                .Concat(_store.BaselinesTouching(pair.PairId, Side.B, b.Select(i => i.ObjectId).Concat(changeIdsB), prefixes))
                .Distinct().ToArray();
            return Prepare(pair, a, b, baselines, ancestorsA, ancestorsB);
        }

    }

    private void AddAliases(string root, IEnumerable<string> touched, HashSet<string> prefixes, List<ItemState> context)
    {
        foreach (var group in touched.Where(p => p.Contains('_') || DropboxNameMapping.Map(p) != p)
            .GroupBy(p => RowCodec.Split(p).Parent, StringComparer.OrdinalIgnoreCase))
        {
            var leaves = group.Select(p => RowCodec.Split(p).Name).ToArray();
            var keys = leaves.Select(DropboxNameMapping.Map).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var aliasLeaf = leaves.Any(p => p.Contains('_') || DropboxNameMapping.Map(p) != p);
            var parents = group.Key.Length == 0 ? new[] { "" } : _store.DirectorySpellings(root, group.Key);
            foreach (var parent in parents)
            {
                // Only the touched leaf's own directory is enumerated, once for all touches there.
                var candidates = aliasLeaf ? _store.LiveChildren(root, parent) :
                    _store.Live(root, leaves.Select(n => parent.Length == 0 ? n : parent + "/" + n));
                foreach (var item in candidates)
                    if (keys.Contains(DropboxNameMapping.Map(RowCodec.Split(item.Path).Name))) prefixes.Add(item.Path);
                foreach (var ancestor in new[] { parent }.Concat(PairPath.Ancestors(parent)))
                    if (ancestor.Length > 0)
                        context.AddRange(_store.LiveSpellings(root, ancestor)); // indexed point lookup, not children
            }
        }
    }

    private static bool WindowsPair(PairDef pair) =>
        ProviderIs(pair.A, SyncProviders.Local) && ProviderIs(pair.B, SyncProviders.Dropbox) ||
        ProviderIs(pair.B, SyncProviders.Local) && ProviderIs(pair.A, SyncProviders.Dropbox);

    private static bool ProviderIs(RootDef root, string provider) =>
        string.Equals(root.Provider, provider, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ItemState> Normalize(PairDef pair, Side side, IReadOnlyList<ItemState> items)
    {
        List<ItemState>? changed = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = NormalizeItem(pair, side, items[i]);
            if (changed is null && item != items[i]) changed = items.Take(i).ToList();
            changed?.Add(item);
        }
        return changed is null ? items : changed;
    }

    private static ItemState NormalizeItem(PairDef pair, Side side, ItemState item) =>
        WindowsPair(pair) && ProviderIs(side == Side.A ? pair.A : pair.B, SyncProviders.Dropbox)
            ? DropboxNameMapping.FromStoredDropbox(item)
            : !WindowsPair(pair) && DropboxNameMapping.LocalPath(item) is not null ? item with { IdentityJson = null } : item;

    private static IReadOnlyList<Baseline> NormalizeBaselines(PairDef pair, IReadOnlyList<Baseline> baselines)
    {
        List<Baseline>? changed = null;
        for (var i = 0; i < baselines.Count; i++)
        {
            var basis = baselines[i];
            var a = NormalizeItem(pair, Side.A, basis.A);
            var b = NormalizeItem(pair, Side.B, basis.B);
            if (changed is null && (a != basis.A || b != basis.B)) changed = baselines.Take(i).ToList();
            changed?.Add(a == basis.A && b == basis.B ? basis : basis with { A = a, B = b });
        }
        return changed is null ? baselines : changed;
    }

    private Scope Prepare(PairDef pair, IReadOnlyList<ItemState> a, IReadOnlyList<ItemState> b,
        IReadOnlyList<Baseline> baselines, IReadOnlyList<ItemState> contextA, IReadOnlyList<ItemState> contextB)
    {
        LoadRefusedFolders(pair);
        baselines = NormalizeBaselines(pair, baselines);
        a = Hide(Normalize(pair, Side.A, a), Side.A);
        b = Hide(Normalize(pair, Side.B, b), Side.B);
        if (WindowsPair(pair))
        {
            // Exclusions follow an established link, not an arbitrary same-spelled other entity.
            var ignoredA = new HashSet<string>(StringComparer.Ordinal);
            var ignoredB = new HashSet<string>(StringComparer.Ordinal);
            foreach (var basis in baselines)
                if (Excluded(basis.A, Side.A) || Excluded(basis.B, Side.B))
                { ignoredA.Add(basis.A.ObjectId); ignoredB.Add(basis.B.ObjectId); }
            a = CarryIgnored(a, ignoredA);
            b = CarryIgnored(b, ignoredB);
        }
        return new(a, b, Filter(baselines))
        {
            ProtectedAliases = WindowsPair(pair) ? baselines.Where(x => Excluded(x.A, Side.A) || Excluded(x.B, Side.B))
                .Where(x => DropboxNameMapping.LocalPath(x.A) is not null || DropboxNameMapping.LocalPath(x.B) is not null)
                .SelectMany(x => new[] { DropboxNameMapping.MatchPath(x.A), DropboxNameMapping.MatchPath(x.B) })
                .ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(),
            ContextA = Hide(Normalize(pair, Side.A, contextA), Side.A).DistinctBy(i => i.ObjectId).ToArray(),
            ContextB = Hide(Normalize(pair, Side.B, contextB), Side.B).DistinctBy(i => i.ObjectId).ToArray()
        };
    }

    private static IReadOnlyList<ItemState> CarryIgnored(IReadOnlyList<ItemState> items,
        HashSet<string> ids)
    {
        // A mapped spelling is not ownership: only a baseline links the other entity.
        if (ids.Count == 0) return items;
        return items.Select(i => ids.Contains(i.ObjectId)
            ? i with { Flags = i.Flags | ItemFlags.Ignored } : i).ToArray();
    }

    private bool Excluded(ItemState item, Side side)
    {
        // An entity is excluded by its own actual path; its other spelling reaches only a linked counterpart.
        var include = side == Side.A ? _includeA : _includeB;
        return !include(item.Path) || SyncIgnorePolicy.IsIgnored(item.Path, item.Kind == ItemKind.Folder, _rules);
    }

    // Decision 59: links never reach either item rows or mapping context.
    private IReadOnlyList<ItemState> Hide(IReadOnlyList<ItemState> items, Side side)
    {
        List<ItemState>? visible = null;
        var links = _links;
        for (var index = 0; index < items.Count; index++)
        {
            if (index % 256 == 0)
                links = _links.ForPaths(items.Skip(index).Take(256)
                    .SelectMany(item => new[] { item.Path, DropboxNameMapping.MatchPath(item) }));
            var item = items[index];
            var alias = DropboxNameMapping.MatchPath(item);
            var covered = links.Count != 0 && (links.Covers(item.Path) || links.Covers(alias)) &&
                !(_rebuildables is { } chosen && chosen.Cloud == side &&
                  UnderRebuildable(chosen, item.Path) && !links.CoversExceptLocalLinks(item.Path));
            var flags = item.Flags;
            var refused = side == Side.A ? _refusedFoldersA : _refusedFoldersB;
            var download = _downloadChoices.Contains(item.Path) && item.Kind == ItemKind.File;
            if (download) flags &= ~ItemFlags.ProviderNameRefused;
            var retained = !download && refused.Retained(item.Path);
            if (!covered)
            {
                var include = side == Side.A ? _includeA : _includeB;
                if (!include(item.Path)) flags |= ItemFlags.Ignored;
                else if (retained)
                    flags = (flags | ItemFlags.Ignored) & ~ItemFlags.CarriedIgnore;
                else if ((!download && refused.Contains(item)) ||
                    Excluded(item, side))
                    flags |= ItemFlags.Ignored | ItemFlags.CarriedIgnore;
            }
            if (visible is null && (covered || flags != item.Flags || retained != item.ProviderRefusalRetained))
            {
                visible = new List<ItemState>(items.Count);
                for (var prior = 0; prior < index; prior++) visible.Add(items[prior]);
            }
            if (!covered && visible is not null)
                visible.Add(flags == item.Flags && retained == item.ProviderRefusalRetained ? item :
                    item with { Flags = flags, ProviderRefusalRetained = retained });

        }
        return visible is null ? items : visible;
    }

    private static bool UnderRebuildable(RebuildableSelection chosen, string path) =>
        chosen.Folders.Any(folder => PairPath.IsSelfOrUnder(path, folder));

    private IReadOnlyList<Baseline> Filter(IReadOnlyList<Baseline> baselines) => baselines
        .Where(b => !Excluded(b.A, Side.A) && !Excluded(b.B, Side.B)).ToArray();

    internal sealed record Scope(IReadOnlyList<ItemState> A, IReadOnlyList<ItemState> B, IReadOnlyList<Baseline> Baselines)
    {
        public IReadOnlySet<string> ProtectedAliases { get; init; } = new HashSet<string>();
        public IReadOnlyList<ItemState> ContextA { get; init; } = [];
        public IReadOnlyList<ItemState> ContextB { get; init; } = [];
    }
}
