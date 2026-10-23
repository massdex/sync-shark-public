using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal sealed partial class NoticeTable
{
    /// <summary>Storage families use the same publication and suppression transitions as all retry notices.</summary>
    internal string? AddStorage(SqliteTransaction tx, long pair, Notice notice, string family, long now)
    {
        if (notice.Storage is null || !notice.ReasonKey.StartsWith(family, StringComparison.Ordinal))
            throw new ArgumentException("A storage notice must belong to its supplied family.", nameof(notice));
        var args = new (string, object?)[] { ("$p", pair), ("$reason", notice.ReasonKey),
            ("$lo", family), ("$hi", family + "￿") };
        // An ignored cause keeps its row. Otherwise preserve the current visible family's identity,
        // including legacy random ids, and let Add refresh the explanation and episode evidence.
        var id = StoreSchema.Scalar(_connection, tx,
            "SELECT notice_id FROM notice INDEXED BY nt_pair_reason WHERE pair_no=$p AND reason_key=$reason AND " +
            ActionableSql("state") + " AND " + IgnoredSql("choice") + " LIMIT 1;", args) as string;
        id ??= StoreSchema.Scalar(_connection, tx,
            "SELECT notice_id FROM notice INDEXED BY nt_pair_reason WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND " +
            VisibleSql("state") + " AND NOT (" + IgnoredSql("choice") + ") LIMIT 1;", args) as string;
        id ??= StoreSchema.Scalar(_connection, tx,
            "SELECT notice_id FROM notice INDEXED BY nt_pair_reason WHERE pair_no=$p AND reason_key=$reason LIMIT 1;", args) as string;
        id ??= StorageIdentity(pair, notice, tx);
        Add(tx, pair, notice with { NoticeId = id }, now);
        // A newer explanation supersedes the old one; no entity debt is declared successful.
        CloseWhere(tx, "superseded", "pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND " +
            ActionableSql("state") + " AND notice_id<>$id AND NOT (" + IgnoredSql("choice") + ");",
            [.. args, ("$id", id)]);
        return IsIgnored(pair, notice.ReasonKey, tx) ? null : id;
    }

    private static bool SameStorageProblem(Notice saved, Notice incoming) =>
        saved.Storage is { } old && incoming.Storage is { } current &&
        old.RootId == current.RootId && old.ObjectId == current.ObjectId &&
        old.RemoteBinding == current.RemoteBinding && old.Operation == current.Operation &&
        saved.RetainedVersion?.ActionId == incoming.RetainedVersion?.ActionId;

    private string StorageIdentity(long pair, Notice notice, SqliteTransaction tx)
    {
        var id = Id(notice);
        // A visible row can retain a former cause's id. Derive a collision-free successor from that
        // row rather than generating random identity; retries find the persisted cause by its index.
        while (ById(pair, id, tx) is { } occupied)
            id = ShortId.Make("ns", notice.PairId, notice.ReasonKey, id, occupied.Notice.ReasonKey);
        return id;
    }

    internal bool IsIgnored(long pair, string reason, SqliteTransaction? tx = null) =>
        StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM notice INDEXED BY nt_pair_reason WHERE pair_no=$p AND reason_key=$reason AND " +
            ActionableSql("state") + " AND " + IgnoredSql("choice") + " LIMIT 1;",
            ("$p", pair), ("$reason", reason)) is not null;
}
