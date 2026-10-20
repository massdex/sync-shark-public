using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Owns the notice table: user-facing notices and their closure.</summary>
/// <remarks>
/// One notice per problem the problem is (pair, path ignoring case, rule, ReasonKey); Detail is
/// display text only. Re-raising an open problem adds nothing; re-raising a closed one reopens the same notice.
/// </remarks>
internal sealed class NoticeTable
{
    private readonly SqliteConnection _connection;

    public NoticeTable(SqliteConnection connection) => _connection = connection;

    /// <summary>Raises a notice once per problem; re-raising a closed one reopens it with the current display text.</summary>
    public void Add(SqliteTransaction transaction, long pairNo, Notice notice, long now)
    {
        var noticeId = notice.RetainedVersion is { } retained ? RetainedId(notice.PairId, retained.ActionId) :
            ShortId.Make("n", notice.PairId, notice.Rule.ToString(CultureInfo.InvariantCulture),
                RowCodec.Key(notice.Path), notice.ReasonKey);
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO notice(notice_id, pair_no, rule, kind, path, detail, reason_key, state, choice, created) " +
            "VALUES($id, $p, $rule, $kind, $path, $detail, $reason, 'open', NULL, $now) ON CONFLICT(notice_id) DO NOTHING;",
            ("$id", noticeId), ("$p", pairNo), ("$rule", notice.Rule), ("$kind", notice.Kind.ToString()),
            ("$path", notice.Path), ("$detail", NoticeDetailCodec.Encode(notice)), ("$reason", notice.ReasonKey), ("$now", now));
        if (StoreSchema.Changes(_connection, transaction) != 0) return;
        if (!IsSameProblem(transaction, noticeId, pairNo, notice))
            throw new InvalidDataException($"Short id collision: {noticeId}");
        // A version-specific notice is immutable after publication; replay must not reopen a
        // completed choice or replace its committed target identity with a planned source stamp.
        if (notice.RetainedVersion is not null) return;
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='open', choice=NULL, kind=$kind, path=$path, detail=$detail " +
            "WHERE notice_id=$id AND NOT ($retry=1 AND COALESCE(choice,'')='ignore') " +
            "AND (state <> 'open' OR ($refresh=1 AND detail <> $detail));",
            ("$retry", NoticeChoices.IsRetryNotice(notice) ? 1 : 0),
            ("$refresh", NoticeChoices.IsRetryNotice(notice) || notice.FreeSpace is not null || (notice.Kind == NoticeKind.UserDecision && notice.Detail.Contains(ProviderNameRefusedException.Detail, StringComparison.Ordinal)) || notice.ReasonKey == ProviderNameRefusedException.NoticeKey ? 1 : 0),
            ("$id", noticeId), ("$kind", notice.Kind.ToString()), ("$path", notice.Path), ("$detail", NoticeDetailCodec.Encode(notice)));
    }

    /// <summary>The open notices of one pair, or of every pair, oldest first.</summary>
    public IReadOnlyList<Notice> Open(long? pairNo)
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT n.notice_id, p.pair_id, n.rule, n.kind, n.path, n.detail, n.reason_key FROM notice n " +
            "JOIN pair p ON p.pair_no=n.pair_no WHERE n.state='open'" +
            (pairNo is null ? string.Empty : " AND n.pair_no=$p") + " ORDER BY n.created, n.notice_id;");
        if (pairNo is not null) command.Parameters.AddWithValue("$p", pairNo.Value);
        var notices = new List<Notice>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            notices.Add(NoticeDetailCodec.Decode(new Notice(reader.GetString(1), (int)reader.GetInt64(2),
                Enum.Parse<NoticeKind>(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(0))));
        return notices;
    }

    /// <summary>Marks a notice closed with the user's choice; an unknown id is a no-op.</summary>
    public void Close(string noticeId, string? choice) =>
        StoreSchema.Execute(_connection, null, "UPDATE notice SET state='closed', choice=$choice WHERE notice_id=$id;",
            ("$choice", choice), ("$id", noticeId));

    public void RecordChoiceFailure(SqliteTransaction transaction, long pairNo, Notice original, string failure)
    {
        var current = ById(pairNo, original.NoticeId!, transaction);
        if (current is null) return;
        // Preserve current structured version evidence, but restore the accepted why/fix text.
        // One UPDATE also clears sticky Ignore; no close/add gap and no retained-version Add no-op.
        var notice = current.Value.Notice with { Detail = original.Detail + "\n\n" + failure };
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='open', choice=NULL, detail=$detail WHERE notice_id=$id AND pair_no=$p;",
            ("$detail", NoticeDetailCodec.Encode(notice)), ("$id", original.NoticeId), ("$p", pairNo));
    }

    public void CloseFamily(long pairNo, string prefix, string? exceptReason) =>
        StoreSchema.Execute(_connection, null,
            "UPDATE notice SET state='closed', choice='resolved' WHERE pair_no=$pair " +
            "AND reason_key >= $prefix AND reason_key < $end " +
            "AND ($except IS NULL OR reason_key <> $except) AND (state='open' OR choice='ignore');",
            ("$pair", pairNo), ("$prefix", prefix), ("$end", prefix + "\uffff"), ("$except", exceptReason));

    internal static string RetainedId(string pairId, string actionId) => ShortId.Make("nv", pairId, actionId);

    public (Notice Notice, bool Closed)? ById(long pairNo, string noticeId, SqliteTransaction? transaction = null)
    {
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT n.notice_id,p.pair_id,n.rule,n.kind,n.path,n.detail,n.reason_key,n.state FROM notice n " +
            "JOIN pair p ON p.pair_no=n.pair_no WHERE n.pair_no=$p AND n.notice_id=$id;");
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$id", noticeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (NoticeDetailCodec.Decode(new Notice(reader.GetString(1), (int)reader.GetInt64(2),
            Enum.Parse<NoticeKind>(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(0))), reader.GetString(7) != "open");
    }

    public void CommitWinner(SqliteTransaction transaction, long pairNo, string pairId, SyncAction action, ItemState? winner)
    {
        if (winner is null) return;
        var row = ById(pairNo, RetainedId(pairId, action.ActionId), transaction);
        if (row?.Notice.RetainedVersion is not { } retained || retained.ActionId != action.ActionId ||
            retained.Side != action.Target || row.Value.Closed) return;
        var notice = row.Value.Notice with { RetainedVersion = retained with { Winner = winner } };
        StoreSchema.Execute(_connection, transaction, "UPDATE notice SET detail=$detail WHERE notice_id=$id AND pair_no=$p;",
            ("$detail", NoticeDetailCodec.Encode(notice)), ("$id", notice.NoticeId), ("$p", pairNo));
    }

    public void CloseResolved(SqliteTransaction transaction, long pairNo, string noticeId, string choice) =>
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='closed',choice=$choice WHERE notice_id=$id AND pair_no=$p;",
            ("$id", noticeId), ("$p", pairNo), ("$choice", choice));

    private bool IsSameProblem(SqliteTransaction transaction, string noticeId, long pairNo, Notice notice)
    {
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT pair_no, rule, path, reason_key, detail FROM notice WHERE notice_id=$id;");
        command.Parameters.AddWithValue("$id", noticeId);
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt64(0) == pairNo && reader.GetInt64(1) == notice.Rule &&
            string.Equals(RowCodec.Key(reader.GetString(2)), RowCodec.Key(notice.Path), StringComparison.Ordinal) &&
            string.Equals(reader.GetString(3), notice.ReasonKey, StringComparison.Ordinal) &&
            NoticeDetailCodec.Decode(notice with { Detail = reader.GetString(4), RetainedVersion = null })
                .RetainedVersion?.ActionId == notice.RetainedVersion?.ActionId;
    }
}
