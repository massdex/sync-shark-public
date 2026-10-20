namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    /// <summary>Checks open network/auth causes in the unified hydration pair range; excludes provider, backlog and item causes.</summary>
    internal bool HasOpenStoragePairCause(string pairId) => InStore(() =>
        StoreSchema.Scalar(_connection, null, """
            SELECT 1 FROM notice INDEXED BY nt_pair_reason
              WHERE pair_no=$p AND reason_key>='storage:hydration-pair:'
                AND reason_key<'storage:hydration-pair:￿' AND state='open'
                AND (reason_key GLOB 'storage:hydration-pair:*:network:*'
                  OR reason_key GLOB 'storage:hydration-pair:*:auth:*') LIMIT 1;
            """, ("$p", RequirePair(pairId))) is not null);

    /// <summary>Updates the one open item/operation notice in place; ignored causes remain immutable.</summary>
    internal string? ReportStorageNotice(string pairId, Notice notice, string familyPrefix) => InStore(() =>
    {
        if (notice.Storage is null || notice.ReasonKey is null || !notice.ReasonKey.StartsWith(familyPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A storage notice must belong to its supplied family.", nameof(notice));
        var pair = RequirePair(pairId);
        using var tx = _connection.BeginTransaction();
        var args = new (string, object?)[] { ("$p", pair), ("$reason", notice.ReasonKey), ("$lo", familyPrefix), ("$hi", familyPrefix + "￿") };
        if (StoreSchema.Scalar(_connection, tx, "SELECT 1 FROM notice WHERE pair_no=$p AND reason_key=$reason AND choice='ignore' LIMIT 1;", args) is not null)
        {
            StoreSchema.Execute(_connection, tx, "UPDATE notice SET state='closed',choice='resolved' WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND state='open' AND COALESCE(choice,'')<>'ignore';", args);
            tx.Commit();
            return null;
        }
        // Prefer the existing open identity even when a historical row has the incoming cause.
        var id = StoreSchema.Scalar(_connection, tx,
            "SELECT notice_id FROM notice WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND state='open' AND COALESCE(choice,'')<>'ignore' LIMIT 1;", args) as string;
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
        else StoreSchema.Execute(_connection, tx,
            "UPDATE notice SET rule=$rule,kind=$kind,path=$path,detail=$detail,reason_key=$reason,state='open',choice=NULL WHERE notice_id=$id;",
            ("$id", id), ("$rule", notice.Rule), ("$kind", notice.Kind.ToString()), ("$path", notice.Path), ("$detail", NoticeDetailCodec.Encode(notice)), ("$reason", notice.ReasonKey));
        StoreSchema.Execute(_connection, tx,
            "UPDATE notice SET state='closed',choice='resolved' WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND state='open' AND notice_id<>$id AND COALESCE(choice,'')<>'ignore';",
            ("$p", pair), ("$lo", familyPrefix), ("$hi", familyPrefix + "￿"), ("$id", id));
        tx.Commit();
        return id;
    });
}
