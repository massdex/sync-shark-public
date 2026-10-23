using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void NoticeUpgradeChecks(string root)
    {
        var folder = Fresh(root, "notice-upgrade");
        var clock = new TestClock();
        var pair = Pair("p1") with { Excluded = ["Excluded"] };
        string excludedId, activeId;
        using (var store = SqliteStateStore.Open(folder, Fast, clock))
        {
            store.SavePair(pair);
            store.AddNotices([new("p1", 40, NoticeKind.Info, "Excluded/item", "failed", "sync-retry"),
                new("p1", 40, NoticeKind.Info, "Active/item", "failed", "sync-retry")]);
            excludedId = store.OpenNotices("p1").Single(n => n.Path.StartsWith("Excluded", StringComparison.Ordinal)).NoticeId!;
            activeId = store.OpenNotices("p1").Single(n => n.Path.StartsWith("Active", StringComparison.Ordinal)).NoticeId!;
        }
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE notice SET state='closed',choice='ignore',closed_at=NULL; PRAGMA user_version=22;";
            command.ExecuteNonQuery();
        }
        using (var store = SqliteStateStore.Open(folder, Fast, clock))
        {
            Require(store.NoticeById("p1", excludedId) is { Closed: true }, "legacy excluded-path Ignore remains terminal");
            Require(store.NoticeById("p1", activeId) is { Closed: false } && store.OpenNotices("p1").Count == 0,
                "legacy nonexcluded Ignore becomes suppressed actionable");
            store.AddNotices([new("p1", 40, NoticeKind.Info, "Excluded/new", "failed", "sync-retry")]);
            var newer = store.OpenNotices("p1").Single().NoticeId!;
            store.CloseNotice(newer, NoticeChoices.Ignore);
            store.SavePairIncluded(pair, pair with { Excluded = [] });
            Require(store.OpenNotices("p1").Any(n => n.NoticeId == newer), "re-including clears actionable Ignore suppression");
        }
        using (var archive = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(folder), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            archive.Open();
            StoreSchema.Verify(archive, clock);
            using var unchanged = archive.CreateCommand();
            unchanged.CommandText = "SELECT state,choice FROM notice WHERE notice_id=$id;";
            unchanged.Parameters.AddWithValue("$id", activeId);
            using var snapshot = unchanged.ExecuteReader();
            Require(snapshot.Read() && snapshot.GetString(0) == "open" && snapshot.GetString(1) == NoticeChoices.Ignore,
                "current-schema read-only verification preserves suppressed notice state without replaying migration");
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
        connection.Open();
        var notices = new NoticeTable(connection, clock);
        using var tx = connection.BeginTransaction();
        notices.CloseWhere(tx, "aggregated", "notice_id=$id;", ("$id", activeId));
        using var inspect = connection.CreateCommand();
        inspect.Transaction = tx;
        inspect.CommandText = "SELECT state,choice,closed_at FROM notice WHERE notice_id=$id;";
        inspect.Parameters.AddWithValue("$id", activeId);
        using (var reader = inspect.ExecuteReader())
            Require(reader.Read() && reader.GetString(0) == "aggregated" && reader.GetString(1) == NoticeChoices.Ignore && reader.IsDBNull(2),
                "aggregation preserves Ignore and has no terminal timestamp");
        notices.CloseResolved(tx, 1, activeId, "accepted");
        notices.CloseResolved(tx, 1, activeId, "superseded");
        using (var reader = inspect.ExecuteReader())
            Require(reader.Read() && reader.GetString(0) == "closed" && reader.GetString(1) == "accepted" && !reader.IsDBNull(2),
                "resolution closes aggregated authority but never overwrites a terminal choice");
    }
}
