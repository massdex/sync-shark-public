namespace Massdex.SyncShark.V2;

/// <summary>Shared ambient contract. Independent LocalGapWorker and LocalIndexingRuns roots clear both slots.</summary>
internal static class ReconciliationBudgetContext
{
    internal static readonly AsyncLocal<IReconciliationBudget?> Current = new();
    internal static readonly AsyncLocal<IReconciliationBudget?> EffectCurrent = new();
    internal static IDisposable MeasureDecisions(IReconciliationBudget budget)
    {
        var previous = Current.Value;
        Current.Value = budget;
        return new Scope(Current, previous);
    }
    internal static IDisposable MeasureEffects()
    {
        var previous = EffectCurrent.Value;
        EffectCurrent.Value = Current.Value;
        return new Scope(EffectCurrent, previous);
    }
    private sealed class Scope(AsyncLocal<IReconciliationBudget?> slot, IReconciliationBudget? previous) : IDisposable
    {
        public void Dispose() => slot.Value = previous;
    }
}
