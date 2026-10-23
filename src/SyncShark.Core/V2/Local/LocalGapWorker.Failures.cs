namespace Massdex.SyncShark.V2;

internal sealed partial class LocalGapWorker
{
    internal bool IsStateDamage(Exception error) =>
        FailureRetryOwner.ScopeOf(error, CancellationToken.None, _isStateDamage) == FailureRetryOwner.FailureScope.State;

    internal void RecordFailure(RootDef root, Exception error)
    {
        if (store is not IStateStore state) return;
        try { state.RecordIndexFailure(root.RootId, error.Message); }
        catch (Exception recordingError) when (!IsStateDamage(recordingError))
        {
            // The root debt remains protected. The shared run failure owner reports the unavailable
            // persistence path; never manufacture another indexing retry counter or notice family.
            RunOutput.Observe(recordingError);
            try
            {
                foreach (var pair in state.Pairs().Where(pair => pair.A.RootId == root.RootId || pair.B.RootId == root.RootId))
                    FailureRetryOwner.RecordRunFailure(state, pair.PairId, recordingError, DateTimeOffset.UtcNow, _isStateDamage);
            }
            catch (Exception reportingError) when (!IsStateDamage(reportingError)) { RunOutput.Observe(reportingError); }
        }
    }

    private void ReportFailure(RootDef root, bool discovery, CancellationTokenSource stop, Exception error)
    {
        stop.Cancel();
        using var reporting = StoreAdmission.WithCancellation(CancellationToken.None);
        RecordFailure(root, error);
        RunTrace.Emit($"local gap worker failed ({root.RootId}): {error}");
        try { store.SetGapStatus(root.RootId, discovery, $"worker failed: {error.Message}; retry on next sync"); }
        catch (Exception noticeError) when (!IsStateDamage(noticeError)) { RunTrace.Emit($"local gap failure status unavailable: {noticeError.Message}"); }
    }
}
