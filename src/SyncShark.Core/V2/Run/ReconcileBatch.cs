namespace Massdex.SyncShark.V2;

/// <summary>Bounded current-state reread and debt acknowledgement for one reconciliation lease.</summary>
internal sealed record ReconcileBatch(string Session, IReadOnlyList<string> Paths,
    IReadOnlySet<string> ObjectIdsA, IReadOnlySet<string> ObjectIdsB, Func<Side, string, bool> Remaining)
{
    internal SqliteStateStore.ReconciliationGapCoverage? GapCoverage { get; set; }
    internal IReadOnlyList<string> MovingFolders { get; init; } = [];
    internal NoticeRepair? MovingFolder { get; init; }
    internal int MovingItems { get; init; }
    internal bool MovingUnitTooLarge { get; init; }
    internal IReadOnlySet<(Side Side, string ObjectId)> MovingIdentities { get; init; } = new HashSet<(Side, string)>();
    internal bool MovingUnit => MovingFolders.Count > 0;
    internal bool OwnsMovingPath(string path) => MovingFolders.Any(folder => PairPath.IsSelfOrUnder(path, folder));
    internal bool MovingUnitObserved { get; set; }
    internal bool ObservedMovingPath(string path) => MovingUnitObserved && OwnsMovingPath(path);
    internal IReadOnlyList<ItemState> RowsA { get; init; } = [];
    internal IReadOnlyList<ItemState> RowsB { get; init; } = [];
    internal Dictionary<string, string> Dispositions { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> DeferredFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly AsyncLocal<ReconcileBatch?> Current = new();
    private ReconciliationBudget Budget { get; } = new(Paths);
    internal long EffectReadBytes => Budget.EffectReadBytes;
    internal long EffectWrittenBytes => Budget.EffectWrittenBytes;
    internal static IDisposable MeasureEffects() => ReconciliationBudgetContext.MeasureEffects();
    internal long DecisionByteLimit { get => Budget.DecisionByteLimit; set => Budget.DecisionByteLimit = value; }
    internal long DecisionBytes => Budget.DecisionBytes;
    internal IDisposable MeasureDecisions(long limit)
    {
        DecisionByteLimit = limit;
        var previous = Current.Value;
        Current.Value = this;
        return new DecisionScope(previous, ReconciliationBudgetContext.MeasureDecisions(Budget));
    }
    private sealed class DecisionScope(ReconcileBatch? previous, IDisposable budget) : IDisposable
    {
        public void Dispose() { budget.Dispose(); Current.Value = previous; }
    }
    internal long ProofBytes { get; set; }

    internal bool AdmitDecisionHash(ItemState item) => Budget.AdmitDecisionHash(item);
    internal int ProofFiles { get; set; }
    internal TimeSpan AdmissionWait { get; set; }
    internal long IntakeRetired { get; set; }
    internal HashSet<string> AliasPaths => Budget.Aliases;
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
