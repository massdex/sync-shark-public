using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Owns the notice table: user-facing notices and their closure.</summary>
/// <remarks>
/// One notice per problem the problem is (pair, path ignoring case, rule, ReasonKey); Detail is
/// display text only. Re-raising an open problem adds nothing; re-raising a closed one reopens the same notice.
/// </remarks>
internal sealed partial class NoticeTable
{
    private readonly SqliteConnection _connection;

    public NoticeTable(SqliteConnection connection) => _connection = connection;

    /// <summary>Raises a notice once per problem; re-raising a closed one reopens it with the current display text.</summary>
    public void Add(SqliteTransaction transaction, long pairNo, Notice notice, long now)
    {
        // Rule 30's unconfirmed comparison and rule 40's retry are one pending item, not two issues.
        // Fold old rows lazily by exact primary key; no scan, migration or schema bump is required.
        if (notice.ReasonKey is "content-unconfirmed" or "sync-retry")
        {
            var canonicalId = ShortId.Make("n", notice.PairId, "40", RowCodec.Key(notice.Path), "sync-retry");
            var retryOpen = ById(pairNo, canonicalId, transaction) is { Closed: false };
            if (notice.ReasonKey == "sync-retry" || retryOpen)
                StoreSchema.Execute(_connection, transaction,
                    "UPDATE notice SET state='closed',choice='superseded-by-retry' WHERE notice_id=$old AND state='open';",
                    ("$old", ShortId.Make("n", notice.PairId, "30", RowCodec.Key(notice.Path), "content-unconfirmed")));
            // A generic planner observation must not erase a more specific executor diagnosis.
            if (notice.ReasonKey == "content-unconfirmed" && retryOpen) return;
        }
        var noticeId = notice.RetainedVersion is { } retained ? RetainedId(notice.PairId, retained.ActionId) :
            notice.Repair is { } repair ? ShortId.Make("n", notice.PairId, notice.Rule.ToString(CultureInfo.InvariantCulture),
                notice.ReasonKey, repair.Side.ToString(), repair.Item.ObjectId) :
            ShortId.Make("n", notice.PairId, notice.Rule.ToString(CultureInfo.InvariantCulture),
                RowCodec.Key(notice.Path), notice.ReasonKey);
        MigrateLegacyRepair(transaction, pairNo, noticeId, notice);
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
        // Fresh observations refresh revision evidence, but never erase an already-recorded effect intent.
        if (notice.Repair is { Destination: null } && ById(pairNo, noticeId, transaction)?.Notice is { Repair: { Destination: not null } captured } prior)
            notice = notice.ReasonKey == "link-item"
                ? notice with { Repair = notice.Repair with { Destination = captured.Destination }, Detail = prior.Detail }
                : notice with { Repair = captured };
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='open', choice=NULL, kind=$kind, path=$path, detail=$detail " +
            "WHERE notice_id=$id AND NOT ($retry=1 AND COALESCE(choice,'')='ignore') " +
            "AND (state <> 'open' OR ($refresh=1 AND detail <> $detail));",
            ("$retry", NoticeChoices.IsRetryNotice(notice) && notice.UploadSuspended is null ? 1 : 0),
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

    internal static string RetainedId(string pairId, string actionId) => ShortId.Make("nv", pairId, actionId);

    public (Notice Notice, bool Closed)? ById(long pairNo, string noticeId, SqliteTransaction? transaction = null)
    {
        noticeId = ResolveLegacyAlias(pairNo, noticeId, transaction);
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
        if (!reader.Read()) return false;
        var saved = NoticeDetailCodec.Decode(notice with
            { Detail = reader.GetString(4), RetainedVersion = null, Repair = null });
        return reader.GetInt64(0) == pairNo && reader.GetInt64(1) == notice.Rule &&
            (notice.Repair is { } repair
                ? saved.Repair is { } prior && prior.Side == repair.Side && prior.Item.ObjectId == repair.Item.ObjectId
                : string.Equals(RowCodec.Key(reader.GetString(2)), RowCodec.Key(notice.Path), StringComparison.Ordinal)) &&
            string.Equals(reader.GetString(3), notice.ReasonKey, StringComparison.Ordinal) &&
            saved.RetainedVersion?.ActionId == notice.RetainedVersion?.ActionId;
    }
}
