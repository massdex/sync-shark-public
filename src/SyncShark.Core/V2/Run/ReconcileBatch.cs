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
    internal static readonly AsyncLocal<ReconcileBatch?> Current = new();
    internal static readonly AsyncLocal<ReconcileBatch?> EffectCurrent = new();
    private long _effectReadBytes, _effectWrittenBytes;
    internal long EffectReadBytes => Interlocked.Read(ref _effectReadBytes);
    internal long EffectWrittenBytes => Interlocked.Read(ref _effectWrittenBytes);
    internal void RecordEffectRead(int bytes) => Interlocked.Add(ref _effectReadBytes, bytes);
    internal void RecordEffectWrite(int bytes) => Interlocked.Add(ref _effectWrittenBytes, bytes);
    internal static IDisposable MeasureEffects()
    {
        var previous = EffectCurrent.Value;
        EffectCurrent.Value = Current.Value;
        return new EffectScope(previous);
    }
    private sealed class EffectScope(ReconcileBatch? previous) : IDisposable
    {
        public void Dispose() => EffectCurrent.Value = previous;
    }

    private long _decisionBytes;
    internal long DecisionByteLimit { get; set; }
    internal long DecisionBytes => Interlocked.Read(ref _decisionBytes);
    internal IDisposable MeasureDecisions(long limit)
    {
        DecisionByteLimit = limit;
        var previous = Current.Value;
        Current.Value = this;
        return new DecisionScope(previous);
    }
    private sealed class DecisionScope(ReconcileBatch? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
    internal long ProofBytes { get; set; }

    internal bool AdmitDecisionHash(ItemState item)
    {
        var bytes = Math.Max(default(long), item.Size);
        while (true)
        {
            var used = Interlocked.Read(ref _decisionBytes);
            if (bytes > DecisionByteLimit - used)
            {
                RunTrace.Emit($"reconciliation hash deferred: path={item.Path} bytes={bytes} remaining={DecisionByteLimit - used}");
                return false;
            }
            if (Interlocked.CompareExchange(ref _decisionBytes, used + bytes, used) == used) return true;
        }
    }

    internal bool OwnsDecisionPath(string path) => Paths.Contains(path, StringComparer.OrdinalIgnoreCase) ||
        AliasPaths.Contains(path);
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
