namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private const string LocalCatchUpFallbackNoticeKey = "local-walk-catch-up";

    private void RecordLocalCatchUpFallback(PairDef pair, RootDef root) =>
        _store.AddNotices([new Notice(pair.PairId, 31, NoticeKind.Info, root.RootId,
            "Local change journal catch-up was unavailable. Retrying a fresh inventory; no stale paused snapshot will be planned.",
            LocalCatchUpFallbackNoticeKey)]);

    private void CloseLocalCatchUpFallback(PairDef pair, RootDef root)
    {
        foreach (var notice in _store.UnresolvedNotices(pair.PairId))
            if (notice.ReasonKey == LocalCatchUpFallbackNoticeKey && notice.NoticeId is not null &&
                string.Equals(notice.Path, root.RootId, StringComparison.Ordinal))
                _store.CloseNotice(notice.NoticeId, "resolved");
    }

    private void CloseLocalScanFallbacks(PairDef pair, RootDef root)
    {
        CloseHydrationFallback(pair, root);
        CloseLocalCatchUpFallback(pair, root);
    }

    private static Delta WithHydrationFailures(Delta delta, IReadOnlyDictionary<string, string> hydrationFailures)
    {
        if (hydrationFailures.Count > 0)
        {
            var failures = delta.IncompleteReasons.ToDictionary(entry => entry.Key, entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);
            foreach (var failure in hydrationFailures) failures[failure.Key] = failure.Value;
            delta = delta with
            {
                IncompleteSubtrees = delta.IncompleteSubtrees.Concat(hydrationFailures.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                IncompleteReasons = failures
            };
        }
        return delta;
    }

    internal sealed record SideScan(long Gen, string CursorAfter, bool Complete, bool Full,
        IReadOnlyList<string> ChangePaths, IReadOnlyList<string> ChangeObjectIds,
        IReadOnlyDictionary<string, DateTimeOffset?> Deleted, IReadOnlyList<string> PreviousPaths)
    {
        internal HashSet<string> ExactCreationObserved { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public bool JournalBacklog { get; init; }
        internal bool AllItemsChanged { get; init; }
        internal bool WalkObserved { get; init; } // Actual snapshot, not merely full planning coverage.
        internal IReadOnlyList<Change>? UserChanges { get; set; } = [];
        // Full also selects stored scope for hydrated deltas; only a returned full snapshot can establish this.
        public bool ScopedFullCoverage { get; init; }
        public IReadOnlyList<string> IncompleteSubtrees { get; init; } = [];
        internal Exception? InventoryFailure { get; init; }
        public IReadOnlyDictionary<string, string> IncompleteReasons { get; init; } = new Dictionary<string, string>();
        public InventoryCoverage? ObservedCoverage { get; init; }
        public long RehashThrough { get; set; }
        internal Dictionary<string, long> PlanningObservationVersions { get; } = new(StringComparer.Ordinal);
        public IReadOnlySet<string> ReleasedDeletionEvidence { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, string> PendingDeletionEvidence { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // This delta run observed the side's durable whole-root read debt and may retire it. Only such
        // a run expands planning to every stored row; a retry still sleeping, or one whose root read
        // failed again, must not pay for a whole-pair load.
        public bool RootDebtSettles { get; init; }
    }

    internal sealed record BothScan(SideScan A, SideScan B)
    {
        internal Func<Side, string, bool>? Unknown { get; init; }
        internal void PendingChanges(RunScope scope, IEnumerable<string> a, IEnumerable<string> b)
        {
            if (A.UserChanges is { } userA && B.UserChanges is { } userB)
            {
                scope.UserChanges(userA, userB);
                A.UserChanges = B.UserChanges = null; // Retain scope sets across reloads, release delta payloads.
            }
            scope.PendingChanges(a, b, A.AllItemsChanged, B.AllItemsChanged);
        }

        public bool JournalBacklog => A.JournalBacklog || B.JournalBacklog;
        public bool AnyFull => A.Full || B.Full;
        public bool HasChanges => A.ChangePaths.Count > 0 || B.ChangePaths.Count > 0;

        public IReadOnlyList<string> Touched(IEnumerable<string> extra) => A.ChangePaths
            .Concat(B.ChangePaths).Concat(A.PreviousPaths).Concat(B.PreviousPaths).Concat(extra)
            .Distinct(StringComparer.Ordinal).ToArray();
    }

}
