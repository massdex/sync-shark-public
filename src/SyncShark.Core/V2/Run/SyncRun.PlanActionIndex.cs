namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // Both execution lanes admit once per id. Rejected effects become durable path debt;
    // a later real failure at the same path replaces that explanation, not the attempt.
    private Dictionary<string, SyncAction> IndexPlanActions(ref Plan plan, PairDef pair, Tally tally)
    {
        var byId = new Dictionary<string, SyncAction>(plan.Actions.Count, StringComparer.Ordinal);
        List<SyncAction>? kept = null;
        for (var i = 0; i < plan.Actions.Count; i++)
        {
            var action = plan.Actions[i];
            if (byId.TryAdd(action.ActionId, action))
            {
                kept?.Add(action);
                continue;
            }
            // Preserve plan order explicitly, allocating only when the plan is malformed.
            kept ??= plan.Actions.Take(i).ToList();
            if (!tally.DuplicateAction(action)) continue;
            var first = byId[action.ActionId];
            var successor = PairPath.Same(first.Path, action.Path) && first.ConflictTransitionKey is not null ? first : action;
            SaveFailedRetry(pair, action.Path, DuplicateActionReason, _store.SyncRetryAt(pair.PairId, action.Path), successor);
        }
        if (kept is not null) plan = plan with { Actions = kept };
        return byId;
    }

    private static string DuplicateActionReason => MessageCatalog.Default.Message("duplicate-plan-action");

    internal sealed partial class Tally
    {
        internal HashSet<string> DuplicatePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, ActionResult> DuplicateEffectFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

        // A real failure still owns its cause, throttle deadline and reviewed authority.
        internal bool CompleteRetry(string path) => !DuplicatePaths.Contains(path) || DuplicateEffectFailures.ContainsKey(path);

        private void RecordDuplicateEffectFailure(SyncAction? action, ActionResult result)
        {
            if (action is null || result.Outcome is not (Outcome.Failed or Outcome.Uncertain)) return;
            lock (_actionGate)
                if (DuplicatePaths.Contains(action.Path)) DuplicateEffectFailures[action.Path] = result;
        }

        internal bool DuplicateAction(SyncAction action)
        {
            lock (_actionGate)
            {
                var firstAtPath = DuplicatePaths.Add(action.Path);
                // Each extra effect fails, but one path accrues only one attempt per run.
                Failed++;
                PlannedDone++;
                Interlocked.Increment(ref _total);
                var kind = _kinds.GetValueOrDefault(action.Kind);
                _kinds[action.Kind] = (kind.Done + 1, kind.Total + 1);
                if (Failures.Count < _failureSampleLimit)
                    Failures.Add(new(action.ActionId, Outcome.Failed, null, null, null, DuplicateActionReason)
                        { RejectedDuplicatePath = action.Path });
                RunInstrumentation.Current?.RecordFailure(DuplicateActionReason);
                return firstAtPath;
            }
        }
    }
}
