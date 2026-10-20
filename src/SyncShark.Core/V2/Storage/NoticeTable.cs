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
        // A payload-free replay with an explicit review key must not create an anonymous
        // path notice beside that identity's closed/ignored review, or discard its evidence.
        if (notice.ReasonKey == QuarantineFailure.NoticeKey && notice.Quarantine is null &&
            notice.NoticeId is { } reviewId && ById(pairNo, reviewId, transaction) is { } review &&
            review.Notice.Quarantine is not null && review.Notice.Rule == notice.Rule && PairPath.Same(review.Notice.Path, notice.Path))
            return;
        var noticeId = Id(notice);
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO notice(notice_id, pair_no, rule, kind, path, detail, reason_key, state, choice, created) " +
            "VALUES($id, $p, $rule, $kind, $path, $detail, $reason, 'open', NULL, $now) ON CONFLICT(notice_id) DO NOTHING;",
            ("$id", noticeId), ("$p", pairNo), ("$rule", notice.Rule), ("$kind", notice.Kind.ToString()),
            ("$path", notice.Path), ("$detail", NoticeDetailCodec.Encode(notice)), ("$reason", notice.ReasonKey), ("$now", now));
        if (StoreSchema.Changes(_connection, transaction) != 0)
        {
            SupersedeQuarantineReviews(transaction, pairNo, noticeId, notice);
            return;
        }
        if (!IsSameProblem(transaction, noticeId, pairNo, notice))
            throw new InvalidDataException($"Short id collision: {noticeId}");
        // A version-specific notice is immutable after publication; replay must not reopen a
        // completed choice or replace its committed target identity with a planned source stamp.
        if (notice.RetainedVersion is not null || notice.ConflictVersions is not null) return;
        if (notice.ReasonKey == QuarantineFailure.NoticeKey && ById(pairNo, noticeId, transaction) is { } previous)
        {
            var old = previous.Notice.Quarantine;
            if (old is not null && notice.Quarantine is { } proposed)
            {
                var changed = false;
                ItemState? PreserveHash(ItemState? item, ItemState? recorded)
                {
                    if (item is null) return null;
                    if (recorded is not { Hash: not null } || recorded.ObjectId != item.ObjectId ||
                        !PairPath.Same(recorded.Path, item.Path)) return item;
                    changed |= item.Hash is not null && item.Hash != recorded.Hash;
                    return item with { Hash = recorded.Hash };
                }
                notice = notice with { Quarantine = proposed with
                    { Source = PreserveHash(proposed.Source, old.Target == proposed.Target ? old.Source : old.ExpectedTarget),
                      ExpectedTarget = PreserveHash(proposed.ExpectedTarget, old.Target == proposed.Target ? old.ExpectedTarget : old.Source) } };
                if (changed) notice = notice with { Detail =
                    "The file content differs from the recorded review. The original reviewed hash is retained; Keep cannot accept changed bytes. Review the file or verify removal of the exclusion before retrying." };
            }
            // Planner summaries must not erase captured identities and recovery options.
            if (notice.Quarantine is null) notice = notice with { Quarantine = old };
            if (old is not null && notice.Quarantine is { ScanOnly: true, ConflictingGrantId: null } summary)
            {
                var same = old.Target == summary.Target
                    ? old.Source?.ObjectId == summary.Source?.ObjectId && old.ExpectedTarget?.ObjectId == summary.ExpectedTarget?.ObjectId
                    : old.Source?.ObjectId == summary.ExpectedTarget?.ObjectId && old.ExpectedTarget?.ObjectId == summary.Source?.ObjectId;
                if (same) notice = notice with { Quarantine = old, Detail = notice.Detail == "Antivirus hold; explicit retry required." ? previous.Notice.Detail : notice.Detail };
            }
            static bool SameEntity(ItemState? a, ItemState? b) => a?.ObjectId == b?.ObjectId;
            if (old is not null && notice.Quarantine is { } incoming && old.Target == incoming.Target &&
                SameEntity(old.Source, incoming.Source) && SameEntity(old.ExpectedTarget, incoming.ExpectedTarget))
                notice = notice with { Quarantine = incoming with
                {
                    GrantedExclusionPath = incoming.GrantedExclusionPath ?? old.GrantedExclusionPath,
                    GrantedStagePath = incoming.GrantedStagePath ?? old.GrantedStagePath,
                    ConflictingGrantId = incoming.ConflictingGrantId ?? old.ConflictingGrantId
                } };
            if (old is not null && notice.Quarantine is { } current && old.Target == current.Target &&
                SameEntity(old.Source, current.Source) && SameEntity(old.ExpectedTarget, current.ExpectedTarget) &&
                StoreSchema.Scalar(_connection, transaction,
                    "SELECT choice FROM notice WHERE notice_id=$id AND state='closed';", ("$id", noticeId)) is string choice &&
                choice is NoticeChoices.QuarantineIgnore or NoticeChoices.QuarantineExclude) return;
        }
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='open', choice=NULL, kind=$kind, path=$path, detail=$detail " +
            "WHERE notice_id=$id AND NOT ($retry=1 AND COALESCE(choice,'')='ignore') " +
            "AND (state <> 'open' OR ($refresh=1 AND detail <> $detail));",
            ("$retry", NoticeChoices.IsRetryNotice(notice) ? 1 : 0),
            ("$refresh", JournalSizing.IsNotice(notice) || notice.Quarantine is not null || notice.DefenderHistory is not null || NoticeChoices.IsRetryNotice(notice) || notice.FreeSpace is not null || (notice.Kind == NoticeKind.UserDecision && notice.Detail.Contains(ProviderNameRefusedException.Detail, StringComparison.Ordinal)) || notice.ReasonKey == ProviderNameRefusedException.NoticeKey ? 1 : 0),
            ("$id", noticeId), ("$kind", notice.Kind.ToString()), ("$path", notice.Path), ("$detail", NoticeDetailCodec.Encode(notice)));
    }

    private void SupersedeQuarantineReviews(SqliteTransaction transaction, long pairNo, string noticeId, Notice notice)
    {
        if (notice.ReasonKey != QuarantineFailure.NoticeKey || notice.Quarantine is not { } current) return;
        // Failure reviews protect both sides; scan summaries protect their target and any
        // explicitly held source. Compare by pair side, never by source/target orientation.
        static (string? A, string? B) HeldIdentities(QuarantineNotice hold)
        {
            var source = hold.HeldSource ?? (!hold.ScanOnly || hold.Source?.Flags.HasFlag(ItemFlags.Quarantined) == true
                ? hold.Source : null);
            return hold.Target == Side.A
                ? (hold.ExpectedTarget?.ObjectId, source?.ObjectId)
                : (source?.ObjectId, hold.ExpectedTarget?.ObjectId);
        }
        var identities = HeldIdentities(current);
        if (identities == (null, null)) return;
        var superseded = new List<string>();
        using (var command = StoreSchema.Command(_connection, transaction,
            "SELECT notice_id,path,detail FROM notice WHERE pair_no=$p AND rule=$rule AND reason_key=$reason " +
            "AND state='open' AND notice_id<>$id;"))
        {
            command.Parameters.AddWithValue("$p", pairNo);
            command.Parameters.AddWithValue("$rule", notice.Rule);
            command.Parameters.AddWithValue("$reason", notice.ReasonKey);
            command.Parameters.AddWithValue("$id", noticeId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (RowCodec.Key(reader.GetString(1)) != RowCodec.Key(notice.Path)) continue;
                var previous = NoticeDetailCodec.Decode(notice with { Detail = reader.GetString(2), Quarantine = null });
                if (previous.Quarantine is not { } old || HeldIdentities(old) != identities) continue;
                // Do not discard separate recovery/exclusion obligations with a scan summary.
                if (old.PendingActionId != current.PendingActionId || old.ConflictingGrantId != current.ConflictingGrantId ||
                    old.GrantedExclusionPath != current.GrantedExclusionPath || old.GrantedStagePath != current.GrantedStagePath) continue;
                // Old preview keys are intentionally not migrated here.
                if (reader.GetString(0) != Id(previous)) continue;
                superseded.Add(reader.GetString(0));
            }
        }
        // Publication precedes closure in the same transaction; no file-state hold is changed.
        foreach (var id in superseded) CloseResolved(transaction, pairNo, id, "quarantine-superseded");
    }

    /// <summary>The open notices of one pair, or of every pair, oldest first.</summary>
    public IReadOnlyList<Notice> Open(long? pairNo, string? defenderRoot = null, string? reasonKey = null)
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT n.notice_id, p.pair_id, n.rule, n.kind, n.path, n.detail, n.reason_key FROM notice n " +
            "JOIN pair p ON p.pair_no=n.pair_no WHERE n.state='open'" +
            (pairNo is null ? string.Empty : " AND n.pair_no=$p") +
            (reasonKey is null ? string.Empty : " AND n.reason_key=$reason") +
            (defenderRoot is null ? string.Empty : " AND (n.reason_key=$held OR (n.reason_key >= $prefix AND n.reason_key < $end) OR (n.reason_key >= $shared AND n.reason_key < $sharedEnd))") +
            " ORDER BY n.created, n.notice_id;");
        if (pairNo is not null) command.Parameters.AddWithValue("$p", pairNo.Value);
        if (reasonKey is not null) command.Parameters.AddWithValue("$reason", reasonKey);
        if (defenderRoot is not null)
        {
            var prefix = DefenderReview.ReviewKey(defenderRoot, "");
            command.Parameters.AddWithValue("$held", DefenderReview.HeldKey(defenderRoot));
            command.Parameters.AddWithValue("$prefix", prefix);
            command.Parameters.AddWithValue("$end", prefix + char.MaxValue);
            var shared = DefenderReview.SharedReleaseKey(defenderRoot, "");
            command.Parameters.AddWithValue("$shared", shared);
            command.Parameters.AddWithValue("$sharedEnd", shared + char.MaxValue);
        }
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

    internal static string Id(Notice notice)
    {
        if (notice.RetainedVersion is { } retained) return RetainedId(notice.PairId, retained.ActionId);
        if (notice.ConflictVersions is not null)
            return ShortId.Make("nc", notice.PairId, notice.Rule.ToString(CultureInfo.InvariantCulture), notice.Path,
                System.Text.Json.JsonSerializer.Serialize(notice.ConflictVersions));
        var rule = notice.Rule.ToString(CultureInfo.InvariantCulture);
        if (notice.Quarantine is { } hold)
            return ShortId.Make("nq", notice.PairId, rule, RowCodec.Key(notice.Path), notice.ReasonKey,
                (hold.Target == Side.A ? hold.ExpectedTarget : hold.Source)?.ObjectId ?? "",
                (hold.Target == Side.B ? hold.ExpectedTarget : hold.Source)?.ObjectId ?? "");
        return ShortId.Make("n", notice.PairId, rule, RowCodec.Key(notice.Path), notice.ReasonKey);
    }

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
