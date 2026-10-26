namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    private long AppendIndexFailureDetails(IReadOnlyList<Notice> summaries, string? search,
        int limit, int offset, int physicalCount, List<Notice> page)
    {
        long total = physicalCount;
        if (_store is not SqliteStateStore storage) return total;
        var remainingOffset = offset > physicalCount ? offset - physicalCount : 0;
        foreach (var summary in summaries)
        {
            var details = storage.IndexFailureDetailPage(summary, search, limit - page.Count, remainingOffset);
            total += details.Count;
            remainingOffset = remainingOffset > details.Count ? (int)(remainingOffset - details.Count) : 0;
            if (details.Count > 0)
            {
                var position = page.FindIndex(notice => notice.NoticeId == summary.NoticeId);
                if (position >= 0)
                    page[position] = summary with { Detail = summary.Detail + string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        MessageCatalog.Default.Message("proof-notice-overflow"), details.Count) };
            }
            page.AddRange(details.Items);
        }
        return total;
    }
}
