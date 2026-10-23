namespace Massdex.SyncShark.V2;

/// <summary>Loads full inventories or touched rows; mapping context never becomes planner item rows.</summary>
internal sealed class RunScope
{
    // Each item may contribute two mapped spellings; each baseline contributes four.
    // RehashProtection accepts at most 1024 distinct paths, bound as one JSON parameter.
    private const int ItemBatchSize = 512;
    private const int BaselineBatchSize = 256;
    private readonly bool _persistDeferred;
    private readonly CancellationToken _cancellationToken;
    private bool _allChangedA, _allChangedB;
    private IReadOnlySet<string> _dirtyA = new HashSet<string>();
    private IReadOnlySet<string> _dirtyB = new HashSet<string>();
    private readonly HashSet<string> _permanentA = new(StringComparer.Ordinal);
    private readonly HashSet<string> _permanentB = new(StringComparer.Ordinal);
    private PairDef? _deferredPair;
    private readonly List<(string Root, ItemState Item)> _deferred = new(256);
    private bool _streamingProtection;
    private long _protectionBatches, _protectionTotal, _protectionStarted;
    private readonly IStateStore _store;
    private readonly IReadOnlyDictionary<string, Side>? _decisions;
    private HashSet<string> _downloadChoices = [];
    private readonly IReadOnlyList<SyncIgnoreRule> _rules;
    private readonly Func<string, bool> _includeA;
    private readonly Func<string, bool> _includeB;
    private readonly LinkExclusions _links;
    private LinkExclusions? _scopeLinks;
    private readonly IReadOnlySet<string>? _activeRetries;
    private readonly bool _restoredRetriesOnly;
    private readonly RecoveryRestoreProtection? _restoreProtection;    internal Action? AfterHideAForTest { get; set; }
    internal Action? RetryProtectionReadForTest { get; set; }
    private readonly RebuildableSelection? _rebuildables;
    private ProviderRefusedPaths _refusedFoldersA = new([]);
    private ProviderRefusedPaths _refusedFoldersB = new([]);

    public RunScope(IStateStore store, Func<string, bool> includeA, Func<string, bool> includeB,
        LinkExclusions? links = null, RebuildableSelection? rebuildables = null, IReadOnlyList<SyncIgnoreRule>? rules = null, IReadOnlyDictionary<string, Side>? decisions = null,
        IReadOnlySet<string>? activeRetries = null, bool restoredRetriesOnly = false,
        RecoveryRestoreProtection? restoreProtection = null, bool persistDeferred = false,
        CancellationToken cancellationToken = default)
    {
        _persistDeferred = persistDeferred;
        _cancellationToken = cancellationToken;
        _store = store;
        _activeRetries = activeRetries;
        _restoredRetriesOnly = restoredRetriesOnly;
        _restoreProtection = restoreProtection;
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

    internal void PendingChanges(IEnumerable<string> a, IEnumerable<string> b, bool allA = false, bool allB = false)
    {
        _allChangedA = allA;
        _allChangedB = allB;
        _dirtyA = a.ToHashSet(StringComparer.Ordinal);
        _dirtyB = b.ToHashSet(StringComparer.Ordinal);
    }

    public Scope LoadFull(PairDef pair) => Prepare(pair, _store.Live(pair.A.RootId), _store.Live(pair.B.RootId),
        _store.Baselines(pair.PairId), [], [], "Full");

    // Hashing updates only content evidence. Keep normalized mapping identities and scope-only flags,
    // and read only the requested paths rather than decoding both roots and all baselines again.
    public Scope RefreshHashes(PairDef pair, Scope scope, HashRequest request) => scope with
    {
        A = Refresh(pair, Side.A, scope.A, request.PathsA),
        B = Refresh(pair, Side.B, scope.B, request.PathsB)
    };

    private IReadOnlyList<ItemState> Refresh(PairDef pair, Side side, IReadOnlyList<ItemState> items,
        IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return items;
        var root = side == Side.A ? pair.A.RootId : pair.B.RootId;
        var current = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        foreach (var stored in _store.Live(root, paths))
            current.TryAdd(stored.Path, NormalizeItem(pair, side, stored));
        List<ItemState>? updated = null;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (current.TryGetValue(item.Path, out var stored) && stored.ObjectId == item.ObjectId)
            {
                const ItemFlags evidenceFlags = ItemFlags.NeedsRehash | ItemFlags.Unreadable;
                var flags = (item.Flags & ~evidenceFlags) | (stored.Flags & evidenceFlags);
                if (item.Hash != stored.Hash || item.Flags != flags)
                {
                    updated ??= items.ToList();
                    updated[index] = item with { Hash = stored.Hash, Flags = flags };
                }
            }
        }
        return updated is null ? items : updated;
    }


