namespace Massdex.SyncShark.V2;

internal sealed partial class ActionRunner
{
    // The result is durable before it is reported; cancellation never abandons it (the effect may have happened).
    internal async Task<ActionResult> CommitAsync(SyncAction action, ActionResult result)
    {
        using var timing = RunInstrumentation.Current?.Measure(RunTimingStage.ResultCommit);
        result = ResultEvidence.Normalize(action, result);
        if (action.ConflictTransitionKey is not null && !action.SuppressReconciledBaseline &&
            result.Outcome is Outcome.Skipped or Outcome.Refused)
            result = result with { Outcome = Outcome.Failed };
        await _committer.CommitAsync(action, result).ConfigureAwait(false);
        RunTrace.EffectResult(action, result);
        return result;
    }
}
