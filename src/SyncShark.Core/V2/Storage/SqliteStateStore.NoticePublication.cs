namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public void AddNotices(IEnumerable<Notice> notices)
    {
        foreach (var page in notices.Chunk(NoticeMaintenancePageRows))
            PublishPreparedNotices(page.Select(notice => PrepareDefenderNotice(notice, retainLive: false)).ToArray());
    }

    private void PublishPreparedNotices(IReadOnlyList<CapturedDefenderNotice> prepared)
    {
        foreach (var page in prepared.Chunk(NoticeMaintenancePageRows)) InStore(() =>
        {
            using var transaction = _connection.BeginTransaction();
            // Explicit reviews retain their own entity/version binding. The validation owner
            // rejects raced optional hashes; a current path occupant cannot replace the review.
            var captured = page.Select(item => ValidateDefenderCapture(item, item.Original, transaction)).ToArray();
            var now = NowMs();
            foreach (var notice in captured)
                _notices.Add(transaction, RequirePair(notice.PairId), notice, now);
            foreach (var pairId in captured.Where(notice => notice.ReasonKey == "deletion-review").Select(notice => notice.PairId).Distinct())
                RefreshDeleteReviewNotices(transaction, RequirePair(pairId), pairId, now, exact: false);
            transaction.Commit();
        });
    }

}
