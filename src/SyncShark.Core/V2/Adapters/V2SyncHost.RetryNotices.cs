namespace Massdex.SyncShark.V2;

public sealed partial class V2SyncHost
{
    private async Task<string> ApplyRetryNoticeChoiceAsync(Notice notice, string choice, CancellationToken ct, Func<SyncAuthorityBatchProgress, Task>? onProgress = null, NoticeActionInput? input = null)
    {
        if (_run is not SyncRun engine)
            throw new InvalidOperationException("This engine cannot schedule item retries. Nothing was changed.");
        var pair = PairMapping.ToSyncPair(_store.Pair(notice.PairId)!, _store.FirstSyncDone(notice.PairId), _settings);
        var delivery = onProgress is null ? null : new ProgressDelivery(onProgress, ProgressNotShown);
        string message;
        try
        {
            message = await engine.ApplyRetryNoticeChoiceAsync(notice.PairId, notice.NoticeId!, choice, ct,
                Composition is { } composition ? (previous, updated) =>
                    V2CliRuntime.SaveNarrowerPair(_store, engine, composition.Paths.ProductRoot, previous, updated) : null,
                progress: new RemovalReviewProgress(pair, _time.GetUtcNow(), _time, delivery), input: input,
                updateRebuildableRule: Composition is { } rules ? (currentPair, current, replacement) =>
                    V2CliRuntime.UpdateRebuildableRule(rules.Paths.ProductRoot, currentPair, _store, current, replacement) : null);
        }
        finally
        {
            if (delivery is not null) await delivery.CompleteAsync(_settings.ProgressDrainTimeout);
        }
        if (notice.ReasonKey != PlanNotices.PlanBoundExceededKey &&
            choice is NoticeChoices.RestoreAgain or NoticeChoices.Rename or NoticeChoices.RetryNow or NoticeChoices.DeleteReviewed or NoticeChoices.KeepRestore) KickAutoSync(notice.PairId);
        return message;
    }

    // Review observation overlaps normal sync; never overwrite the other run's active-progress snapshot.
    private sealed class RemovalReviewProgress(SyncPair pair, DateTimeOffset started, TimeProvider clock,
        ProgressDelivery? delivery) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => delivery?.Post(RunResultMapping.ToProgress(pair, value, started, clock.GetUtcNow()));
    }

}
