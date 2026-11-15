using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Settles an unknown Dropbox move from its single by-ID observation.</summary>
internal sealed class UnknownMoveSettlement(ExecutorContext context, ItemSync items)
{
    internal async Task<(SyncOutcome Outcome, bool Resolved)> SettleAsync(Issue issue, Action close,
        CancellationToken ct)
    {
        var held = (new SyncOutcome(ActionKind.None, NoneReason.Held, null, IssueKind.Failure, false, issue.Cause), false);
        if (await items.Renames.ObserveUnknownMoveAsync(issue, ct) is not { } read) return held;
        var (decision, observed) = read;
        var bound = context.Store.SyncedById(context.Pair.PairId, Side.Dropbox, issue.Subject.Move!.ExpectedId);
        if (!UnknownMoveCheck.Settles(decision, bound is not null)) return held;
        try
        {
            var settled = await items.Renames.SettleUnknownMoveAsync(issue, observed!.Value.Location.Entry,
                bound, decision == UnknownMoveCheck.Outcome.RemainingHop, close, ct);
            return (settled.Outcome, !settled.Kept);
        }
        // The rename owner already decided whether to restore the responsibility.
        catch (Exception error) when (ItemFailures.IsItemError(error))
        {
            context.Events(RunEvent.Failed(error, path: issue.Path));
            return held;
        }
    }
}
