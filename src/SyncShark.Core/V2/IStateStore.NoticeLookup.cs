namespace Massdex.SyncShark.V2;

public partial interface IStateStore
{
    /// <summary>Exact unresolved cause lookup, including ignored and aggregated work.</summary>
    bool HasUnresolvedNotice(string pairId, string reason, string path) =>
        UnresolvedNotices(pairId).Any(n => n.ReasonKey == reason && PairPath.Same(n.Path, path));

    /// <summary>Resolve matching work without loading the notice population.</summary>
    void CloseUnresolvedNotices(string pairId, string reason, string choice, string? path = null)
    {
        foreach (var notice in UnresolvedNotices(pairId).Where(n => n.ReasonKey == reason &&
            (path is null || PairPath.Same(n.Path, path))))
            if (notice.NoticeId is not null) CloseNotice(notice.NoticeId, choice);
    }
}
