namespace Massdex.SyncShark.V2;

public sealed partial class V2SyncHost
{
    private async Task ApplyRetryNoticeChoiceAsync(Notice notice, string choice, CancellationToken ct, Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        if (_run is not SyncRun engine)
            throw new InvalidOperationException("This engine cannot schedule item retries. Nothing was changed.");
        var pair = PairMapping.ToSyncPair(_store.Pair(notice.PairId)!, _store.FirstSyncDone(notice.PairId), _settings);
        var delivery = onProgress is null ? null : new ProgressDelivery(onProgress, ProgressNotShown);
        try
        {
            await engine.ApplyRetryNoticeChoiceAsync(notice.PairId, notice.NoticeId!, choice, ct,
                Composition is { } composition ? (previous, updated) =>
                    V2CliRuntime.SaveNarrowerPair(_store, engine, composition.Paths.ProductRoot, previous, updated) : null,
                progress: new RemovalReviewProgress(pair, _time.GetUtcNow(), _time, delivery));
        }
        finally
        {
            if (delivery is not null) await delivery.CompleteAsync(_settings.ProgressDrainTimeout);
        }
        if (choice is NoticeChoices.RetryNow or NoticeChoices.DeleteReviewed or NoticeChoices.KeepRestore) KickAutoSync(notice.PairId);
    }

    // Review observation overlaps normal sync; never overwrite the other run's active-progress snapshot.
    private sealed class RemovalReviewProgress(SyncPair pair, DateTimeOffset started, TimeProvider clock,
        ProgressDelivery? delivery) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => delivery?.Post(RunResultMapping.ToProgress(pair, value, started, clock.GetUtcNow()));
    }

}
