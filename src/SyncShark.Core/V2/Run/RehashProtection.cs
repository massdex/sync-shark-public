namespace Massdex.SyncShark.V2;

/// <summary>Database-backed protection: a million pending files do not become a million managed rows/paths.</summary>
internal sealed class RehashProtection
{
    internal const int Rule = NoticeVocabulary.RehashRule;
    internal const string Reason = NoticeVocabulary.RehashReason;
    private readonly IStateStore _store;
    private readonly PairDef _pair;
    private readonly ItemFlags _flag;
    internal long Count { get; }
    private readonly bool _gap;
    private readonly PathProtectionIndex? _a;
    private readonly PathProtectionIndex? _b;
    private readonly IReadOnlyDictionary<string, ProtectionMatch>? _batch;
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<ProtectionMatch>>? _frozenRead;

    internal RehashProtection Freeze(Func<IReadOnlyList<string>, IReadOnlyList<ProtectionMatch>> read) => new(this, read);

    private RehashProtection(RehashProtection source, Func<IReadOnlyList<string>, IReadOnlyList<ProtectionMatch>> read)
    {
        _store = source._store; _pair = source._pair; _flag = source._flag; _gap = source._gap;
        Count = Math.Max(1, source.Count);
        _frozenRead = read;
    }

    private RehashProtection(RehashProtection source, IReadOnlyDictionary<string, ProtectionMatch> batch)
    {
        _store = source._store; _pair = source._pair; _flag = source._flag; Count = source.Count;
        _a = source._a; _b = source._b; _gap = source._gap; _batch = batch;
        _frozenRead = source._frozenRead;
    }

    internal bool RequiresBatch => _a is null || _b is null;

    internal RehashProtection ForPaths(IEnumerable<string> paths)
    {
        if (_a is not null && _b is not null) return this;
        var input = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (input.Length > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
        if (_frozenRead is not null)
        {
            var frozen = _frozenRead(input);
            return new(this, input.Select((path, n) => (path, match: frozen[n]))
                .ToDictionary(entry => entry.path, entry => entry.match, StringComparer.OrdinalIgnoreCase));
        }
        var left = _a is null ? _store.ProtectionBatch(_pair.A.RootId, _flag, input) : null;
        var right = _b is null ? _store.ProtectionBatch(_pair.B.RootId, _flag, input) : null;
        var matches = new Dictionary<string, ProtectionMatch>(StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < input.Length; n++)
        {
            var path = input[n];
            var a = left?[n] ?? new ProtectionMatch(_a!.Touches(path, false), _a.Touches(path, true));
            var b = right?[n] ?? new ProtectionMatch(_b!.Touches(path, false), _b.Touches(path, true));
            matches[path] = new(a.Covered || b.Covered, a.Contains || b.Contains);
        }
        return new(this, matches);
    }

    internal RehashProtection(IStateStore store, PairDef pair, ItemFlags flag = ItemFlags.NeedsRehash)
    {
        _store = store; _pair = pair; _flag = flag;
        _a = store.ProtectionIndex(pair.A.RootId, flag);
        _b = store.ProtectionIndex(pair.B.RootId, flag);
        _gap = flag == ItemFlags.NeedsRehash && store is ILocalGapStore gaps &&
            (gaps.HasGapProtection(pair.A.RootId) || gaps.HasGapProtection(pair.B.RootId));
        Count = store.LiveFlaggedSummary(pair.A.RootId, flag, 0).Count +
            store.LiveFlaggedSummary(pair.B.RootId, flag, 0).Count;
        // Count is also the consumer's presence gate; gap discovery is deliberately not counted by a root scan.
        if (_gap) Count = Math.Max(1, Count);
    }

    internal bool Touches(string path, bool descendants)
    {
        if (Count == 0) return false;
        if (_a is not null && _b is not null) return _a.Touches(path, descendants) || _b.Touches(path, descendants);
        if (_batch is not null && _batch.TryGetValue(path, out var match))
            return descendants ? match.Contains : match.Covered;
        // Exceptional callers may omit preparation. Resolve only this path, rather than failing
        // the run or silently admitting a protected action. Normal consumers stay bounded/batched;
        // do not retain misses in an unbounded cache.
        return ForPaths([path]).Touches(path, descendants);
    }

    /// <summary>
    /// The one background-verification summary. It carries both counts: files awaiting content verification (rehash
    /// or gap debt) and changes parked waiting for that verification. It is display-only (empty path, never a hold)
    /// and the run closes it only when both counts are zero.
    /// </summary>
    internal Notice Summary(string pairId, int heldCount = 0)
    {
        var waiting = heldCount > 0
            ? $"{heldCount} {(heldCount == 1 ? "change is" : "changes are")} waiting for background verification; "
            : string.Empty;
        if (_gap) return new Notice(pairId, Rule, NoticeKind.Info, "",
            "Local journal verification is pending; unvisited and unverified files stay protected while other files continue. " +
            waiting +
            string.Join("; ", new[] { _pair.A.RootId, _pair.B.RootId }.Distinct()
                .Where(id => ((ILocalGapStore)_store).HasGapProtection(id))
                .Select(id => ((ILocalGapStore)_store).GapProgress(id))) +
            ". Options: when the journal is readable, leave the computer idle on AC power to verify automatically; " +
            "use Sync Now to admit verified results or retry failed items after correcting the reported cause. " +
            "If the journal is unavailable, restore/enable it with a Windows administrator before retrying. " +
            "Show remaining lists the changes parked for this verification; they are not marked synced.", Reason);
        var examples = _store.RehashReasons(_pair.A.RootId, 5).ToList();
        examples.AddRange(_store.RehashReasons(_pair.B.RootId, 5 - examples.Count));
        return new Notice(pairId, Rule, NoticeKind.Info, "",
            $"{Count} files awaiting content verification; {waiting}kept safe on both sides while other files continue; " +
            "automatically retried next sync. Show remaining lists the changes parked for this verification; they are not " +
            "marked synced. " + string.Join("; ", examples), Reason);
    }
}
