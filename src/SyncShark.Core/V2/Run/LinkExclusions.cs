namespace Massdex.SyncShark.V2;

/// <summary>
/// The paths a run leaves alone on BOTH sides: every junction, symlink or mount point (decision 59, items flagged
/// <see cref="ItemFlags.Link"/>) and, while stub replacement is off, every online-only cloud-files stub (decision 57,
/// items flagged <see cref="ItemFlags.Placeholder"/>). Nothing at or under such a path is planned, and no folder move
/// or delete may contain one. The one exception is a chosen rebuildable folder's cloud-only delete, which ignores
/// LOCAL links (it never touches the local side). Protection is carried across the run in which the item disappears
/// (the stored state
/// before the scan is unioned with the state after it). One standing summary notice per kind lists them.
/// </summary>
internal sealed class LinkExclusions
{
    /// <summary>The Run rule number of the links summary notice.</summary>
    internal const int Rule = 36;

    /// <summary>The Run rule number of the stubs-left-alone summary notice.</summary>
    internal const int StubRule = 37;

    /// <summary>The links summary notice's stable reason key; the detail carries the count and top folders.</summary>
    internal static readonly NoticeReason Reason = new("links-excluded",
        "links (junctions, symlinks) are not followed; they and the same paths on the other side are not synced");

    /// <summary>The stubs summary notice's stable reason key; the detail carries the count and top folders.</summary>
    internal static readonly NoticeReason StubReason = new("stubs-left-alone",
        "online-only placeholders left alone; they and the same paths on the other side are not synced");

    private const int TopFolders = 5;
    private readonly HashSet<string> _links;
    private readonly HashSet<string> _nonLocalLinks;
    private readonly HashSet<string> _stubs;
    private readonly HashSet<string> _pending;
    private readonly RehashProtection? _owned;
    private readonly RehashProtection? _rehash;
    private readonly Func<string, bool, bool>? _retryProtection;
    private readonly HashSet<string> _all;
    private readonly HashSet<string> _containers;

    private LinkExclusions(IEnumerable<string> links, IEnumerable<string> stubs, IEnumerable<string>? pending = null,
        IEnumerable<string>? nonLocalLinks = null, RehashProtection? rehash = null,
        Func<string, bool, bool>? retryProtection = null, RehashProtection? owned = null)
    {
        _owned = owned;
        _links = new HashSet<string>(links, StringComparer.OrdinalIgnoreCase);
        _nonLocalLinks = new HashSet<string>(nonLocalLinks ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _stubs = new HashSet<string>(stubs, StringComparer.OrdinalIgnoreCase);
        _pending = new HashSet<string>(pending ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _rehash = rehash;
        _retryProtection = retryProtection;
        _all = new HashSet<string>(_links.Concat(_stubs).Concat(_pending), StringComparer.OrdinalIgnoreCase);
        _containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _all)
            for (var cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1))
                _containers.Add(path[..cut]);
    }

    /// <summary>A bounded consumer's immutable overflow answers; ordinary snapshots are shared.</summary>
    internal bool RequiresBatch => _rehash?.RequiresBatch == true || _owned?.RequiresBatch == true;
    internal LinkExclusions ForPaths(IEnumerable<string> paths) => !RequiresBatch ? this :
        new(_links, _stubs, _pending, _nonLocalLinks, _rehash?.ForPaths(paths), _retryProtection, _owned?.ForPaths(paths));

