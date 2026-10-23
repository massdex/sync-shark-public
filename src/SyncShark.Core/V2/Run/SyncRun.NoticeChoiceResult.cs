namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // UI and CLI share this boundary; the legacy throwing entry point remains available
    // to internal callers that deliberately handle admission refusal themselves.
    internal async Task<NoticeChoiceResult> ApplyRetryNoticeChoiceResultAsync(
        string pairId, string noticeId, string choice, CancellationToken ct,
        Action<PairDef, PairDef>? saveNarrowerPair = null,
        Action<SyncAction, ActionResult>? removalResult = null,
        IProgress<RunProgress>? progress = null, Notice? acceptedExclusion = null,
        NoticeActionInput? input = null,
        Func<PairDef, RebuildableChoice, RebuildableChoice?, string>? updateRebuildableRule = null)
    {
        var original = NoticeForChoice(pairId, noticeId);
        try
        {
            var message = await ApplyRetryNoticeChoiceAsync(pairId, noticeId, choice, ct,
                saveNarrowerPair, removalResult, progress, acceptedExclusion, input, updateRebuildableRule);
            return new(true, message);
        }
        catch (SyncRunRefusedException error)
        {
            var message = error.Message + " Review the notice and folder settings, then Retry or Ignore this cause.";
            if (original is not null) _store.RecordNoticeChoiceFailure(original, message);
            return new(false, message);
        }
    }
}
