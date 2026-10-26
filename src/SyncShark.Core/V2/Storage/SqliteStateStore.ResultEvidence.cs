using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private static IReadOnlyList<(SyncAction Action, ActionResult Result)> NormalizeResults(
        IReadOnlyList<(SyncAction Action, ActionResult Result)> results) =>
        results.Select(row => (row.Action, ResultEvidence.Normalize(row.Action, row.Result))).ToArray();

    private void SaveResultFailure(SqliteTransaction transaction, long pairNo, string pairId,
        SyncAction action, SyncAction retainedAction, ActionResult result, long now)
    {
        SaveResultFailureCause(transaction, pairNo, retainedAction, result);
        if (result.Outcome == Outcome.Failed && (ResultEvidence.CommitOnly(action) ||
            result.Reason == ExecutionReasons.BaselineUnconfirmed.Detail))
            _notices.Add(transaction, pairNo, FailureRetryOwner.RetryNotice(pairId, action.Path,
                result.Reason ?? ExecutionReasons.BaselineUnconfirmed.Detail), now);
    }
}