    /// <summary>Nothing left alone.</summary>
    internal static LinkExclusions None { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// The links (and, when <paramref name="stubsLeftAlone"/>, the stubs) both sides of the pair hold in the store.
    /// </summary>
    internal static LinkExclusions Of(IStateStore store, PairDef pair, bool stubsLeftAlone)
    {
        IEnumerable<string> Flagged(ItemFlags flag) =>
            store.LiveFlagged(pair.A.RootId, flag).Concat(store.LiveFlagged(pair.B.RootId, flag))
                .Where(item => flag != ItemFlags.Placeholder || item.Kind == ItemKind.File && !item.Flags.HasFlag(ItemFlags.OwnedPointer)).Select(item => item.Path);
        var nonLocalLinks = new[] { pair.A, pair.B }
            .Where(root => !string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase))
            .SelectMany(root => store.LiveFlagged(root.RootId, ItemFlags.Link)).Select(item => item.Path);
        return new(Flagged(ItemFlags.Link), stubsLeftAlone ? Flagged(ItemFlags.Placeholder) : Array.Empty<string>(),
            pending: store.LiveFlagged(pair.A.RootId, ItemFlags.Quarantined)
                .Concat(store.LiveFlagged(pair.B.RootId, ItemFlags.Quarantined))
                .SelectMany(item => new[] { item.Path, DropboxNameMapping.MatchPath(item) })
                .Concat(store.DefenderGrantHoldPaths(pair.A.RootId)).Concat(store.DefenderGrantHoldPaths(pair.B.RootId))
                .Concat(store.OpenNotices(pair.PairId).Where(n => n.Quarantine is not null).Select(n => n.Path)),
            nonLocalLinks: nonLocalLinks, rehash: new RehashProtection(store, pair), owned: new RehashProtection(store, pair, ItemFlags.OwnedPointer));
    }

    /// <summary>
    /// This run's protection: the state before the scan unioned with the state after it, so a link or stub that
    /// disappears in this run still protects its path (and the other side's items there) for the whole run.
    /// </summary>
    internal LinkExclusions With(LinkExclusions after) =>
        new(_links.Concat(after._links), _stubs.Concat(after._stubs), _pending.Concat(after._pending),
            _nonLocalLinks.Concat(after._nonLocalLinks), after._rehash ?? _rehash,
            after._retryProtection ?? _retryProtection, after._owned ?? _owned);

    /// <summary>
    /// Decision 64: also leaves alone, on both sides, every path whose user-decision notice is open (a first-sync tie
    /// or needs-decision row): no copy, no keep-both, no delete until the user decides. No summary notice; the
    /// decision notices themselves are listed.
    /// </summary>
    internal LinkExclusions WithPending(IEnumerable<string> pending) =>
        new(_links, _stubs, _pending.Concat(pending), _nonLocalLinks, _rehash, _retryProtection, _owned);

    internal LinkExclusions WithRetryProtection(Func<string, bool, bool> protection) =>
        new(_links, _stubs, _pending, _nonLocalLinks, _rehash, protection, _owned);

    internal int Count => (int)Math.Min(int.MaxValue, _all.Count + (_rehash?.Count ?? 0) + (_owned?.Count ?? 0) + (_retryProtection is null ? 0 : 1));

    /// <summary>The recorded link paths (the baselines under them are forgotten, see SyncRun).</summary>
    internal IReadOnlyCollection<string> Links => _links;


    /// <summary>True when the path is protected or lies under a protected path (checks the path and each ancestor).</summary>
    internal bool Covers(string path)
    {
        if (_retryProtection?.Invoke(path, false) == true || _rehash?.Touches(path, descendants: false) == true ||
            _owned?.Touches(path, descendants: false) == true) return true;
        if (_all.Count == 0) return false;
        if (_all.Contains(string.Empty)) return true;
        for (var current = path; current.Length > 0; current = Parent(current))
            if (_all.Contains(current)) return true;
        return false;
    }

    /// <summary>True when a protected path lies strictly under the path (or the path is the whole pair).</summary>
    internal bool Contains(string path) => _retryProtection?.Invoke(path, true) == true ||
        _rehash?.Touches(path, descendants: true) == true || _owned?.Touches(path, descendants: true) == true ||
        _all.Count > 0 && (path.Length == 0 || _containers.Contains(path));

    /// <summary>Ordinary copy/source predicates protect every held path, including hash debt.</summary>
    internal Func<string, bool> Narrow(Func<string, bool> include) =>
        Count == 0 ? include : path => include(path) && !Covers(path);

