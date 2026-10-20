namespace Massdex.SyncShark.V2;

public sealed partial class V2SyncHost
{
    private async Task<string> ApplyRetryNoticeChoiceAsync(Notice notice, string choice, CancellationToken ct, NoticeActionInput? input = null)
    {
        if (_run is not SyncRun engine)
            throw new InvalidOperationException("This engine cannot schedule item retries. Nothing was changed.");
        var message = await engine.ApplyRetryNoticeChoiceAsync(notice.PairId, notice.NoticeId!, choice, ct,
            Composition is { } composition ? (previous, updated) =>
                V2CliRuntime.SaveNarrowerPair(_store, engine, composition.Paths.ProductRoot, previous, updated) : null, input,
            Composition is { } rules ? (pair, current, replacement) =>
                V2CliRuntime.UpdateRebuildableRule(rules.Paths.ProductRoot, pair, _store, current, replacement) : null);
        if (choice is NoticeChoices.Rename or NoticeChoices.RetryNow or NoticeChoices.DeleteReviewed or NoticeChoices.KeepRestore) KickAutoSync(notice.PairId);
        return message;
    }
}
