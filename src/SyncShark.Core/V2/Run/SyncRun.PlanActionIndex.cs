namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // Both ordinary and held-evidence execution use this admission boundary. A collision
    // is item debt, not permission to execute a second effect with the same durable key.
    private Dictionary<string, SyncAction> IndexPlanActions(ref Plan plan, PairDef pair, Tally tally)
    {
        var byId = new Dictionary<string, SyncAction>(plan.Actions.Count, StringComparer.Ordinal);
        var duplicate = false;
        foreach (var action in plan.Actions)
        {
            if (byId.TryAdd(action.ActionId, action)) continue;
            duplicate = true;
            tally.DuplicateAction(action);
            SaveFailedRetry(pair, action.Path, DuplicateActionReason, _store.SyncRetryAt(pair.PairId, action.Path));
        }
        // Allocate another action array only for a malformed plan; the normal million-item
        // plan needs no additional list beyond the index it already used.
        if (duplicate) plan = plan with { Actions = byId.Values.ToArray() };
        return byId;
    }

    private static string DuplicateActionReason => MessageCatalog.Default.Message("duplicate-plan-action");

    internal sealed partial class Tally
    {
        internal HashSet<string> DuplicatePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal void DuplicateAction(SyncAction action)
        {
            lock (_actionGate)
            {
                DuplicatePaths.Add(action.Path);
                // A duplicate is a separate failed item, not a result for the first action.
                // In particular, its failure cannot consume the first action's progress slot.
                Failed++;
                PlannedDone++;
                Interlocked.Increment(ref _total);
                var kind = _kinds.GetValueOrDefault(action.Kind);
                _kinds[action.Kind] = (kind.Done + 1, kind.Total + 1);
                if (Failures.Count < _failureSampleLimit)
                    Failures.Add(new(action.ActionId, Outcome.Failed, null, null, null, DuplicateActionReason));
                RunInstrumentation.Current?.RecordFailure(DuplicateActionReason);
            }
        }
    }
}