    /// <summary>Inventory must still observe hash-debt paths so verification can retry.</summary>
    internal Func<string, bool> NarrowInventory(Func<string, bool> include) =>
        _all.Count == 0 ? include : path => include(path) && !InventoryCovers(path);

    // Hash debt protects effects, not inventory: it must be observed again even without new journal records.
    private bool InventoryCovers(string path)
    {
        if (_pending.Contains(string.Empty)) return true;
        for (var current = path; current.Length > 0; current = Parent(current))
            if (_links.Contains(current) || _stubs.Contains(current) || _pending.Contains(current)) return true;
        return false;
    }

    /// <summary>The move/delete predicate narrowed: never at, under or containing a protected path.</summary>
    internal Func<string, bool> NarrowAction(Func<string, bool> include) =>
        Count == 0 ? include : path => include(path) && !Covers(path) && !Contains(path);

    // The same protection without the LOCAL links (cloud links, stubs and pending decisions only).
    private LinkExclusions? _withoutLocalLinks;
    private LinkExclusions WithoutLocalLinks => _withoutLocalLinks ??= new(_nonLocalLinks, _stubs, _pending, rehash: _rehash, retryProtection: _retryProtection, owned: _owned);

    /// <summary>
    /// True when the path is protected by anything other than a local link: a cloud link, a left-alone stub or an
    /// open user decision at or above it. Used only to let the cloud copy of a chosen rebuildable folder reach the
    /// planner although a local link sits at, above or under it.
    /// </summary>
    internal bool CoversExceptLocalLinks(string path) => WithoutLocalLinks.Covers(path);

    /// <summary>
    /// A chosen rebuildable's cloud-only folder delete ignores LOCAL links (at, above or under its path): it has no
    /// source and no from-path, so it never reads, follows or changes anything on the local side. Cloud links, stubs
    /// and pending decisions still protect it on every axis. The caller supplies the strict pair-boundary predicate
    /// (exclusions, nested pairs, product-owned areas) and restricts this to approved rebuildable cloud deletes.
    /// </summary>
    internal Func<string, bool> NarrowRebuildableDelete(Func<string, bool> include)
    {
        var other = WithoutLocalLinks;
        return path => include(path) && !other.Covers(path) && !other.Contains(path);
    }

    /// <summary>The standing pair-level summary notices (links, stubs) for what currently exists.</summary>
    internal IReadOnlyList<Notice> Summaries(string pairId)
    {
        var notices = new List<Notice>();
        if (_links.Count > 0)
            notices.Add(Summary(pairId, Rule, Reason, _links,
                $"{_links.Count} {(_links.Count == 1 ? "link" : "links")} not followed and not synced on either side"));
        if (_stubs.Count > 0)
            notices.Add(Summary(pairId, StubRule, StubReason, _stubs,
                $"online-only placeholders left alone ({_stubs.Count}); not read, replaced or synced on either side"));
        if (_rehash is { Count: > 0 }) notices.Add(_rehash.Summary(pairId));
        return notices;
    }

    /// <summary>True for a notice this class raises.</summary>
    internal static bool IsSummary(Notice notice) =>
        notice.Rule == Rule && notice.ReasonKey == Reason.Key ||
        notice.Rule == StubRule && notice.ReasonKey == StubReason.Key ||
        notice.Rule == RehashProtection.Rule && notice.ReasonKey == RehashProtection.Reason;

    private static Notice Summary(string pairId, int rule, NoticeReason reason, IEnumerable<string> paths, string head)
    {
        var top = paths.GroupBy(path => string.Join('/', path.Split('/').Take(2)), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(TopFolders).Select(group => $"{group.Key} ({group.Count()})");
        return new Notice(pairId, rule, NoticeKind.Info, string.Empty, head + "; top folders: " + string.Join(", ", top),
            reason.Key);
    }

    private static string Parent(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut < 0 ? string.Empty : path[..cut];
    }
}
