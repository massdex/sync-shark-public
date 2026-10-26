namespace Massdex.SyncShark.V2;

/// <summary>Bounded current-state reread and debt acknowledgement for one reconciliation lease.</summary>
internal sealed record ReconcileBatch(string Session, IReadOnlyList<string> Paths,
    IReadOnlySet<string> ObjectIdsA, IReadOnlySet<string> ObjectIdsB, Func<Side, string, bool> Remaining)
{
    internal SqliteStateStore.ReconciliationGapCoverage? GapCoverage { get; set; }
    internal IReadOnlyList<ItemState> RowsA { get; init; } = [];
    internal IReadOnlyList<ItemState> RowsB { get; init; } = [];
    internal Dictionary<string, string> Dispositions { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> DeferredFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal bool Contains(string path) => Paths.Any(scope => PairPath.IsSelfOrUnder(path, scope));

    internal bool Contains(SyncAction action) => Contains(action.Path) ||
        action.FromPath is { } from && Contains(from) ||
        action.Source is { } source && (Contains(source.Path) ||
            (action.Target == Side.A ? ObjectIdsB : ObjectIdsA).Contains(source.ObjectId)) ||
        action.ExpectedTarget is { } target &&
            (action.Target == Side.A ? ObjectIdsA : ObjectIdsB).Contains(target.ObjectId);
}
