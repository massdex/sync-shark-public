namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal sealed record SideScan(long Gen, string CursorAfter, bool Complete, bool Full,
        IReadOnlyList<string> ChangePaths, IReadOnlyList<string> ChangeObjectIds,
        IReadOnlyDictionary<string, DateTimeOffset?> Deleted, IReadOnlyList<string> PreviousPaths)
    {
        internal bool AllItemsChanged { get; init; }
        internal IReadOnlyList<Change>? UserChanges { get; set; } = [];
        // Full also selects stored scope for hydrated deltas; only a returned full snapshot can establish this.
        public bool ScopedFullCoverage { get; init; }
        public IReadOnlyList<string> IncompleteSubtrees { get; init; } = [];
        public IReadOnlyDictionary<string, string> IncompleteReasons { get; init; } = new Dictionary<string, string>();
        public InventoryCoverage? ObservedCoverage { get; init; }
        public long RehashThrough { get; init; }
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
        internal void PendingChanges(RunScope scope, IEnumerable<string> a, IEnumerable<string> b)
        {
            if (A.UserChanges is { } userA && B.UserChanges is { } userB)
            {
                scope.UserChanges(userA, userB);
                A.UserChanges = B.UserChanges = null; // Retain scope sets across reloads, release delta payloads.
            }
            scope.PendingChanges(a, b, A.AllItemsChanged, B.AllItemsChanged);
        }

        public bool AnyFull => A.Full || B.Full;
        public bool HasChanges => A.ChangePaths.Count > 0 || B.ChangePaths.Count > 0;

        public IReadOnlyList<string> Touched(IEnumerable<string> extra) => A.ChangePaths
            .Concat(B.ChangePaths).Concat(A.PreviousPaths).Concat(B.PreviousPaths).Concat(extra)
            .Distinct(StringComparer.Ordinal).ToArray();
    }

}
