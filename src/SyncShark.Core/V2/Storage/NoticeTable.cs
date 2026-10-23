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

    private readonly TimeProvider _clock;

    public NoticeTable(SqliteConnection connection) : this(connection, TimeProvider.System) { }

    public NoticeTable(SqliteConnection connection, TimeProvider clock)
    {
        _connection = connection;
        _clock = clock;
    }

    /// <summary>The single terminal-transition owner, including bulk auto-closers. Repeating the same
    /// closure never renews history retention; resolving a suppressed episode starts a new terminal clock.</summary>
    internal void CloseWhere(SqliteTransaction? transaction, string? choice, string where,
        params (string Name, object? Value)[] parameters)
    {
        if (choice == "aggregated")
        {
            AggregateWhere(transaction, where, parameters);
            return;
        }
        if (choice is not null && NoticeChoices.IsIgnoreChoice(choice))
        {
            // Ignore is presentation suppression, never resolution, exclusion or a paused work item.
            StoreSchema.Execute(_connection, transaction,
                "UPDATE notice SET state=CASE WHEN state='aggregated' THEN state ELSE 'open' END, " +
                "choice=$closingChoice,closed_at=NULL WHERE " + ActionableSql("state") + " AND (" + where.TrimEnd(';') + ");",
                [.. parameters, ("$closingChoice", choice)]);
            return;
        }
        // This is the shared episode-success owner: closing replaces an earlier Ignore choice as well as
        // retiring its state. Every family routes success here; Ignore never carries into a later failure.
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET closed_at=CASE WHEN state<>'closed' OR choice IS NOT $closingChoice " +
            "OR closed_at IS NULL THEN $closedAt ELSE closed_at END,state='closed',choice=$closingChoice WHERE " + where,
            [.. parameters, ("$closingChoice", choice), ("$closedAt", _clock.GetUtcNow().ToUnixTimeMilliseconds())]);
    }

    internal void ReopenWhere(SqliteTransaction? transaction, string where,
        params (string Name, object? Value)[] parameters) => StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='open',choice=NULL,closed_at=NULL WHERE " + where, parameters);

    internal void AggregateWhere(SqliteTransaction? transaction, string where,
        params (string Name, object? Value)[] parameters) => StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='aggregated',closed_at=NULL WHERE " + where, parameters);

    /// <summary>Age only terminal history. Suppression, retained choices and frozen restore authority are
    /// durable state, not disposable history; resolving their episode makes them eligible later.</summary>
    internal void Prune(SqliteTransaction transaction, long cutoff)
    {
        while (PrunePage(transaction, cutoff, 512) != 0) { }
    }

    internal int PrunePage(SqliteTransaction transaction, long cutoff, int limit)
    {
        if (limit is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(limit));
        StoreSchema.Execute(_connection, transaction,
            "DELETE FROM notice WHERE notice_id IN (SELECT notice_id FROM notice " +
            "WHERE state='closed' AND closed_at < $cutoff AND restore_hold=0 " +
            "AND NOT (" + IgnoredSql("choice") + ") AND COALESCE(choice,'') NOT IN ('quarantine-exclude','keep-rebuildable-rule') LIMIT $limit);",
            ("$cutoff", cutoff), ("$limit", limit));
        return checked((int)StoreSchema.Changes(_connection, transaction));
    }

    /// <summary>Raises a notice once per problem; re-raising a closed one reopens it with the current display text.</summary>
    public void Add(SqliteTransaction transaction, long pairNo, Notice notice, long now)
    {
        // A payload-free replay with an explicit review key must not create an anonymous
        // path notice beside that identity's closed/ignored review, or discard its evidence.
        if (notice.ReasonKey == QuarantineFailure.NoticeKey && notice.Quarantine is null &&
            notice.NoticeId is { } reviewId && ById(pairNo, reviewId, transaction) is { } review &&
            review.Notice.Quarantine is not null && review.Notice.Rule == notice.Rule && PairPath.Same(review.Notice.Path, notice.Path))
            return;
        if (SuppressDedicatedRetry(transaction, pairNo, ref notice)) return;
        // Rule 30's unconfirmed comparison and rule 40's retry are one pending item, not two issues.
        // Fold old rows lazily by exact primary key; no scan, migration or schema bump is required.
        if (RetryNoticeFold.SuppressIncoming(notice,
            () => StoreSchema.Scalar(_connection, transaction,
                "SELECT 1 FROM sync_retry WHERE pair_no=$p AND path_key=$key AND json_extract(json,'$.DeleteBlockedDetail') IS NOT NULL;",
                ("$p", pairNo), ("$key", RowCodec.Key(notice.Path))) is not null,
            (rule, reason) => ById(pairNo, ShortId.Make("n", notice.PairId, rule.ToString(CultureInfo.InvariantCulture),
                RowCodec.Key(notice.Path), reason), transaction) is { Closed: false } existing ? existing.Notice : null,
            loser => CloseWhere(transaction, "superseded-by-retry",
                "notice_id=$old AND " + ActionableSql("state") + ";", ("$old", loser.NoticeId)))) return;
        var noticeId = Id(notice);
        // Keep dismisses one rule until that rule itself changes (its category or prefix changes the id).
        if (notice.RebuildableRule is not null && IsKeptRule(transaction, pairNo, noticeId)) return;
        MigrateLegacyRepair(transaction, pairNo, noticeId, notice);
        var restoreHold = RecoveryRestoreHold.Holds(notice) ? 1 : 0;
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO notice(notice_id, pair_no, rule, kind, path, path_key, detail, reason_key, state, choice, created, restore_hold) " +
            "VALUES($id, $p, $rule, $kind, $path, $path_key, $detail, $reason, 'open', NULL, $now, $hold) ON CONFLICT(notice_id) DO NOTHING;",
            ("$id", noticeId), ("$p", pairNo), ("$rule", notice.Rule), ("$kind", notice.Kind.ToString()),
            ("$path", notice.Path), ("$path_key", PathKey.Of(notice.Path)),
            ("$detail", NoticeDetailCodec.Encode(notice)), ("$reason", notice.ReasonKey), ("$now", now),
            ("$hold", restoreHold));
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
                choice == NoticeChoices.QuarantineExclude) return;
        }
        // Fresh observations refresh revision evidence, but never erase an already-recorded effect intent.
        if (notice.Repair is { Destination: null } && ById(pairNo, noticeId, transaction)?.Notice is { Repair: { Destination: not null } captured } prior)
            notice = notice.ReasonKey == "link-item"
                ? notice with { Repair = notice.Repair with { Destination = captured.Destination, RetainedStaging = notice.Repair.RetainedStaging ?? captured.RetainedStaging }, Detail = prior.Detail }
                : notice with { Repair = captured };
        // A moved repair can keep identical detail (including a captured destination); its display path
        // must still follow the current observation, including immediately after folding a legacy row.
        var previousNotice = ById(pairNo, noticeId, transaction)?.Notice;
        var sameEpisode = SameEpisode(previousNotice, notice);
        var suppressed = "$sameEpisode=1 AND " + IgnoredSql("choice");
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state=CASE WHEN " + suppressed + " THEN state ELSE 'open' END, " +
            "closed_at=CASE WHEN " + suppressed + " THEN closed_at ELSE NULL END, " +
            "choice=CASE WHEN " + suppressed + " THEN choice ELSE NULL END, " +
            "rule=$rule, kind=$kind, path=$path, path_key=$path_key, reason_key=$reason, detail=$detail, restore_hold=$hold " +
            "WHERE notice_id=$id AND ((NOT (" + suppressed + ") AND state <> 'open') OR restore_hold <> $hold " +
            "OR ($refresh=1 AND (detail <> $detail OR path <> $path OR path_key <> $path_key OR reason_key <> $reason OR rule <> $rule)));",
            ("$sameEpisode", sameEpisode ? 1 : 0),
            ("$refresh", notice.Storage is not null || NoticeVocabulary.IsJournalNotice(notice) || notice.Quarantine is not null || notice.DefenderHistory is not null || NoticeChoices.IsRetryNotice(notice) || notice.FreeSpace is not null || (notice.Kind == NoticeKind.UserDecision && notice.Detail.Contains(ProviderNameRefusedException.Detail, StringComparison.Ordinal)) || notice.ReasonKey == ProviderNameRefusedException.NoticeKey ? 1 : 0),
            ("$id", noticeId), ("$rule", notice.Rule), ("$kind", notice.Kind.ToString()), ("$path", notice.Path),
            ("$path_key", PathKey.Of(notice.Path)), ("$reason", notice.ReasonKey), ("$detail", NoticeDetailCodec.Encode(notice)),
            ("$hold", restoreHold));
    }

    // Display text, attempt count and retry deadline are not cause identity. Providers/failure owners
    // supply CauseKey before formatting a message; legacy notices fall back to their stable reason.
    private static bool SameEpisode(Notice? previous, Notice incoming) => previous is not null &&
        (incoming.CauseKey is null || incoming.CauseKey == previous.CauseKey) &&
        (incoming.FolderDeleteEpisode is null || incoming.FolderDeleteEpisode == previous.FolderDeleteEpisode) &&
        (incoming.UploadSuspended is null || incoming.UploadSuspended == previous.UploadSuspended);

    internal static string IgnoredSql(string choiceColumn) =>
        "COALESCE(" + choiceColumn + ",'') IN ('ignore','quarantine-ignore')";

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
            "AND " + NoticeTable.ActionableSql("state") + " AND notice_id<>$id;"))
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
    public IReadOnlyList<Notice> Open(long? pairNo, string? defenderRoot = null, string? reasonKey = null, bool unresolved = false)
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT n.notice_id, p.pair_id, n.rule, n.kind, n.path, n.detail, n.reason_key FROM notice n " +
            "JOIN pair p ON p.pair_no=n.pair_no WHERE " + (unresolved ? ActionableSql("n.state") :
                VisibleSql("n.state") + " AND NOT (" + IgnoredSql("n.choice") + ")") +
            (pairNo is null ? string.Empty : " AND n.pair_no=$p") +
            (reasonKey is null ? string.Empty : " AND n.reason_key=$reason") +
            (defenderRoot is null ? string.Empty : " AND (n.reason_key=$held OR (n.reason_key >= $prefix AND n.reason_key < $end) OR (n.reason_key >= $shared AND n.reason_key < $sharedEnd))") +
            " ORDER BY n.created, n.notice_id;");
        if (pairNo is not null) command.Parameters.AddWithValue("$p", pairNo.Value);
        if (reasonKey is not null) command.Parameters.AddWithValue("$reason", reasonKey);
        if (defenderRoot is not null)
        {
            var prefix = DefenderReviewKeys.ReviewKey(defenderRoot, "");
            command.Parameters.AddWithValue("$held", DefenderReviewKeys.HeldKey(defenderRoot));
            command.Parameters.AddWithValue("$prefix", prefix);
            command.Parameters.AddWithValue("$end", prefix + char.MaxValue);
            var shared = DefenderReviewKeys.SharedReleaseKey(defenderRoot, "");
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

    /// <summary>Open link-item notices for one pair, filtered by reason in SQL.</summary>
    public IReadOnlyList<Notice> OpenLinks(long pairNo) => Open(pairNo, reasonKey: "link-item", unresolved: true);

    /// <summary>Marks a notice closed with the user's choice; an unknown id is a no-op.</summary>
    public void Close(string noticeId, string? choice) =>
        CloseWhere(null, choice, "notice_id=$id;", ("$id", noticeId));

    internal static string Id(Notice notice)
    {
        if (notice.RebuildableRule is { } rebuildable)
            return ShortId.Make("n", notice.PairId, notice.Rule.ToString(CultureInfo.InvariantCulture), notice.ReasonKey,
                rebuildable.Category.ToLowerInvariant(), RowCodec.Key(rebuildable.Prefix));
        if (notice.ReasonKey == ExecutionReasons.ConflictCopyBlocked && notice.NoticeId is { Length: > 0 } successorId)
            return successorId;
        if (notice.Storage is not null) return notice.NoticeId ?? ShortId.Make("ns", notice.PairId, notice.ReasonKey);
        // One durable notice per Recovery item: pending and ready share this id, so the verified transition updates
        // the same row instead of leaving a stale pending notice behind.
        if (IsRecoveryRestoreReason(notice.ReasonKey))
        {
            if (notice.ConflictOperation?.Choice is { Length: > 0 } recoveryId)
                return ShortId.Make("rrs", notice.PairId, recoveryId);
            if (notice.NoticeId is { Length: > 0 } explicitId) return explicitId;
        }
        if (notice.RetainedVersion is { } retained) return RetainedId(notice.PairId, retained.ActionId);
        if (notice.ConflictVersions is not null)
            return ShortId.Make("nc", notice.PairId, notice.Rule.ToString(CultureInfo.InvariantCulture), notice.Path,
                System.Text.Json.JsonSerializer.Serialize(notice.ConflictVersions));
        if (notice.Repair is { } repair) return ShortId.Make("n", notice.PairId, notice.Rule.ToString(CultureInfo.InvariantCulture),
            notice.ReasonKey, repair.Side.ToString(), repair.Item.ObjectId);
        var rule = notice.Rule.ToString(CultureInfo.InvariantCulture);
        if (notice.Quarantine is { } hold)
            return ShortId.Make("nq", notice.PairId, rule, RowCodec.Key(notice.Path), notice.ReasonKey,
                (hold.Target == Side.A ? hold.ExpectedTarget : hold.Source)?.ObjectId ?? "",
                (hold.Target == Side.B ? hold.ExpectedTarget : hold.Source)?.ObjectId ?? "");
        return ShortId.Make("n", notice.PairId, rule, RowCodec.Key(notice.Path), notice.ReasonKey);
    }

    private static bool IsRecoveryRestoreReason(string reason) =>
        reason is "recovery-restore-pending" or "recovery-restore-ready";

    public void RecordChoiceFailure(SqliteTransaction transaction, long pairNo, Notice original, string failure)
    {
        var current = ById(pairNo, original.NoticeId!, transaction);
        if (current is null) return;
        // Preserve current structured version evidence, but restore the accepted why/fix text.
        // One UPDATE also clears sticky Ignore; no close/add gap and no retained-version Add no-op.
        var notice = current.Value.Notice with { Detail = original.Detail + "\n\n" + failure };
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='open', closed_at=NULL, choice=NULL, detail=$detail WHERE notice_id=$id AND pair_no=$p;",
            ("$detail", NoticeDetailCodec.Encode(notice)), ("$id", original.NoticeId), ("$p", pairNo));
    }

    public void CloseFamily(long pairNo, string prefix, string? exceptReason) =>
        CloseWhere(null, "resolved",
            "pair_no=$pair " +
            "AND reason_key >= $prefix AND reason_key < $end " +
            "AND ($except IS NULL OR reason_key <> $except) AND (" + ActionableSql("state") + " OR " + IgnoredSql("choice") + ");",
            ("$pair", pairNo), ("$prefix", prefix), ("$end", prefix + "\uffff"), ("$except", exceptReason));

    internal static string RetainedId(string pairId, string actionId) => ShortId.Make("nv", pairId, actionId);

    // Inbox visibility is narrower: aggregated notices are hidden there but remain actionable.
    internal static string ActionableSql(string stateColumn) => stateColumn + " IN ('open','aggregated')";

    internal static string VisibleSql(string stateColumn) =>
        "(" + ActionableSql(stateColumn) + " AND " + stateColumn + "<>'aggregated')";

    public (Notice Notice, bool Closed)? ById(long pairNo, string noticeId, SqliteTransaction? transaction = null)
    {
        noticeId = ResolveLegacyAlias(pairNo, noticeId, transaction);
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT n.notice_id,p.pair_id,n.rule,n.kind,n.path,n.detail,n.reason_key," + ActionableSql("n.state") + " FROM notice n " +
            "JOIN pair p ON p.pair_no=n.pair_no WHERE n.pair_no=$p AND n.notice_id=$id;");
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$id", noticeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (NoticeDetailCodec.Decode(new Notice(reader.GetString(1), (int)reader.GetInt64(2),
            Enum.Parse<NoticeKind>(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(0))), !reader.GetBoolean(7));
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
        CloseWhere(transaction, choice, "notice_id=$id AND pair_no=$p AND " + ActionableSql("state") + ";",
            ("$id", noticeId), ("$p", pairNo));

    // A kept rebuildable rule is dismissed for that exact rule identity; changing the rule changes the id.
    private bool IsKeptRule(SqliteTransaction transaction, long pairNo, string noticeId)
    {
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT choice FROM notice WHERE pair_no=$p AND notice_id=$id AND state='closed';");
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$id", noticeId);
        return command.ExecuteScalar() is string choice && string.Equals(choice, NoticeChoices.KeepRule, StringComparison.Ordinal);
    }

    private bool IsSameProblem(SqliteTransaction transaction, string noticeId, long pairNo, Notice notice)
    {
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT pair_no, rule, kind, path, reason_key, detail FROM notice WHERE notice_id=$id;");
        command.Parameters.AddWithValue("$id", noticeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        // Decode the stored row independently: a plain-text detail must not inherit the incoming operation.
        var saved = NoticeDetailCodec.Decode(new Notice(notice.PairId, (int)reader.GetInt64(1),
            Enum.Parse<NoticeKind>(reader.GetString(2)), reader.GetString(3), reader.GetString(5),
            reader.GetString(4), noticeId, ConflictOperation: null));
        // One Recovery item keeps one notice row: its reason may move pending <-> ready and its destination may be
        // retried, but the stored recovery identity must be exactly the incoming one.
        var savedReason = reader.GetString(4);
        var savedRecoveryId = saved.ConflictOperation?.Choice;
        var incomingRecoveryId = notice.ConflictOperation?.Choice;
        var recoveryItem = IsRecoveryRestoreReason(savedReason) && IsRecoveryRestoreReason(notice.ReasonKey) &&
            !string.IsNullOrEmpty(savedRecoveryId) && !string.IsNullOrEmpty(incomingRecoveryId) &&
            string.Equals(savedRecoveryId, incomingRecoveryId, StringComparison.Ordinal);
        if (IsRecoveryRestoreReason(savedReason) || IsRecoveryRestoreReason(notice.ReasonKey))
            return reader.GetInt64(0) == pairNo && recoveryItem;
        // Storage families may refresh their cause in place; the captured entity/operation must still match.
        if (notice.Storage is not null)
            return reader.GetInt64(0) == pairNo && reader.GetInt64(1) == notice.Rule && SameStorageProblem(saved, notice);
        var sameReason = string.Equals(savedReason, notice.ReasonKey, StringComparison.Ordinal);
        var sameRuleAndPath = recoveryItem || (reader.GetInt64(1) == notice.Rule &&
            (notice.Repair is { } repair
                ? saved.Repair is { } prior && prior.Side == repair.Side && prior.Item.ObjectId == repair.Item.ObjectId
                : notice.RebuildableRule is { } rule
                    ? saved.RebuildableRule is { } savedRule && string.Equals(savedRule.Category, rule.Category, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(RowCodec.Key(savedRule.Prefix), RowCodec.Key(rule.Prefix), StringComparison.Ordinal)
                    : string.Equals(RowCodec.Key(reader.GetString(3)), RowCodec.Key(notice.Path), StringComparison.Ordinal)));
        return reader.GetInt64(0) == pairNo && sameRuleAndPath && sameReason &&
            saved.RetainedVersion?.ActionId == notice.RetainedVersion?.ActionId;
    }
}
