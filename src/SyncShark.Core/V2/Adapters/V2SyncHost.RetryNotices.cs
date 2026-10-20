namespace Massdex.SyncShark.V2;

public sealed partial class V2SyncHost
{
    private async Task ApplyRetryNoticeChoiceAsync(Notice notice, string choice, CancellationToken ct)
    {
        if (_run is not SyncRun engine)
            throw new InvalidOperationException("This engine cannot schedule item retries. Nothing was changed.");
        await engine.ApplyRetryNoticeChoiceAsync(notice.PairId, notice.NoticeId!, choice, ct,
            Composition is { } composition ? (previous, updated) =>
                V2CliRuntime.SaveNarrowerPair(_store, engine, composition.Paths.ProductRoot, previous, updated) : null);
        if (choice is NoticeChoices.RetryNow or NoticeChoices.DeleteReviewed or NoticeChoices.KeepRestore) KickAutoSync(notice.PairId);
    }
}
