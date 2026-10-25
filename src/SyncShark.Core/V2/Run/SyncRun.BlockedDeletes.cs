namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private string ShowDeleteNoticeItems(string pairId, bool review, NoticeActionInput? input)
    {
        if (input?.Offset is > 0 || input?.Name is not null)
            throw new SyncRunRefusedException("Use the returned page token, not an offset or name.");
        var page = _store.ReadDeleteNoticeItems(pairId, review, input?.PageToken, 50);
        var lines = page.Items.Select(item => item.Path + ": " + item.Detail +
            (item.NoticeId is null ? "" : $" Notice {item.NoticeId}; choices: {NoticeChoices.DeleteReviewed} (Delete with Recovery), {NoticeChoices.KeepRestore} (Keep and sync back), {NoticeChoices.Ignore} (Ignore).")).ToList();
        if (page.Items.Count == 0) lines.Add("No remaining items in this page. Refresh notices.");
        if (page.NextToken is not null) lines.Add("Next page token: " + page.NextToken);
        lines.Add("Read-only current view; no deletion is approved by viewing this page.");
        return string.Join(Environment.NewLine, lines);
    }

    private string ShowBlockedDeletes(string pairId, NoticeActionInput? input)
    {
        if (input?.Offset is > 0 || input?.Name is not null)
            throw new SyncRunRefusedException("Use the returned page token for blocked deletions, not an offset or name.");
        var page = _store.ReadBlockedDeletes(pairId, input?.PageToken, 50);
        var lines = page.Items.Select(item => $"{item.Path}: {item.Detail}").ToList();
        if (lines.Count == 0) lines.Add("No blocked deletions remain in this page. Refresh notices for the current count.");
        if (page.NextToken is not null) lines.Add("Next page token: " + page.NextToken);
        lines.Add("Read-only current view. These items remain unsynced and retry automatically; this does not approve deletion.");
        return string.Join(Environment.NewLine, lines);
    }
}
