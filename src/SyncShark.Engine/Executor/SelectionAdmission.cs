using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Owns a selection branch's footprint and evidence across its single admitted retry.</summary>
internal sealed class SelectionAdmission(ExecutorContext context, ItemSync items, ILiveExecutionBoundary boundary,
    WorkSelection selection, string key, SelectionPeer? peer, LiveEffectScope selectedScope,
    Row row, LocalEvidence.DirtyProof? owned, ItemSync.LivePreparation preparation,
    ItemRetryCandidate? expected,
    Action<Row, Issue> retain, Action? release)
{
    internal Row Row { get; private set; } = row;
    internal LocalEvidence.DirtyProof? Owned { get; private set; } = owned;
    internal LiveCoverage Coverage { get; private set; } = null!;
    internal IReadOnlyList<LiveEffectScope> Footprint { get; private set; } = [];

    internal async Task<SyncOutcome> RunAsync(Guid attemptId, CancellationToken token)
    {
        await CaptureCoverageAsync(token).ConfigureAwait(false);
        try
        {
            return preparation.Action.Kind == ActionKind.Upload
                ? await items.Dispatch.AdmitUploadAsync(release, staged => ExecuteAsync(staged, attemptId, token), token)
                : await ExecuteAsync(null, attemptId, token);
        }
        catch (LiveAdmissionStaleException stale) when
            (stale.Reason == LiveAdmissionStaleReason.FootprintChanged && stale.HandoffRow is not null)
        {
            context.Evidence.Release(Row, Owned);
            Row = stale.HandoffRow;
            Owned = context.Evidence.Capture(Row);
        }
        // The old lease has unwound; a second refusal cannot enter this catch or adopt again.
        preparation = await items.Preparation.PrepareLiveAsync(Row, token,
            previous: preparation with { Retried = true }).ConfigureAwait(false);
        Row = preparation.Action.Row;
        Owned = context.Evidence.CaptureIfHashed(Row);
        await CaptureCoverageAsync(token).ConfigureAwait(false);
        return await ExecuteAsync(null, attemptId, token);
    }

    private async ValueTask CaptureCoverageAsync(CancellationToken token)
    {
        Footprint = LockKeys.ForSelectedBranch(LockKeys.ForLiveAction(preparation.Action, context.Pair, preparation.Labels),
            key, selection, selectedScope);
        Coverage = (await boundary.CaptureCoverageAsync(selection, Footprint, token).ConfigureAwait(false)) with { Peer = peer };
    }

    private Task<SyncOutcome> ExecuteAsync(Func<ValueTask>? staged, Guid attemptId, CancellationToken token) =>
        boundary.ExecuteAsync([new LiveExecutionRequest(selection, Coverage, Footprint)],
            async (execution, admittedToken) =>
            {
                if (staged is not null) await execution.ReleaseAdmissionAsync();
                return await items.Execution.RunAdmittedAsync(Row, admittedToken, execution: execution,
                    selection: selection with { Peer = peer }, retainUnfinished: retain,
                    preparedLabels: preparation.Labels, attemptId: attemptId, expectedAttempt: expected,
                    preparation: preparation, uploadStaged: staged);
            }, token);
}
