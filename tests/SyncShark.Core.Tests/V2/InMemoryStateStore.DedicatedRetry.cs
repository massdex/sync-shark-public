namespace Massdex.SyncShark.V2;

internal sealed partial class InMemoryStateStore
{
    // Called under AddNotices' gate, matching NoticeTable's transaction-scoped adapter.
    private bool SuppressDedicatedRetry(ref Notice notice)
    {
        var incoming = notice;
        Notice? Find(string reason, int? rule = null) => _notices.LastOrDefault(known =>
            known.PairId == incoming.PairId && PairPath.Same(known.Path, incoming.Path) &&
            known.ReasonKey == reason && (rule is null || known.Rule == rule) &&
            !_closedNotices.ContainsKey(known.NoticeId!));
        return RetryNoticeFold.SuppressDedicated(ref notice, reason => Find(reason) is not null,
            choice =>
            {
                if (Find("sync-retry", 40) is { NoticeId: { } id }) CloseNotice(id, choice);
            },
            retry => Find("unreadable", 30) is { } unreadable
                ? unreadable with { Detail = retry.Detail, CauseKey = retry.CauseKey,
                    CauseIdentity = retry.CauseIdentity, FailureRetry = retry.FailureRetry ?? unreadable.FailureRetry }
                : retry);
    }
}
