using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal Action? IndexFailureTransitionForTest { get; set; }

    internal const string IndexFailurePrefix = NoticeVocabulary.IndexFailurePrefix;

    // The one notice cause for a root: a root-level pass failure, one item's metadata/hash failure,
    // a rejected proof, or a background rehash failure. These stable classes define root-summary
    // episodes; individual file names and diagnostic wording never change the suppression identity.
    private const string RootCause = "root";
    private const string ItemCause = "item";
    private const string ProofCause = "proof";
    private const string RehashCause = "rehash";

    /// <summary>A root with no future indexing deadline may be attempted; a fresh root is always due.</summary>
    public bool IndexRetryDue(string rootId) => InStore(() => StoreSchema.Scalar(_connection, null,
        "SELECT 1 FROM root WHERE root_id=$id AND index_retry_after<=$now;", ("$id", rootId), ("$now", NowMs())) is not null);

    /// <summary>
    /// The earliest future gap or rehash deadline for one root. Each leg is an indexed MIN: the
    /// (root_no,retry_after) partial indexes make it a bounded seek, not a whole-root scan. The literal
    /// retry_after&gt;0 keeps the partial indexes inferable from a bound retry_after&gt;$now term; every stored
    /// deadline is non-negative, so it never changes the result.
    /// </summary>
    internal const string NextIndexRetrySql = """
        SELECT MIN(due) FROM (
          SELECT MIN(retry_after) AS due FROM local_gap_item WHERE root_no=$r AND retry_after>$now AND retry_after>0
          UNION ALL
          SELECT MIN(retry_after) FROM local_rehash WHERE root_no=$r AND retry_after>$now AND retry_after>0);
        """;

    /// <summary>
    /// The next future durable indexing deadline, or null when work is due now. A future root-wide gate
    /// supersedes item deadlines; otherwise this is the earliest future gap or rehash deadline. Read-only.
    /// </summary>
    public DateTimeOffset? NextIndexRetry(string rootId) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return (DateTimeOffset?)null;
        var now = NowMs();
        var gate = StoreSchema.Scalar(_connection, null,
            "SELECT index_retry_after FROM root WHERE root_no=$r AND index_retry_after>$now;",
            ("$r", root.Value), ("$now", now));
        if (gate is not null and not DBNull) return DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(gate));
        var item = StoreSchema.Scalar(_connection, null, NextIndexRetrySql, ("$r", root.Value), ("$now", now));
        return item is null or DBNull ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(item));
    });

    /// <summary>Records one root-level indexing failure using the one retry owner and publishes its notice.</summary>
    public void RecordIndexFailure(string rootId, string reason) => InIndexingStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return;
        var previous = Convert.ToInt32(StoreSchema.Scalar(_connection, null,
            "SELECT index_failure_count FROM root WHERE root_no=$r;", ("$r", root.Value)));
        var retry = FailureRetryOwner.Failed(previous, _clock.GetUtcNow());
        var owner = IndexNoticeOwner(rootId);
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx,
            "UPDATE root SET index_failure_count=$n,index_retry_after=$due,index_failure_reason=$why WHERE root_no=$r;",
            ("$r", root.Value), ("$n", retry.Attempts), ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()),
            ("$why", reason));
        if (owner is { } noticeOwner) PublishIndexFailure(tx, noticeOwner, rootId, RootCause, reason, retry);
        tx.Commit();
    });

    /// <summary>
    /// Records one gap item's failure: the item keeps its durable row, gets a future deadline and a reason,
    /// and a changed cause reopens the root's one physical notice. No total attempt cap exists; the shared
    /// retry owner alone owns backoff.
    /// </summary>
    private void SaveGapFailure(string rootId, long root, string epoch, string objectId, string reason,
        string cause = ItemCause) => InIndexingStore(() =>
    {
        var owner = IndexNoticeOwner(rootId);
        using var tx = _connection.BeginTransaction();
        SaveGapFailure(tx, rootId, root, epoch, objectId, reason, cause, owner);
        tx.Commit();
    });

    private void SaveGapFailure(SqliteTransaction tx, string rootId, long root, string epoch,
        string objectId, string reason, string cause, (string PairId, long PairNo)? owner)
    {
        var count = StoreSchema.Scalar(_connection, tx,
            "SELECT failure_count FROM local_gap_item WHERE root_no=$r AND object_id=$o AND epoch=$e;",
            ("$r", root), ("$o", objectId), ("$e", epoch));
        if (count is null) return;
        var retry = FailureRetryOwner.Failed(Convert.ToInt32(count), _clock.GetUtcNow());
        StoreSchema.Execute(_connection, tx,
            "UPDATE local_gap_item SET failure_count=$n,failure_reason=$why,retry_after=$due,retry_allowed=1 WHERE root_no=$r AND object_id=$o AND epoch=$e;",
            ("$r", root), ("$o", objectId), ("$e", epoch), ("$n", retry.Attempts),
            ("$why", reason), ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()));
        if (owner is { } noticeOwner)
        {
            PublishIndexFailure(tx, noticeOwner, rootId, cause, reason, retry);
            if (ContentProofFailure.Decode(reason) is not null && _reads.ByObject(root, objectId, tx) is { } item)
                PublishIndexItemFailure(tx, noticeOwner, rootId, cause, reason, retry, item.Path);
        }
    }

    /// <summary>Records one background rehash failure against its exact attempt revision; a stale attempt is refused.</summary>
    public bool FailIndexingRehash(string rootId, ItemState item, long revision, string reason) => InIndexingStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return false;
        var count = StoreSchema.Scalar(_connection, null,
            "SELECT failure_count FROM local_rehash WHERE root_no=$r AND object_id=$o AND evidence_revision=$v;",
            ("$r", root.Value), ("$o", item.ObjectId), ("$v", revision));
        if (count is null) return false;
        var retry = FailureRetryOwner.Failed(Convert.ToInt32(count), _clock.GetUtcNow());
        var owner = IndexNoticeOwner(rootId);
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx,
            "UPDATE local_rehash SET failure_count=$n,retry_after=$due,reason=$why WHERE root_no=$r AND object_id=$o AND evidence_revision=$v;",
            ("$r", root.Value), ("$o", item.ObjectId), ("$v", revision), ("$n", retry.Attempts),
            ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()), ("$why", reason));
        if (owner is { } noticeOwner)
        {
            PublishIndexFailure(tx, noticeOwner, rootId, RehashCause, reason, retry);
            if (ContentProofFailure.Decode(reason) is not null)
                PublishIndexItemFailure(tx, noticeOwner, rootId, RehashCause, reason, retry, item.Path);
        }
        tx.Commit();
        return true;
    });

    /// <summary>
    /// An explicit Retry makes every durable indexing deadline due now without clearing attempt evidence or
    /// changing any exclusion; success alone clears counts.
    /// </summary>
    internal void RequestIndexRetry(string rootId) => InIndexingStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return;
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, "UPDATE root SET index_retry_after=0 WHERE root_no=$r;", ("$r", root.Value));
        StoreSchema.Execute(_connection, tx, "UPDATE local_gap_item SET retry_after=0,retry_allowed=1 WHERE root_no=$r;", ("$r", root.Value));
        StoreSchema.Execute(_connection, tx, "UPDATE local_rehash SET retry_after=0 WHERE root_no=$r;", ("$r", root.Value));
        tx.Commit();
    });

    /// <summary>
    /// Clears the root's failure evidence only when no protection or pending verification remains; the notice
    /// family closes with it. A partial pass never clears another item's failure.
    /// </summary>
    public void CompleteIndexFailure(string rootId) => InIndexingStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || HasGapProtection(root) || HasPendingRehashVerification(rootId)) return;
        // Only a root that still carries evidence may change; a clean root must not publish work.
        if (StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM root WHERE root_no=$r AND (index_failure_count!=0 OR index_failure_reason IS NOT NULL OR index_retry_after!=0);",
            ("$r", root.Value)) is null) return;
        var pairs = Pairs().Where(pair => pair.A.RootId == rootId || pair.B.RootId == rootId)
            .Select(pair => RequirePair(pair.PairId)).ToArray();
        using var tx = _connection.BeginTransaction();
        // Clear only what still exists; repeated completion of a restored root must change nothing.
        StoreSchema.Execute(_connection, tx,
            "UPDATE root SET index_failure_count=0,index_retry_after=0,index_failure_reason=NULL WHERE root_no=$r " +
            "AND (index_failure_count!=0 OR index_failure_reason IS NOT NULL OR index_retry_after!=0);", ("$r", root.Value));
        IndexFailureTransitionForTest?.Invoke();
        // One physical notice exists at the lowest pair; closing its family also resolves every projection.
        foreach (var pair in pairs)
            _notices.CloseFamily(pair, IndexFailurePrefix + rootId, null, tx);
        tx.Commit();
    });

    /// <summary>The lowest-numbered pair that owns the root's one physical notice; projections derive from it.</summary>
    private (string PairId, long PairNo)? IndexNoticeOwner(string rootId)
    {
        var owners = Pairs().Where(pair => pair.A.RootId == rootId || pair.B.RootId == rootId)
            .Select(pair => (PairId: pair.PairId, PairNo: RequirePair(pair.PairId)))
            .OrderBy(entry => entry.PairNo)
            .ToArray();
        return owners.Length == 0 ? null : owners[0];
    }

    /// <summary>
    /// Publishes (or refreshes) the root's one physical notice. Ignore suppresses this episode only: a changed
    /// cause reopens the same row with the current evidence, and the structured failure state always follows it.
    /// </summary>
    private void PublishIndexFailure(SqliteTransaction transaction, (string PairId, long PairNo) owner,
        string rootId, string cause, string reason, FailureRetryOwner.State retry, string path = "")
    {
        var reasonKey = IndexFailurePrefix + rootId;
        var notice = new Notice(owner.PairId, 40, NoticeKind.Info, path,
            MessageCatalog.Default.Message(path.Length == 0 ? "proof-notice-root" : "proof-notice-item") +
            (ContentProofFailure.Decode(reason)?.Display ?? reason) + MessageCatalog.Default.Message("proof-notice-guidance"),
            reasonKey)
        { FailureRetry = retry, CauseKey = IndexCause(rootId, cause) };
        var previous = ReadIndexNotice(transaction, owner, rootId, path);
        if (previous is { Choice: NoticeChoices.Ignore } prior &&
            !string.Equals(prior.Notice.CauseKey, notice.CauseKey, StringComparison.Ordinal))
        {
            // A different cause is a new episode. NOTICE owns the transition; Add below then refreshes the
            // current evidence. The same cause stays suppressed for the ignored episode.
            _notices.ReopenWhere(transaction, "notice_id=$id", ("$id", prior.Notice.NoticeId));
        }
        _notices.Add(transaction, owner.PairNo, notice, NowMs());
    }

    private (Notice Notice, string State, string? Choice)? ReadIndexNotice(SqliteTransaction transaction,
        (string PairId, long PairNo) owner, string rootId, string path = "")
    {
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT notice_id,path,detail,state,choice FROM notice WHERE pair_no=$p AND reason_key=$r AND path_key=$path " +
            "ORDER BY created DESC,notice_id DESC LIMIT 1;");
        command.Parameters.AddWithValue("$p", owner.PairNo);
        command.Parameters.AddWithValue("$r", IndexFailurePrefix + rootId);
        command.Parameters.AddWithValue("$path", RowCodec.Key(path));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var notice = NoticeDetailCodec.Decode(new Notice(owner.PairId, 40, NoticeKind.Info,
            reader.GetString(1), reader.GetString(2), IndexFailurePrefix + rootId, reader.GetString(0)));
        return (notice, reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private void CloseIndexItemFailure(SqliteTransaction tx, string rootId, string path) =>
        _notices.CloseWhere(tx, "resolved", "reason_key=$reason AND path_key=$path;",
            ("$reason", IndexFailurePrefix + rootId), ("$path", RowCodec.Key(path)));

    /// <summary>Summary suppression belongs to the root and failure class, never the latest item's detail.</summary>
    private static string IndexCause(string rootId, string failureKind) =>
        rootId + "|" + failureKind;

    /// <summary>The root id named by one index-failure notice reason, or null for any other reason.</summary>
    internal static string? IndexFailureRoot(string? reasonKey) =>
        reasonKey is not null && reasonKey.StartsWith(IndexFailurePrefix, StringComparison.Ordinal)
            ? reasonKey[IndexFailurePrefix.Length..]
            : null;

    /// <summary>
    /// Resolves an index notice opened from another pair's projection to the one physical notice, but only when
    /// the requesting pair actually contains that root. Null when the notice is gone or not this pair's concern.
    /// </summary>
    internal (string PairId, Notice Notice)? IndexNoticeById(string pairId, string noticeId) =>
        InStore<(string PairId, Notice Notice)?>(() =>
    {
        if (PairRecord(pairId) is not { } requested) return null;
        using var command = StoreSchema.Command(_connection, null,
            "SELECT n.notice_id,p.pair_id,n.rule,n.kind,n.path,n.detail,n.reason_key " +
            "FROM notice n JOIN pair p ON p.pair_no=n.pair_no " +
            "WHERE n.notice_id=$id AND n.state='open' AND n.reason_key>=$prefix AND n.reason_key<$end LIMIT 1;");
        command.Parameters.AddWithValue("$id", noticeId);
        command.Parameters.AddWithValue("$prefix", IndexFailurePrefix);
        command.Parameters.AddWithValue("$end", IndexFailurePrefix + '￿');
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var notice = NoticeDetailCodec.Decode(new Notice(reader.GetString(1), (int)reader.GetInt64(2),
            Enum.Parse<NoticeKind>(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(0)));
        var root = IndexFailureRoot(notice.ReasonKey);
        if (root is null || requested.A.RootId != root && requested.B.RootId != root) return null;
        return (notice.PairId, notice);
    });

    /// <summary>
    /// Adds the one physical notice's effect to every other pair that shares its root. The unresolved flag is
    /// forwarded so an inbox/action read sees exactly the same physical rows as its own pair read.
    /// </summary>
    private IReadOnlyList<Notice> ProjectIndexNotices(string? pairId, IReadOnlyList<Notice> notices,
        bool unresolved = false)
    {
        if (pairId is null || PairRecord(pairId) is not { } pair) return notices;
        var roots = new[] { pair.A.RootId, pair.B.RootId };
        var projected = roots.Distinct(StringComparer.Ordinal)
            .SelectMany(root => _notices.Open(null, reasonKey: IndexFailurePrefix + root, unresolved: unresolved))
            .Where(notice => notice.PairId != pairId).Select(notice => notice with { PairId = pairId });
        return notices.Concat(projected).ToArray();
    }
}
