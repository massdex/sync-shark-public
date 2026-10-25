namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public bool HasUnresolvedNotice(string pairId, string reason, string path) => InStore(() =>
        PairNo(pairId) is { } pair && StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM notice INDEXED BY nt_pair_reason_path WHERE pair_no=$p " +
            "AND reason_key=$reason AND path_key=$path AND " + NoticeTable.ActionableSql("state") + " LIMIT 1;",
            ("$p", pair), ("$reason", reason), ("$path", PathKey.Of(path))) is not null);

    public void CloseUnresolvedNotices(string pairId, string reason, string choice, string? path = null) => InStore(() =>
    {
        if (PairNo(pairId) is not { } pair) return;
        _notices.CloseWhere(null, choice,
            "pair_no=$p AND reason_key=$reason AND " + NoticeTable.ActionableSql("state") +
            (path is null ? ";" : " AND path_key=$path;"),
            ("$p", pair), ("$reason", reason), ("$path", path is null ? null : PathKey.Of(path)));
    });
}
