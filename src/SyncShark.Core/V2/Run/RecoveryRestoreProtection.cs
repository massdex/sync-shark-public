namespace Massdex.SyncShark.V2;

/// <summary>
/// What a Recovery-restore notice is allowed to hold. A pending notice protects a path only after the restore has a
/// frozen intent: bytes may already have been written and a later run must verify them. An action-less failure
/// (CONFLICT, PROTECTED, NO-VERSION, or FAILED before a write) is informational and holds nothing (finding 2).
/// </summary>
internal static class RecoveryRestoreHold
{
    internal const string PendingReason = "recovery-restore-pending";
    internal const string ReadyReason = "recovery-restore-ready";

    /// <summary>The restore's own create-only intent; its id is the durable rrs id and it cannot claim a baseline.</summary>
    internal static bool IsFrozenIntent(SyncAction action) =>
        action.ActionId.StartsWith("rrs", StringComparison.Ordinal) && action.SuppressReconciledBaseline;

    internal static bool HasFrozenIntent(ConflictNoticeOperation? operation) =>
        operation?.Actions.Any(IsFrozenIntent) == true;

    internal static bool Holds(Notice notice) =>
        notice.ReasonKey == PendingReason && HasFrozenIntent(notice.ConflictOperation);
}

/// <summary>
/// One run-scoped snapshot of pending frozen restore holds. The gate is a single indexed EXISTS per run, so a pair
/// with no held restore pays no per-item callback and no per-item SQL. Held path keys preload within the same bounds
/// as <see cref="RetryProtection"/>; above them, lookups use the indexed predicate through a bounded cache.
/// </summary>
internal sealed class RecoveryRestoreProtection
{
    internal const int PathLimit = RetryProtection.PathLimit;
    internal const int CharacterLimit = RetryProtection.CharacterLimit;
    internal const int CacheEntries = 4096;
    internal const int CacheCharacters = 262_144;

    private readonly PathProtectionIndex? _snapshot;
    private readonly Func<string, bool, bool>? _fallback;

    private RecoveryRestoreProtection(bool active, PathProtectionIndex? snapshot, Func<string, bool, bool>? fallback)
    {
        Active = active;
        _snapshot = snapshot;
        _fallback = fallback;
    }

    /// <summary>No pending frozen intent: every lookup is in-memory and answers false.</summary>
    internal static RecoveryRestoreProtection None { get; } = new(false, null, null);

    /// <summary>True when this run has at least one pending frozen restore intent.</summary>
    internal bool Active { get; }

    /// <summary>Held paths materialized in the snapshot; zero when the bounded fallback is in use.</summary>
    internal int Count => _snapshot?.Count ?? 0;

    /// <summary>True when held paths fit the bounded preload; false means callers must bind a fallback.</summary>
    internal bool SnapshotComplete => _snapshot is not null;

    /// <summary>
    /// Self/ancestor protection, plus descendants when requested. Touches(true) is descendants-only, so the union
    /// keeps the store predicate's contract: descendants requests addition, never substitution.
    /// </summary>
    internal bool Covers(string path, bool descendants) =>
        _snapshot is not null
            ? _snapshot.Touches(path, false) || descendants && _snapshot.Touches(path, true)
            : _fallback?.Invoke(path, descendants) == true;

    /// <summary>
    /// Protection for one planning scope. The normal bounded snapshot is in-memory. Beyond the cap the fallback reads
    /// through the caller's planning transaction, never the live writer, so one scope keeps one consistent view; its
    /// bounded cache is scoped to that same snapshot.
    /// </summary>
    internal Func<string, bool, bool> Bind(SqliteStateStore.PlanningProtection planning, string pairId) =>
        _snapshot is not null
            ? Covers
            : Cached((path, descendants) => planning.RecoveryRestoreProtected(pairId, path, descendants));

    internal static RecoveryRestoreProtection Create(IStateStore store, string pairId, Action? onFallback = null,
        int pathLimit = PathLimit, int characterLimit = CharacterLimit)
    {
        // One indexed EXISTS: the whole per-item cost disappears when no restore may have written bytes.
        if (!store.HasPendingRestoreHolds(pairId)) return None;
        var snapshot = PathProtectionIndex.TryCreate(store.ReadPendingRestoreHoldPaths(pairId), pathLimit, characterLimit);
        if (snapshot is null)
        {
            onFallback?.Invoke();
            return new RecoveryRestoreProtection(true, null,
                Cached((path, descendants) => store.RecoveryRestoreProtected(pairId, path, descendants)));
        }
        return new RecoveryRestoreProtection(true, snapshot, null);
    }

    private static Func<string, bool, bool> Cached(Func<string, bool, bool> lookup)
    {
        // Same bounded consumer cache as RetryProtection.CachedFallback: a pair beyond the snapshot cap keeps the
        // indexed predicate's semantics without materializing or retaining an unbounded path set.
        var cache = new Dictionary<(string Path, bool Descendants), bool>();
        long characters = 0;
        var gate = new object();
        return (path, descendants) =>
        {
            lock (gate)
            {
                if (cache.TryGetValue((path, descendants), out var answer)) return answer;
                answer = lookup(path, descendants);
                if (cache.Count >= CacheEntries || characters + path.Length > CacheCharacters)
                {
                    cache.Clear();
                    characters = 0;
                }
                if (path.Length <= CacheCharacters)
                {
                    cache[(path, descendants)] = answer;
                    characters += path.Length;
                }
                return answer;
            }
        };
    }
}
