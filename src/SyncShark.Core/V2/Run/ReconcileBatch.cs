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
    internal long ProofBytes { get; set; }
    internal int ProofFiles { get; set; }
    internal TimeSpan AdmissionWait { get; set; }
    internal long IntakeRetired { get; set; }
    internal HashSet<string> AliasPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> ObservedAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal bool UnobservedAlias(string path) => AliasPaths.Contains(path) && !ObservedAliases.Contains(path);
    internal bool Contains(string path) => Paths.Any(scope => PairPath.IsSelfOrUnder(path, scope));

    internal bool Contains(SyncAction action) => Contains(action.Path) ||
        action.FromPath is { } from && Contains(from) ||
        action.Source is { } source && (Contains(source.Path) ||
            (action.Target == Side.A ? ObjectIdsB : ObjectIdsA).Contains(source.ObjectId)) ||
        action.ExpectedTarget is { } target &&
            (action.Target == Side.A ? ObjectIdsA : ObjectIdsB).Contains(target.ObjectId);
}