    public Scope LoadTouched(PairDef pair, IReadOnlyCollection<string> touched,
        IReadOnlyCollection<string> changeIdsA, IReadOnlyCollection<string> changeIdsB)
    {
        PendingChanges(changeIdsA, changeIdsB);
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
        for (var expansion = 0; ; expansion++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var scope = Load();
            var loadedA = scope.A.Select(i => i.ObjectId).ToHashSet(StringComparer.Ordinal);
            var loadedB = scope.B.Select(i => i.ObjectId).ToHashSet(StringComparer.Ordinal);
            var frontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var basis in scope.Baselines)
            {
                var missingA = AddCounterpart(pair.A.RootId, basis.A, loadedA);
                var missingB = AddCounterpart(pair.B.RootId, basis.B, loadedB);
                if (missingA || missingB)
                {
                    held.Add(basis.A.Path);
                    held.Add(basis.B.Path);
                }
            }
            if (frontier.Count == 0)
                return Prepare(pair, scope.A, scope.B, scope.Baselines, ancestorsA, ancestorsB);
            if (expansion == 64)
            {
                // Never let the expansion budget turn an unloaded endpoint into absence.
                // The exact frontier remains retryable; disconnected closed siblings can run.
                if (_persistDeferred)
                    foreach (var path in frontier)
                        _store.SaveSyncRetry(pair.PairId, new(path, "planning", 0,
                            "Linked scope expansion reached its bounded slice; retry the remaining identities.",
                            DateTimeOffset.MinValue));
                held.UnionWith(frontier);
                return Prepare(pair, scope.A, scope.B, scope.Baselines, ancestorsA, ancestorsB,
                    closureHolds: held);
            }
            prefixes.UnionWith(frontier);

            bool AddCounterpart(string root, ItemState basis, HashSet<string> loaded)
            {
                if (loaded.Contains(basis.ObjectId)) return false;
                var path = _store.ByObject(root, basis.ObjectId)?.Path ?? basis.Path;
                if (prefixes.Contains("") || prefixes.Contains(path) || PairPath.Ancestors(path).Any(prefixes.Contains)) return false;
                frontier.Add(path);
                return true;
            }
        }

