using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed record DeleteNoticeItem(string Path, string Detail, string? NoticeId);
public sealed record DeleteNoticePage(IReadOnlyList<DeleteNoticeItem> Items, string? NextToken);

public sealed partial class SqliteStateStore
{
    internal const string UncertainDeleteSummary = "uncertain-delete-summary";
    internal const string DeleteReviewSummary = "deletion-review-summary";
    private const string UncertainDeleteWhere = "pair_no=$p AND state='uncertain' AND json_extract(action_json,'$.Kind')='Delete'";
    private const string DeleteReviewWhere = "pair_no=$p AND json_extract(json,'$.MissingDeleteIdentityReview')=1";

    public DeleteNoticePage ReadDeleteNoticeItems(string pairId, bool review, string? after = null, int limit = 50) => InStore(() =>
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var key = review ? "path_key" : "action_id";
        using var command = StoreSchema.Command(_connection, null,
            "SELECT " + key + "," + (review ? "json FROM sync_retry WHERE " + DeleteReviewWhere : "action_json FROM pending_effect WHERE " + UncertainDeleteWhere) +
            " AND " + key + (after is null ? ">=$after" : ">$after") + " ORDER BY " + key + " LIMIT $limit;");
        command.Parameters.AddWithValue("$p", RequirePair(pairId));
        command.Parameters.AddWithValue("$after", after ?? "");
        command.Parameters.AddWithValue("$limit", limit + 1);
        var rows = new List<DeleteNoticeItem>(limit);
        string? last = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (rows.Count == limit) return new DeleteNoticePage(rows, last);
            last = reader.GetString(0);
            if (review)
            {
                var retry = JsonSerializer.Deserialize<SyncRetry>(reader.GetString(1), RowCodec.Json)!;
                rows.Add(new(retry.Path, retry.LastError,
                    ShortId.Make("n", pairId, NoticeChoices.DeletionReviewRule.ToString(System.Globalization.CultureInfo.InvariantCulture), RowCodec.Key(retry.Path), "deletion-review")));
            }
            else
            {
                var action = JsonSerializer.Deserialize<SyncAction>(reader.GetString(1), RowCodec.Json)!;
                rows.Add(new(action.Path, UncertainDeleteDetail(action), null));
            }
        }
        return new DeleteNoticePage(rows, null);
    });

    private static string UncertainDeleteDetail(SyncAction action) => action.ProviderEffect?.Kind == "delete-submit"
        ? "Deletion outcome unknown; waiting for confirmation from Dropbox. Surviving copies stay protected; no new deletion is approved."
        : "Interrupted deletion could not be verified; sync retries both endpoints. No new deletion is approved.";

    private void RefreshDeleteReviewNotices(SqliteTransaction transaction, long pairNo, string pairId, long now, bool exact)
    {
        Refresh(false);
        Refresh(true);
        void Refresh(bool review)
        {
            var source = review ? "sync_retry WHERE " + DeleteReviewWhere : "pending_effect WHERE " + UncertainDeleteWhere;
            var summary = review ? DeleteReviewSummary : UncertainDeleteSummary;
            var reason = review ? "deletion-review" : "interrupted-delete-unverified";
            var count = (long)StoreSchema.Scalar(_connection, transaction,
                exact ? "SELECT COUNT(*) FROM " + source : "SELECT COUNT(*) FROM (SELECT 1 FROM " + source + " LIMIT 21);", ("$p", pairNo))!;
            if (count > 20)
            {
                // Aggregated decisions stay addressable by notice id; only the inbox list hides them.
                StoreSchema.Execute(_connection, transaction,
                    "UPDATE notice SET state='aggregated' WHERE pair_no=$p AND reason_key=$reason AND state='open'" +
                    (review ? " AND EXISTS (SELECT 1 FROM sync_retry WHERE " + DeleteReviewWhere + " AND json_extract(json,'$.Path')=notice.path)" : "") + ";",
                    ("$p", pairNo), ("$reason", reason));
                _notices.Add(transaction, pairNo, new Notice(pairId, 0, NoticeKind.Info, "",
                    $"{(exact ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "More than 20")} " +
                    (review ? "deletions need your review because the original source identity is unavailable. Show remaining offers Delete with Recovery, Keep and sync back, or Ignore for each item." :
                        "deletion outcomes remain unknown, waiting for endpoint confirmation (from Dropbox for pending cloud submissions). Surviving copies stay protected. Show remaining lists the pending items.") , summary), now);
            }
            else
            {
                StoreSchema.Execute(_connection, transaction,
                    "UPDATE notice SET state='closed',choice='resolved' WHERE pair_no=$p AND reason_key=$summary AND state='open';" +
                    "UPDATE notice SET state='open' WHERE pair_no=$p AND reason_key=$reason AND state='aggregated';",
                    ("$p", pairNo), ("$summary", summary), ("$reason", reason));
                if (!review)
                {
                    // Only the <=20 remaining intents need individual rows, including after a
                    // larger group shrinks. Never materialize the interrupted-intent backlog.
                    StoreSchema.Execute(_connection, transaction,
                        "UPDATE notice SET state='closed',choice='resolved' WHERE pair_no=$p AND reason_key=$reason AND " + NoticeTable.ActionableSql("state") + ";",
                        ("$p", pairNo), ("$reason", reason));
                    using var command = StoreSchema.Command(_connection, transaction,
                        "SELECT action_json FROM pending_effect WHERE " + UncertainDeleteWhere + " ORDER BY action_id LIMIT 20;");
                    command.Parameters.AddWithValue("$p", pairNo);
                    var actions = new List<SyncAction>(20);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) actions.Add(JsonSerializer.Deserialize<SyncAction>(reader.GetString(0), RowCodec.Json)!);
                    foreach (var action in actions)
                        _notices.Add(transaction, pairNo, new Notice(pairId, action.Rule, NoticeKind.Info, action.Path,
                            UncertainDeleteDetail(action), reason), now);
                }
            }
        }
    }
}
