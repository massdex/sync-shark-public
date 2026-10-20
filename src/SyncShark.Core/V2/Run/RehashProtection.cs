namespace Massdex.SyncShark.V2;

/// <summary>Database-backed protection: a million pending files do not become a million managed rows/paths.</summary>
internal sealed class RehashProtection
{
    internal const int Rule = 38;
    internal const string Reason = "local-rehash-pending";
    private readonly IStateStore _store;
    private readonly PairDef _pair;
    internal long Count { get; }
    private readonly PathProtectionIndex? _a;
    private readonly PathProtectionIndex? _b;
    private readonly IReadOnlyDictionary<string, ProtectionMatch>? _batch;

    private RehashProtection(RehashProtection source, IReadOnlyDictionary<string, ProtectionMatch> batch)
    {
        _store = source._store; _pair = source._pair; Count = source.Count;
        _a = source._a; _b = source._b; _batch = batch;
    }

    internal bool RequiresBatch => _a is null || _b is null;

    internal RehashProtection ForPaths(IEnumerable<string> paths)
    {
        if (_a is not null && _b is not null) return this;
        var input = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (input.Length > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
        var left = _a is null ? _store.ProtectionBatch(_pair.A.RootId, ItemFlags.NeedsRehash, input) : null;
        var right = _b is null ? _store.ProtectionBatch(_pair.B.RootId, ItemFlags.NeedsRehash, input) : null;
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

    internal RehashProtection(IStateStore store, PairDef pair)
    {
        _store = store; _pair = pair;
        _a = store.ProtectionIndex(pair.A.RootId, ItemFlags.NeedsRehash);
        _b = store.ProtectionIndex(pair.B.RootId, ItemFlags.NeedsRehash);
        Count = store.LiveFlaggedSummary(pair.A.RootId, ItemFlags.NeedsRehash, 0).Count +
            store.LiveFlaggedSummary(pair.B.RootId, ItemFlags.NeedsRehash, 0).Count;
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

    internal Notice Summary(string pairId)
    {
        var examples = _store.RehashReasons(_pair.A.RootId, 5).ToList();
        examples.AddRange(_store.RehashReasons(_pair.B.RootId, 5 - examples.Count));
        return new Notice(pairId, Rule, NoticeKind.Info, "",
            $"{Count} files awaiting content verification; kept safe on both sides while other files continue; " +
            "automatically retried next sync. " + string.Join("; ", examples), Reason);
    }
}
