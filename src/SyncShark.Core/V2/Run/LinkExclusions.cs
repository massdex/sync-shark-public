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
    internal static readonly NoticeReason Reason = new(NoticeVocabulary.LinksExcluded,
        "links (junctions, symlinks) are not followed; they and the same paths on the other side are not synced");

    /// <summary>The stubs summary notice's stable reason key; the detail carries the count and top folders.</summary>
    internal static readonly NoticeReason StubReason = new(NoticeVocabulary.StubsLeftAlone,
        "online-only placeholders left alone; they and the same paths on the other side are not synced");

    private const int TopFolders = 5;
    private readonly HashSet<string> _links;
    private readonly HashSet<string> _nonLocalLinks;
    private readonly HashSet<string> _stubs;
    private readonly HashSet<string> _pending;
    private readonly RehashProtection? _rehash;
    private readonly Func<string, bool, bool>? _retryProtection;
    private readonly PendingPathIndex _paths;

    private LinkExclusions(IEnumerable<string> links, IEnumerable<string> stubs, IEnumerable<string>? pending = null,
        IEnumerable<string>? nonLocalLinks = null, RehashProtection? rehash = null,
        Func<string, bool, bool>? retryProtection = null, int heldCount = 0)
    {
        _links = new HashSet<string>(links, StringComparer.OrdinalIgnoreCase);
        _nonLocalLinks = new HashSet<string>(nonLocalLinks ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _stubs = new HashSet<string>(stubs, StringComparer.OrdinalIgnoreCase);
        _pending = new HashSet<string>(pending ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _rehash = rehash;
        _retryProtection = retryProtection;
        HeldCount = heldCount;
        _paths = new PendingPathIndex(_links, _stubs, _pending);
    }

    /// <summary>Verification holds counted after the run's intent classification; the combined summary shows both.</summary>
    internal int HeldCount { get; }

    /// <summary>A bounded consumer's immutable overflow answers; ordinary snapshots are shared.</summary>
    internal bool RequiresBatch => _rehash?.RequiresBatch == true;
    internal LinkExclusions ForPaths(IEnumerable<string> paths) => !RequiresBatch ? this :
        new(_links, _stubs, _pending, _nonLocalLinks, _rehash?.ForPaths(paths), _retryProtection);

    internal bool HasRehashProtection => _rehash is not null;

    internal LinkExclusions Freeze(SqliteStateStore.PlanningProtection snapshot,
        Func<IReadOnlyList<string>, IReadOnlyList<ProtectionMatch>>? read = null) =>
        new(_links, _stubs, _pending, _nonLocalLinks, _rehash?.Freeze(read ?? snapshot.Read),
            _retryProtection, HeldCount);

    /// <summary>Nothing left alone.</summary>
    internal static LinkExclusions None { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    // A genuine whole-root decision is durable authority, unlike an empty-path display summary.
    // Preserve it even while its retry sleeps or is not in this run's bounded due page.
    internal static bool HasRootDecisionHold(IStateStore store, string pairId) =>
        DecisionHolds.HasRootDecisionHold(store, pairId);

    /// <summary>
    /// The links (and, when <paramref name="stubsLeftAlone"/>, the stubs) both sides of the pair hold in the store.
    /// </summary>
    internal static LinkExclusions Of(IStateStore store, PairDef pair, bool stubsLeftAlone)
    {
        IEnumerable<string> Flagged(ItemFlags flag) =>
            store.LiveFlagged(pair.A.RootId, flag).Concat(store.LiveFlagged(pair.B.RootId, flag))
                .Where(item => flag != ItemFlags.Placeholder || item.Kind == ItemKind.File).Select(item => item.Path);
        var nonLocalLinks = new[] { pair.A, pair.B }
            .Where(root => !string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase))
            .SelectMany(root => store.LiveFlagged(root.RootId, ItemFlags.Link)).Select(item => item.Path);
        return new(Flagged(ItemFlags.Link), stubsLeftAlone ? Flagged(ItemFlags.Placeholder) : Array.Empty<string>(),
            pending: HoldPaths(store, pair),
            nonLocalLinks: nonLocalLinks, rehash: new RehashProtection(store, pair),
            heldCount: (int)Math.Min(int.MaxValue, (long)store.CountHeldIntents(pair.PairId) +
                (store is SqliteStateStore sqlite ? sqlite.CountDeferredPlanning(pair.PairId, verificationOnly: true) : 0)));
    }

    // Evidence observations may inspect links/stubs, but must not retire debt under a real hold.
    internal static LinkExclusions HoldsOf(IStateStore store, PairDef pair) =>
        new([], [], pending: HoldPaths(store, pair));

    private static IEnumerable<string> HoldPaths(IStateStore store, PairDef pair) =>
        store.LiveFlagged(pair.A.RootId, ItemFlags.Quarantined)
                .Concat(store.LiveFlagged(pair.B.RootId, ItemFlags.Quarantined))
                .SelectMany(item => new[] { item.Path, DropboxNameMapping.MatchPath(item) })
                .Concat(store.DefenderGrantHoldPaths(pair.A.RootId)).Concat(store.DefenderGrantHoldPaths(pair.B.RootId))
                .Concat(HasRootDecisionHold(store, pair.PairId) ? new[] { string.Empty } : Array.Empty<string>())
                .Concat(store.UnresolvedNotices(pair.PairId).Where(n => PlanNotices.HasPathScope(n) && n.Quarantine is not null).Select(n => n.Path));

    /// <summary>
    /// This run's protection: the state before the scan unioned with the state after it, so a link or stub that
    /// disappears in this run still protects its path (and the other side's items there) for the whole run.
    /// </summary>
    internal LinkExclusions With(LinkExclusions after) =>
        new(_links.Concat(after._links), _stubs.Concat(after._stubs), _pending.Concat(after._pending),
            _nonLocalLinks.Concat(after._nonLocalLinks), after._rehash ?? _rehash,
            after._retryProtection ?? _retryProtection, after.HeldCount);

    /// <summary>
    /// Decision 64: also leaves alone, on both sides, every path whose user-decision notice is open (a first-sync tie
    /// or needs-decision row): no copy, no keep-both, no delete until the user decides. No summary notice; the
    /// decision notices themselves are listed.
    /// </summary>
    internal LinkExclusions WithPending(IEnumerable<string> pending) =>
        new(_links, _stubs, _pending.Concat(pending), _nonLocalLinks, _rehash, _retryProtection, HeldCount);

    internal LinkExclusions WithRetryProtection(Func<string, bool, bool> protection) =>
        new(_links, _stubs, _pending, _nonLocalLinks, _rehash, protection, HeldCount);

    internal int Count => (int)Math.Min(int.MaxValue, _paths.Count + (_rehash?.Count ?? 0) + (_retryProtection is null ? 0 : 1));

    /// <summary>The recorded link paths (the baselines under them are forgotten, see SyncRun).</summary>
    internal IReadOnlyCollection<string> Links => _links;


    internal bool TouchesRehash(string path) => _rehash?.Touches(path, descendants: false) == true;

    internal bool Covers(string path)
    {
        if (_retryProtection?.Invoke(path, false) == true || _rehash?.Touches(path, descendants: false) == true) return true;
        return _paths.Covers(path);
    }

    /// <summary>True when a protected path lies strictly under the path (or the path is the whole pair).</summary>
    internal bool Contains(string path) => _retryProtection?.Invoke(path, true) == true ||
        _rehash?.Touches(path, descendants: true) == true ||
        _paths.Contains(path);

    /// <summary>Whether this exact path (or an ancestor) is a recorded link, with no other protection kind.</summary>
    internal bool CoversLink(string path) => Covers(_links, path);

    /// <summary>Whether a link lies strictly under this path (for a move or delete that would carry it).</summary>
    internal bool ContainsLink(string path) =>
        _paths.ContainsLink(path);

    /// <summary>Whether this exact path (or an ancestor) is a left-alone online-only placeholder.</summary>
    internal bool CoversStub(string path) => Covers(_stubs, path);

    /// <summary>Whether a left-alone placeholder lies strictly under this path.</summary>
    internal bool ContainsStub(string path) =>
        _paths.ContainsStub(path);

    /// <summary>Whether this exact path (or an ancestor) waits on quarantine, Defender or a pair-wide decision.</summary>
    internal bool CoversPending(string path) => Covers(_pending, path);

    /// <summary>Whether a pending-evidence path lies strictly under this path.</summary>
    internal bool ContainsPending(string path) =>
        _paths.ContainsPending(path);

    private static bool Covers(HashSet<string> paths, string path) => PairPath.CoveredBy(path, paths);

    /// <summary>Ordinary copy/source predicates protect every held path, including hash debt.</summary>
    internal Func<string, bool> Narrow(Func<string, bool> include) =>
        Count == 0 ? include : path => include(path) && !Covers(path);

    /// <summary>Inventory must still observe hash-debt paths so verification can retry.</summary>
    internal Func<string, bool> NarrowInventory(Func<string, bool> include) =>
        _paths.Count == 0 ? include : path => include(path) && !InventoryCovers(path);

    // Hash debt protects effects, not inventory: it must be observed again even without new journal records.
    private bool InventoryCovers(string path) =>
        PairPath.CoveredBy(path, _pending, rootPrefixCovers: true) ||
        PairPath.CoveredBy(path, _links, rootPrefixCovers: false) ||
        PairPath.CoveredBy(path, _stubs, rootPrefixCovers: false);

    /// <summary>The move/delete predicate narrowed: never at, under or containing a protected path.</summary>
    internal Func<string, bool> NarrowAction(Func<string, bool> include) =>
        Count == 0 ? include : path => include(path) && !Covers(path) && !Contains(path);

    // The same protection without the LOCAL links (cloud links, stubs and pending decisions only).
    private LinkExclusions? _withoutLocalLinks;
    private LinkExclusions WithoutLocalLinks => _withoutLocalLinks ??= new(_nonLocalLinks, _stubs, _pending,
        rehash: _rehash, retryProtection: _retryProtection, heldCount: HeldCount);

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
        // One combined summary for background verification debt: content verification (rehash or gap) and the
        // intents currently parked for it. It stays open while either count is above zero (M3).
        if (_rehash is { } rehash && (rehash.Count > 0 || HeldCount > 0))
            notices.Add(rehash.Summary(pairId, HeldCount));
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
