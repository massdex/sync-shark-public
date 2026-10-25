namespace Massdex.SyncShark.V2;

internal sealed partial class NoticeTable
{
    internal static readonly string QuarantinePageSql =
        "SELECT notice_id,rule,kind,path,detail,reason_key FROM notice INDEXED BY nt_groupable_delete " +
        "WHERE pair_no=$p AND reason_key=$reason AND " + ActionableSql("state") +
        " AND notice_id>$after AND notice_id<=$through ORDER BY notice_id LIMIT $limit;";

    internal string QuarantineHorizon(long pairNo) => StoreSchema.Scalar(_connection, null,
        "SELECT MAX(notice_id) FROM notice INDEXED BY nt_groupable_delete WHERE pair_no=$p AND reason_key=$reason AND " +
        ActionableSql("state") + ";", ("$p", pairNo), ("$reason", QuarantineFailure.NoticeKey)) as string ?? string.Empty;

    internal IReadOnlyList<Notice> QuarantinePage(long pairNo, string pairId, string after, string through, int limit)
    {
        using var command = StoreSchema.Command(_connection, null, QuarantinePageSql);
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$reason", QuarantineFailure.NoticeKey);
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$through", through);
        command.Parameters.AddWithValue("$limit", limit);
        var notices = new List<Notice>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            notices.Add(NoticeDetailCodec.Decode(new Notice(pairId, (int)reader.GetInt64(1),
                Enum.Parse<NoticeKind>(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(0))));
        return notices;
    }
}
