namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    /// <summary>Open pair-cause rows for exactly the two storage pair families; never item notices.</summary>
    internal IReadOnlyList<StorageNotice> OpenStoragePairCauses(string pairId, string rootId, string remoteBinding) => InStore(() =>
    {
        var pair = RequirePair(pairId);
        var prefixes = new[]
        {
            StorageNotices.Prefix(new StorageNotice(rootId, "", remoteBinding, "availability")),
            StorageNotices.Prefix(new StorageNotice(rootId, "", remoteBinding, StorageNotices.HydrationPairOperation))
        };
        var result = new List<StorageNotice>(2);
        foreach (var prefix in prefixes)
        {
            // Each statement pins pair_no and its own reason range so LIMIT 1 is a true index bound even
            // when the pair has no pair-family row and the table holds a large item-notice population.
            var detail = StoreSchema.Scalar(_connection, null, $"""
                SELECT detail FROM notice INDEXED BY nt_pair_reason
                  WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND {NoticeTable.ActionableSql("state")}
                    AND COALESCE(choice,'')<>'ignore' LIMIT 1;
                """, ("$p", pair), ("$lo", prefix), ("$hi", prefix + "￿")) as string;
            if (detail is not null &&
                NoticeDetailCodec.Decode(new Notice(pairId, 0, NoticeKind.Info, "", detail, "")).Storage is { } storage)
                result.Add(storage);
        }
        return (IReadOnlyList<StorageNotice>)result;
    });

    /// <summary>Updates the one open item/operation notice in place; ignored causes remain immutable.</summary>
    internal string? ReportStorageNotice(string pairId, Notice notice, string familyPrefix) => InStore(() =>
    {
        var pair = RequirePair(pairId);
        using var tx = _connection.BeginTransaction();
        var id = ReportStorageNotice(tx, pair, notice, familyPrefix);
        tx.Commit();
        return id;
    });

    /// <summary>The transaction-scoped form used by aggregate writers, so one decision cannot race its own family.</summary>
    private string? ReportStorageNotice(Microsoft.Data.Sqlite.SqliteTransaction tx, long pair, Notice notice, string familyPrefix)
    {
        if (notice.Storage is null || notice.ReasonKey is null || !notice.ReasonKey.StartsWith(familyPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A storage notice must belong to its supplied family.", nameof(notice));
        var args = new (string, object?)[] { ("$p", pair), ("$reason", notice.ReasonKey), ("$lo", familyPrefix), ("$hi", familyPrefix + "￿") };
        if (StoreSchema.Scalar(_connection, tx, "SELECT 1 FROM notice WHERE pair_no=$p AND reason_key=$reason AND choice='ignore' LIMIT 1;", args) is not null)
        {
            _notices.CloseWhere(tx, "resolved",
                "pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND " + NoticeTable.ActionableSql("state") + " AND COALESCE(choice,'')<>'ignore';", args);
            return null;
        }
        // Prefer the existing open identity even when a historical row has the incoming cause.
        var id = StoreSchema.Scalar(_connection, tx,
            "SELECT notice_id FROM notice WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND " + NoticeTable.ActionableSql("state") + " AND COALESCE(choice,'')<>'ignore' LIMIT 1;", args) as string;
        id ??= StoreSchema.Scalar(_connection, tx, "SELECT notice_id FROM notice WHERE pair_no=$p AND reason_key=$reason AND COALESCE(choice,'')<>'ignore' LIMIT 1;", args) as string;
        if (id is null)
        {
            // Notice ids ordinarily derive from the cause. A prior in-place update can have retained
            // that id for another cause, so use an independent stable id for this new lifecycle.
            id = Guid.NewGuid().ToString("N");
            StoreSchema.Execute(_connection, tx,
                "INSERT INTO notice(notice_id,pair_no,rule,kind,path,detail,reason_key,state,choice,created) VALUES($id,$p,$rule,$kind,$path,$detail,$reason,'open',NULL,$now);",
                ("$id", id), ("$p", pair), ("$rule", notice.Rule), ("$kind", notice.Kind.ToString()), ("$path", notice.Path),
                ("$detail", NoticeDetailCodec.Encode(notice)), ("$reason", notice.ReasonKey), ("$now", NowMs()));
        }
        else
        {
            StoreSchema.Execute(_connection, tx,
                "UPDATE notice SET rule=$rule,kind=$kind,path=$path,detail=$detail,reason_key=$reason WHERE notice_id=$id;",
                ("$id", id), ("$rule", notice.Rule), ("$kind", notice.Kind.ToString()), ("$path", notice.Path), ("$detail", NoticeDetailCodec.Encode(notice)), ("$reason", notice.ReasonKey));
            _notices.ReopenWhere(tx, "notice_id=$id;", ("$id", id));
        }
        _notices.CloseWhere(tx, "resolved",
            "pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND " + NoticeTable.ActionableSql("state") + " AND notice_id<>$id AND COALESCE(choice,'')<>'ignore';",
            ("$p", pair), ("$lo", familyPrefix), ("$hi", familyPrefix + "￿"), ("$id", id));
        return id;
    }
}
