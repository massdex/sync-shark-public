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

    private void RetainInterruptedHash(RootDef root, LocalGapEpoch epoch, ItemState item, LocalGapHashResult result)
    {
        if (result.Outcome == LocalGapHashOutcome.Held) return;
        if (result.Failure is { RootScoped: true } rootFailure &&
            store is IStateStore known)
        {
            if (known.IndexRetryDue(root.RootId)) known.RecordIndexFailure(root.RootId, rootFailure.Encode());
            return;
        }
        // IO owns the observed cause; live pause state cannot reinterpret the result.
        var reason = result.Diagnostic ?? MessageCatalog.Default.Message("proof-interrupted");
        if (result.Outcome == LocalGapHashOutcome.ChangedDuringRead)
            store.DeferGapHash(root.RootId, epoch, item, nameof(LocalGapHashOutcome.ChangedDuringRead));
        else if (result.Outcome == LocalGapHashOutcome.Unverifiable)
            store.FailGapItem(root.RootId, epoch, item, reason);
        store.SetGapStatus(root.RootId, false, result.Outcome switch
        {
            LocalGapHashOutcome.Paused => MessageCatalog.Default.Message("proof-paused"),
            LocalGapHashOutcome.Disabled => MessageCatalog.Default.Message("proof-disabled"),
            _ => $"{reason} for {item.Path}; retry pending"
        });
        RunTrace.Emit($"local gap verification interrupted or changed; retained for restart: {item.Path}");
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