        Scope Load()
        {
            var a = _store.LiveUnder(pair.A.RootId, prefixes);
            var b = _store.LiveUnder(pair.B.RootId, prefixes);
            // Context ancestors must never discover baselines or widen the item scope.
            var baselines = _store.BaselinesTouching(pair.PairId, Side.A, a.Select(i => i.ObjectId).Concat(changeIdsA), prefixes)
                .Concat(_store.BaselinesTouching(pair.PairId, Side.B, b.Select(i => i.ObjectId).Concat(changeIdsB), prefixes))
                .Distinct().ToArray();
            return new(a, b, baselines);
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
        IReadOnlyList<Baseline> baselines, IReadOnlyList<ItemState> contextA, IReadOnlyList<ItemState> contextB,
        string provenanceScope = "Touched", IReadOnlyCollection<string>? closureHolds = null)
    {
        PlanProvenance.Current.Value?.BeginScope(provenanceScope);
        _cancellationToken.ThrowIfCancellationRequested();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _protectionStarted = started;
        _protectionBatches = 0;
        _protectionTotal = Batches(a.Count, ItemBatchSize) + Batches(b.Count, ItemBatchSize) +
            Batches(baselines.Count, BaselineBatchSize) + Batches(contextA.Count, ItemBatchSize) +
            Batches(contextB.Count, ItemBatchSize);
        using var snapshot = (_store as SqliteStateStore)?.OpenPlanningProtection(pair);
        _deferredPair = pair;
        try
        {
            // Normalize before staging so mapped spellings are identical to the consumer.
            a = Normalize(pair, Side.A, a);
            b = Normalize(pair, Side.B, b);
            baselines = NormalizeBaselines(pair, baselines);
            contextA = Normalize(pair, Side.A, contextA);
            contextB = Normalize(pair, Side.B, contextB);
            using var protection = snapshot is not null && _links.HasRehashProtection
                ? snapshot.Stream() : null;
            _streamingProtection = protection is not null;
            _scopeLinks = snapshot is null ? _links : _links.Freeze(snapshot, protection is null ? null : protection.Read);
            if (closureHolds is not null) _scopeLinks = _scopeLinks.WithPending(closureHolds);
            var restore = _restoreProtection;
            var active = _activeRetries?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (snapshot is not null)
            {
                // Null means a custom caller supplied no run snapshot and keeps the original planning-transaction read.
                // An explicit inactive snapshot contributes nothing (no callback, no SQL); beyond the cap the fallback is
                // bound to THIS transaction, so one scope never mixes a frozen preload with the live writer.
                Func<string, bool, bool>? restoreLookup = null;
                if (restore is null)
                    restoreLookup = (path, descendants) => snapshot.RecoveryRestoreProtected(pair.PairId, path, descendants);
                else if (restore.Active)
                    restoreLookup = restore.Bind(snapshot, pair.PairId);
                var retryLookup = active is null ? null : snapshot.RetryLookup(pair.PairId, active,
                    _restoredRetriesOnly, RetryProtectionReadForTest);
                if (restoreLookup is not null || retryLookup is not null)
                    _scopeLinks = _scopeLinks.WithRetryProtection((path, descendants) =>
                        (restoreLookup is not null && restoreLookup(path, descendants)) ||
                        (retryLookup is not null && retryLookup(path, descendants)));
            }
            else if (restore is { Active: true })
            {
                _scopeLinks = _scopeLinks.WithRetryProtection(restore.Covers);
            }
            if (!_streamingProtection) RunActivity.Preparation($"Preparing protection scope 0/{_protectionTotal}");
            LoadRefusedFolders(pair);
            _permanentA.Clear();
            _permanentB.Clear();
            foreach (var basis in baselines)
            {
                if (!_dirtyA.Contains(basis.A.ObjectId) && !_dirtyB.Contains(basis.B.ObjectId)) continue;
                if (!PermanentHold(basis.A, _scopeLinks ?? _links) && !PermanentHold(basis.B, _scopeLinks ?? _links)) continue;
                if (_dirtyA.Contains(basis.A.ObjectId)) _permanentA.Add(basis.A.ObjectId);
                if (_dirtyB.Contains(basis.B.ObjectId)) _permanentB.Add(basis.B.ObjectId);
            }
            var hiddenA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hiddenB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var claims = Planner.ClaimBaselines(baselines, a, b);
            a = Hide(a, Side.A, baselines.Count == 0 ? null : hiddenA);
            AfterHideAForTest?.Invoke();
            b = Hide(b, Side.B, baselines.Count == 0 ? null : hiddenB);
            // Hidden means unknown, never absent. Remove the whole established triple,
            // including a counterpart at a different (moved/mapped) path.
            (a, b, baselines) = HideClaimedTriples(a, b, baselines, hiddenA, hiddenB, claims);
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
                ContextA = HideCore(contextA, Side.A, context: true).DistinctBy(i => i.ObjectId).ToArray(),
                ContextB = HideCore(contextB, Side.B, context: true).DistinctBy(i => i.ObjectId).ToArray()
            };
        }
        finally
        {
            try { FlushDeferred(); }
            finally
            {
                _deferredPair = null;
                _streamingProtection = false;
                RunActivity.Preparation(null);
            }
            _scopeLinks = null;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            RunLog.Trace($"protection scope snapshot: {elapsed.TotalMilliseconds:F0} ms");
            if (elapsed > TimeSpan.FromSeconds(60))
                RunLog.Trace("WARNING: protection scope snapshot exceeded 60 seconds; retained consistent protection until scope completion");
        }
    }

    private (IReadOnlyList<ItemState> A, IReadOnlyList<ItemState> B, IReadOnlyList<Baseline> Baselines)
        HideProtectedTriples(IReadOnlyList<ItemState> a, IReadOnlyList<ItemState> b, IReadOnlyList<Baseline> baselines,
            HashSet<string> hiddenA, HashSet<string> hiddenB) =>
        HideClaimedTriples(a, b, baselines, hiddenA, hiddenB, Planner.ClaimBaselines(baselines, a, b));

    private (IReadOnlyList<ItemState> A, IReadOnlyList<ItemState> B, IReadOnlyList<Baseline> Baselines)
        HideClaimedTriples(IReadOnlyList<ItemState> a, IReadOnlyList<ItemState> b, IReadOnlyList<Baseline> baselines,
            HashSet<string> hiddenA, HashSet<string> hiddenB,
            IEnumerable<(Baseline Basis, ItemState? A, ItemState? B)> claims)
    {
        var visible = new List<Baseline>();
        using var claimed = claims.GetEnumerator();
        baselines = baselines.OrderBy(x => x.ObjectKey, StringComparer.Ordinal).ToArray();
        var links = _scopeLinks ?? _links;
        for (var index = 0; index < baselines.Count; index++)
        {
            if (index % BaselineBatchSize == 0)
            {
                ProtectionBatchStarting();
                links = (_scopeLinks ?? _links).ForPaths(baselines.Skip(index).Take(BaselineBatchSize)
                    .SelectMany(basis => new[] { basis.A.Path, DropboxNameMapping.MatchPath(basis.A),
                        basis.B.Path, DropboxNameMapping.MatchPath(basis.B) }));
            }
            if (!claimed.MoveNext()) throw new InvalidOperationException("Incomplete baseline claims.");
            var (basis, claimA, claimB) = claimed.Current;
            var covered = (claimA is not null && hiddenA.Contains(claimA.ObjectId)) ||
                (claimB is not null && hiddenB.Contains(claimB.ObjectId)) ||
                hiddenA.Contains(basis.A.ObjectId) || hiddenB.Contains(basis.B.ObjectId) ||
                Covered(basis.A, Side.A, links) || Covered(basis.B, Side.B, links);
            if (covered)
            {
                if (!PermanentHold(basis.A, links) && !PermanentHold(basis.B, links))
                {
                    Defer(basis.A, Side.A, links);
                    Defer(basis.B, Side.B, links);
                }
                hiddenA.Add(basis.A.ObjectId);
                hiddenB.Add(basis.B.ObjectId);
                if (claimA is not null) hiddenA.Add(claimA.ObjectId);
                if (claimB is not null) hiddenB.Add(claimB.ObjectId);
            }
            else visible.Add(basis);
        }
        if (visible.Count == baselines.Count) return (a, b, baselines);
        if (PlanProvenance.Current.Value is { } provenance)
        {
            foreach (var item in a) if (hiddenA.Contains(item.ObjectId)) provenance.Visibility(Side.A, item, true, "protected-baseline-triple");
            foreach (var item in b) if (hiddenB.Contains(item.ObjectId)) provenance.Visibility(Side.B, item, true, "protected-baseline-triple");
        }
        return (a.Where(item => !hiddenA.Contains(item.ObjectId)).ToArray(),
            b.Where(item => !hiddenB.Contains(item.ObjectId)).ToArray(), visible);
    }

    private bool Covered(ItemState item, Side side, LinkExclusions links) =>
        links.Count != 0 && (links.Covers(item.Path) || links.Covers(DropboxNameMapping.MatchPath(item))) &&
        !(_rebuildables is { } chosen && chosen.Cloud == side &&
          UnderRebuildable(chosen, item.Path) && !links.CoversExceptLocalLinks(item.Path));

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
    private IReadOnlyList<ItemState> Hide(IReadOnlyList<ItemState> items, Side side, HashSet<string>? hidden = null) =>
        HideCore(items, side, hidden, context: false);

    private IReadOnlyList<ItemState> HideCore(IReadOnlyList<ItemState> items, Side side, HashSet<string>? hidden = null, bool context = false)
    {
        List<ItemState>? visible = null;
        var links = _scopeLinks ?? _links;
        for (var index = 0; index < items.Count; index++)
        {
            if (index % ItemBatchSize == 0)
            {
                ProtectionBatchStarting();
                links = (_scopeLinks ?? _links).ForPaths(items.Skip(index).Take(ItemBatchSize)
                    .SelectMany(item => new[] { item.Path, DropboxNameMapping.MatchPath(item) }));
            }
            var item = items[index];
            var covered = Covered(item, side, links);
            if (!context && PlanProvenance.Current.Value is { } provenance)
                provenance.Visibility(side, item, covered, covered ? ProtectionReason(item, links) : "visible");
            if (covered)
            {
                hidden?.Add(item.ObjectId);
                Defer(item, side, links);
            }
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

    // Labels the frozen production predicates; it never participates in admission or changes a result.
    private static string ProtectionReason(ItemState item, LinkExclusions links)
    {
        var paths = new[] { item.Path, DropboxNameMapping.MatchPath(item) };
        if (paths.Any(links.CoversLink)) return "link";
        if (paths.Any(links.CoversStub)) return "placeholder";
        if (paths.Any(links.CoversPending)) return "pending-decision-or-evidence";
        if (paths.Any(links.TouchesRehash)) return "rehash-or-gap";
        return "other-protection";
    }

    private static bool PermanentHold(ItemState item, LinkExclusions links) =>
        new[] { item.Path, DropboxNameMapping.MatchPath(item) }.Any(path =>
            links.CoversLink(path) || links.CoversStub(path) || links.CoversPending(path));

    private void Defer(ItemState item, Side side, LinkExclusions links)
    {
        if (!_persistDeferred || _deferredPair is not { } pair || _store is not SqliteStateStore ||
            (!(side == Side.A ? _allChangedA : _allChangedB) && !(side == Side.A ? _dirtyA : _dirtyB).Contains(item.ObjectId)) ||
            !new[] { item.Path, DropboxNameMapping.MatchPath(item) }.Any(links.TouchesRehash) ||
            (side == Side.A ? _permanentA : _permanentB).Contains(item.ObjectId) || PermanentHold(item, links) ||
            _store.SyncRetryAt(pair.PairId, item.Path) is not null) return;
        _deferred.Add(((side == Side.A ? pair.A : pair.B).RootId, item));
        if (_deferred.Count == 256) FlushDeferred();
    }

    private void FlushDeferred()
    {
        if (_deferred.Count == 0) return;
        try
        {
            if (_store is SqliteStateStore store && _deferredPair is { } pair)
                store.DeferPlanning(pair.PairId, _deferred);
        }
        catch (Exception error)
        {
            RunLog.Trace($"Deferred planning write failed; retaining retry work: {error.Message}");
            if (_deferredPair is { } pair)
            {
                // Like RetainPlanningFailures, failure to persist retry debt must reach the
                // run's failure owner before it can publish a newer cursor. Keep the batch
                // until every handoff succeeds; partial fallback writes are idempotent.
                foreach (var row in _deferred)
                    _store.SaveSyncRetry(pair.PairId, new(row.Item.Path, "planning", 0,
                        error.Message, DateTimeOffset.MinValue));
            }
        }
        _deferred.Clear();
    }

    private static long Batches(int count, int size) => ((long)count + size - 1) / size;

    private void ProtectionBatchStarting()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var batch = ++_protectionBatches;
        var message = $"Preparing protection scope {batch}/{_protectionTotal}";
        RunActivity.Preparation(message);
        if (batch == 1 || batch % 2048 == 0 || batch == _protectionTotal)
            RunLog.Trace($"{message}; elapsed-ms={System.Diagnostics.Stopwatch.GetElapsedTime(_protectionStarted).TotalMilliseconds:F0}");
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
