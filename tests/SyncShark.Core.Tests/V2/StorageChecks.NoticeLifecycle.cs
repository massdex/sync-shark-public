using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    // E9 / cloud-files.md Ignore decision: a long-running outage is not terminal history. Retention starts at closure.
    private static void NoticeLifecycleChecks(string root)
    {
        var folder = Fresh(root, "notice-lifecycle");
        var clock = new TestClock();
        var notice = new Notice("p1", 40, NoticeKind.Info, "Parent/file.txt", "Unavailable; retry after reconnecting.", "sync-retry");
        string id;
        using (var store = SqliteStateStore.Open(folder, Fast, clock, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(Pair("p1"));
            store.AddNotices([notice]);
            id = store.OpenNotices("p1").Single().NoticeId!;
            clock.UtcNow += Fast.HistoryRetention + TimeSpan.FromDays(1);
            store.Prune();
            Require(store.NoticeById("p1", id) is { Closed: false }, "active old notice never age-prunes");
            store.CloseNotice(id, NoticeChoices.Ignore);
            Require(store.UnresolvedNotices("p1").Any(item => item.NoticeId == id),
                "suppressed failure remains an input to retry and auto-close work");
            clock.UtcNow += Fast.HistoryRetention + TimeSpan.FromDays(1);
            store.Prune();
            store.AddNotices([notice]);
            Require(store.NoticeById("p1", id) is { Closed: false } && store.OpenNotices("p1").Count == 0,
                "Ignore survives an outage longer than history retention without resurfacing");
        }
        using (var store = SqliteStateStore.Open(folder, Fast, clock, LocalItems.CaptureOptionalHash))
        {
            store.AddNotices([notice]);
            Require(store.OpenNotices("p1").Count == 0, "Ignore episode survives cold reopen");
            store.AddNotices([notice with { CauseKey = "permission-denied" }]);
            Require(store.OpenNotices("p1").Single().NoticeId == id,
                "new cause resurfaces one entity notice instead of duplicating it");
            store.CloseNotice(id, NoticeChoices.Ignore);
            store.AddNotices([notice with { CauseKey = "permission-denied", Detail = "Retry attempt increased", FailureRetry = new(3, clock.UtcNow) }]);
            Require(store.OpenNotices("p1").Count == 0 && store.NoticeById("p1", id)?.Notice.FailureRetry?.Attempts == 3,
                "same ignored cause refreshes durable retry metadata without resurfacing");
            store.CloseNotice(id, "resolved");
            store.Prune();
            Require(store.NoticeById("p1", id) is { Closed: true }, "resolution starts retention now, not at creation or Ignore");
            clock.UtcNow += Fast.HistoryRetention - TimeSpan.FromDays(1);
            store.CloseNotice(id, "resolved");
            store.Prune();
            Require(store.NoticeById("p1", id) is not null, "terminal history remains for its full window");
            clock.UtcNow += TimeSpan.FromDays(2);
            store.Prune();
            Require(store.NoticeById("p1", id) is null, "repeated auto-close does not renew terminal retention");
            store.AddNotices([notice, notice with { Path = "PARENT/FILE.TXT" }]);
            Require(store.OpenNotices("p1").Count == 1, "same entity/problem deduplicates case-insensitively");
            var unreadable = new Notice("p1", 30, NoticeKind.Info, "Parent/unreadable.txt", "Cannot read", "unreadable");
            store.AddNotices([unreadable]);
            var unreadableId = store.OpenNotices("p1").Single(n => n.Path == unreadable.Path).NoticeId!;
            store.CloseNotice(unreadableId, NoticeChoices.Ignore);
            store.AddNotices([FailureRetryOwner.RetryNotice("p1", unreadable.Path, "scan denied")]);
            Require(store.UnresolvedNotices("p1").Count(n => n.Path == unreadable.Path) == 1 &&
                store.NoticeById("p1", unreadableId)?.Notice.CauseKey == "scan denied",
                "generic scan failure refreshes the one unreadable entity notice");
            store.ClearSyncRetry("p1", unreadable.Path);
            Require(store.NoticeById("p1", unreadableId) is { Closed: true }, "successful scan closes unreadable notice with retry debt");
        }

        using (var store = SqliteStateStore.Open(folder, Fast, clock, LocalItems.CaptureOptionalHash))
        {
            var frozen = new SyncAction("rrs-fixture", 40, ActionKind.CopyNew, Side.B,
                "Parent/restore.txt", null, null, null, false, null) { SuppressReconciledBaseline = true };
            var restore = new Notice("p1", 40, NoticeKind.Info, frozen.Path, "Restore outcome unknown; retry.",
                "recovery-restore-pending", ConflictOperation: new("receipt-fixture", null, [frozen], []));
            store.AddNotices([restore]);
            var restoreId = store.OpenNotices("p1").Single(item => item.ReasonKey == restore.ReasonKey).NoticeId!;
            store.CloseNotice(restoreId, NoticeChoices.Ignore);
            Require(store.OpenNotices("p1").All(item => item.NoticeId != restoreId) &&
                store.RecoveryRestoreNoticesPage("p1").Any(item => item.NoticeId == restoreId),
                "suppressed restore remains in the retry work page but not the inbox");
            Require(store.HasPendingRestoreHolds("p1") && store.RecoveryRestoreProtected("p1", frozen.Path),
                "suppressed frozen restore retains deletion/overwrite protection");
            clock.UtcNow += Fast.HistoryRetention + TimeSpan.FromDays(1);
            store.Prune();
            Require(store.NoticeById("p1", restoreId) is { Closed: false }, "protective ignored restore never ages out");
        }

        // Every bulk closer uses the same timestamp owner, not an independent creation-age rule.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
        connection.Open();
        var table = new NoticeTable(connection, clock);
        table.CloseFamily(1, "sync-", null);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT closed_at FROM notice WHERE notice_id=$id;";
        command.Parameters.AddWithValue("$id", id);
        Require(Convert.ToInt64(command.ExecuteScalar()) == clock.UtcNow.ToUnixTimeMilliseconds(),
            "bulk family closer stamps closure using the store clock");
    }
}
