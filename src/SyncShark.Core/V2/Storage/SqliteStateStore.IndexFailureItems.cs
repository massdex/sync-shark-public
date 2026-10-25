using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private void PublishIndexItemFailure(SqliteTransaction tx, (string PairId, long PairNo) owner,
        string rootId, string cause, string reason, FailureRetryOwner.State retry, string path)
    {
        if (ContentProofFailure.Decode(reason) is not { ItemScoped: true }) return;
        var existing = ReadIndexNotice(tx, owner, rootId, path);
        var count = Convert.ToInt32(StoreSchema.Scalar(_connection, tx,
            "SELECT COUNT(*) FROM (SELECT 1 FROM notice WHERE pair_no=$p AND reason_key=$r " +
            "AND path_key<>'' AND " + NoticeTable.ActionableSql("state") + " LIMIT $limit);",
            ("$p", owner.PairNo), ("$r", IndexFailurePrefix + rootId), ("$limit", StorageNotices.NoticeAggregateLimit)));
        if (existing is not { State: "open" or "aggregated" } && count >= StorageNotices.NoticeAggregateLimit) return;
        PublishIndexFailure(tx, owner, rootId, cause, reason, retry, path);
    }

    // Diagnostics beyond the inbox cap stay in their existing entity-owned debt, not extra notice rows.
    // The CLI reads bounded pages on demand. Both count and page use the same retained-cause query.
    private static string UnlistedIndexFailuresSql => """
        WITH failures AS (
          SELECT object_id,reason FROM local_rehash WHERE root_no=$root AND failure_count>0
          UNION ALL
          SELECT g.object_id,g.failure_reason FROM local_gap_item g
          WHERE g.root_no=$root AND g.failure_count>0 AND NOT EXISTS(
            SELECT 1 FROM local_rehash h WHERE h.root_no=g.root_no AND h.object_id=g.object_id AND h.failure_count>0)
        )
        SELECT f.object_id,d.path,f.name,w.reason
        FROM failures w JOIN file_state f INDEXED BY fs_obj ON f.root_no=$root AND f.object_id=w.object_id
        JOIN dir d ON d.dir_no=f.dir_no
        WHERE substr(w.reason,1,length($prefix))=$prefix
          AND CASE WHEN json_valid(substr(w.reason,length($prefix)+1))
            THEN json_extract(substr(w.reason,length($prefix)+1),'$.Stage') ELSE -1 END IN (
        """ + string.Join(",", ContentProofFailure.ItemStages.Select(stage => (int)stage)) + """
        ) AND NOT EXISTS(SELECT 1 FROM notice n WHERE n.pair_no=$pair AND n.reason_key=$reason
          AND n.path_key=CASE WHEN d.path_key='' THEN f.name_key ELSE d.path_key||'/'||f.name_key END
          AND
        """ + " " + NoticeTable.ActionableSql("n.state") + ")";

    internal (long Count, IReadOnlyList<Notice> Items) IndexFailureDetailPage(Notice summary,
        string? search, int limit, int offset) => InStore(() =>
    {
        var rootId = IndexFailureRoot(summary.ReasonKey);
        if (rootId is null || RootNo(rootId) is not { } root || IndexNoticeOwner(rootId) is not { } owner)
            return (0L, (IReadOnlyList<Notice>)Array.Empty<Notice>());
        var sql = "SELECT * FROM (" + UnlistedIndexFailuresSql + ") WHERE $search='' OR " +
            "instr(lower(CASE WHEN path='' THEN name ELSE path||'/'||name END),lower($search))>0";
        (string, object?)[] parameters = [("$root", root), ("$pair", owner.PairNo),
            ("$reason", IndexFailurePrefix + rootId), ("$prefix", ContentProofFailure.Prefix), ("$search", search ?? "")];
        var count = Convert.ToInt64(StoreSchema.Scalar(_connection, null, "SELECT COUNT(*) FROM (" + sql + ");", parameters));
        var rows = new List<Notice>();
        if (limit <= 0) return (count, (IReadOnlyList<Notice>)rows);
        using var command = StoreSchema.Command(_connection, null, sql + " ORDER BY path,name,object_id LIMIT $limit OFFSET $offset;");
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Min(limit, V2Settings.ScanRetryScopeLimit));
        command.Parameters.AddWithValue("$offset", offset < 0 ? 0 : offset);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var failure = ContentProofFailure.Decode(reader.GetString(3))!;
            rows.Add(summary with { Path = RowCodec.Join(reader.GetString(1), reader.GetString(2)),
                Detail = failure.Display + MessageCatalog.Default.Message("proof-notice-guidance") });
        }
        return (count, (IReadOnlyList<Notice>)rows);
    });
}
