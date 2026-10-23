using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed record BlockedDeleteItem(string Path, string Detail);
public sealed record BlockedDeletePage(IReadOnlyList<BlockedDeleteItem> Items, string? NextToken);

public sealed partial class SqliteStateStore
{
    internal const string BlockedDeleteSummary = "blocked-delete-summary";
    private const string BlockedDeleteWhere = "pair_no=$p AND json_extract(json,'$.DeleteBlockedDetail') IS NOT NULL";

    public BlockedDeletePage ReadBlockedDeletes(string pairId, string? after = null, int limit = 50) => InStore(() =>
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using var command = StoreSchema.Command(_connection, null,
            "SELECT path_key,json_extract(json,'$.Path'),json_extract(json,'$.DeleteBlockedDetail') FROM sync_retry WHERE " +
            BlockedDeleteWhere + (after is null ? " AND path_key>=$after" : " AND path_key>$after") + " ORDER BY path_key LIMIT $limit;");
        command.Parameters.AddWithValue("$p", RequirePair(pairId));
        command.Parameters.AddWithValue("$after", after ?? "");
        command.Parameters.AddWithValue("$limit", limit + 1);
        var items = new List<BlockedDeleteItem>(limit);
        string? last = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (items.Count == limit) return new BlockedDeletePage(items, last);
            last = reader.GetString(0);
            items.Add(new(reader.GetString(1), reader.GetString(2)));
        }
        return new BlockedDeletePage(items, null);
    });

    public void RefreshBlockedDeleteNotice(string pairId) => InStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        RefreshBlockedDeleteNotice(transaction, RequirePair(pairId), pairId, NowMs(), exact: true);
        RefreshDeleteReviewNotices(transaction, RequirePair(pairId), pairId, NowMs(), exact: true);
        transaction.Commit();
    });

    // Commit batches use LIMIT 21, not a growing full COUNT for every blocked item. The run-end
    // refresh counts the partial index once. No inventory or unbounded retry list is materialized.
    private void RefreshBlockedDeleteNotice(SqliteTransaction transaction, long pairNo, string pairId, long now, bool exact = false)
    {
        var count = (long)StoreSchema.Scalar(_connection, transaction,
            exact ? "SELECT COUNT(*) FROM sync_retry WHERE " + BlockedDeleteWhere + ";" :
                "SELECT COUNT(*) FROM (SELECT 1 FROM sync_retry WHERE " + BlockedDeleteWhere + " LIMIT 21);",
            ("$p", pairNo))!;
        // A provider-deferred/unknown batch gets no per-item notice even below the threshold.
        var deferred = count is > 0 and <= 20 && StoreSchema.Scalar(_connection, transaction,
            "SELECT 1 FROM sync_retry WHERE " + BlockedDeleteWhere +
            " AND json_extract(json,'$.DeleteBlockedDeferred')=1 LIMIT 1;", ("$p", pairNo)) is not null;
        if (count > 20 || deferred)
        {
            _notices.CloseWhere(transaction, "aggregated",
                "pair_no=$p AND reason_key=$reason AND " + NoticeTable.ActionableSql("state") + ";",
                ("$p", pairNo), ("$reason", DeleteSourceGuard.ReasonKey));
            _notices.Add(transaction, pairNo, new Notice(pairId, 0, NoticeKind.Info, "",
                $"{(exact || count <= 20 ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "More than 20")} deletions remain blocked because source absence or a required prerequisite could not be verified. " +
                "Nothing was deleted for these items. Held folders (including children without a baseline) remain protected. Sync retries automatically; Show remaining lists each path and why, 50 per page.",
                BlockedDeleteSummary), now);
            return;
        }
        _notices.CloseWhere(transaction, "resolved",
            "pair_no=$p AND reason_key=$reason AND " + NoticeTable.ActionableSql("state") + ";",
            ("$p", pairNo), ("$reason", BlockedDeleteSummary));
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT json_extract(json,'$.Path'),json_extract(json,'$.DeleteBlockedDetail') FROM sync_retry WHERE " + BlockedDeleteWhere + " ORDER BY path_key LIMIT 20;");
        command.Parameters.AddWithValue("$p", pairNo);
        var remaining = new List<BlockedDeleteItem>(20);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) remaining.Add(new(reader.GetString(0), reader.GetString(1)));
        foreach (var item in remaining)
            _notices.Add(transaction, pairNo, new Notice(pairId, 0, NoticeKind.Info, item.Path,
                item.Detail, DeleteSourceGuard.ReasonKey, DeleteSourceGuard.NoticeId(pairId, item.Path)), now);
    }
}
