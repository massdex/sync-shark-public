using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void NoticeRegressionChecks(string root)
    {
        DedicatedRetryPublicationChecks(root);
        NoticePointLookupChecks(root);
        NoticeLookupUpgradeChecks(root);
        ScanNoticeAggregationRetainsDebt(root);
        RunCauseSuppressionChecks(root);
    }

    private static void DedicatedRetryPublicationChecks(string root)
    {
        var folder = Fresh(root, "dedicated-retry-publication");
        using var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        foreach (var reason in new[] { ExecutionReasons.ConflictCopyBlocked, InterruptedDelete.UnverifiedReason,
            "deletion-evidence-pending", "unreadable" })
        foreach (var dedicatedFirst in new[] { false, true })
        {
            var path = reason + (dedicatedFirst ? "/first" : "/last");
            var dedicated = new Notice("p1", reason == "unreadable" ? 30 : 40, NoticeKind.Info,
                path, "Dedicated explanation", reason);
            var generic = FailureRetryOwner.RetryNotice("p1", path, "stable-cause", 3);
            store.AddNotices(dedicatedFirst ? [dedicated, generic] : [generic, dedicated]);
            var active = store.UnresolvedNotices("p1").Where(n => n.Path == path).ToArray();
            Require(active.Length == 1 && active[0].ReasonKey == reason,
                "dedicated notice supersedes generic retry in both publication orders: " + reason);
            store.CloseNotice(active[0].NoticeId!, NoticeChoices.Ignore);
            store.AddNotices([generic with { CauseKey = active[0].CauseKey }]);
            Require(store.UnresolvedNotices("p1").Count(n => n.Path == path) == 1 &&
                store.OpenNotices("p1").All(n => n.Path != path),
                "ignored dedicated cause retains authority without a duplicate retry: " + reason);
            if (reason == "unreadable")
                Require(store.NoticeById("p1", active[0].NoticeId!)?.Notice.FailureRetry?.Attempts == 3,
                    "unreadable folding retains refreshed retry evidence");
        }
    }

    private static void NoticePointLookupChecks(string root)
    {
        var folder = Fresh(root, "notice-point-lookup");
        using var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        store.SavePair(Pair("p2"));
        var notice = new Notice("p1", 40, NoticeKind.Info, "Parent/item.txt", "Pending", "probe-reason");
        store.AddNotices([notice, notice with { Path = "Parent/other.txt" }, notice with { PairId = "p2" }]);
        var id = store.OpenNotices("p1").Single(n => n.Path == notice.Path).NoticeId!;
        store.CloseNotice(id, NoticeChoices.Ignore);
        Require(store.HasUnresolvedNotice("p1", notice.ReasonKey, "PARENT/ITEM.TXT"),
            "point lookup includes ignored cause and canonical path casing");
        store.CloseUnresolvedNotices("p1", notice.ReasonKey, "resolved", "PARENT/ITEM.TXT");
        Require(store.NoticeById("p1", id) is { Closed: true } &&
            store.HasUnresolvedNotice("p1", notice.ReasonKey, "Parent/other.txt") &&
            store.HasUnresolvedNotice("p2", notice.ReasonKey, notice.Path),
            "exact closure never reaches another path or pair");
        Require(!store.HasUnresolvedNotice("missing", notice.ReasonKey, notice.Path), "unknown pair lookup is empty");
        store.CloseUnresolvedNotices("p1", notice.ReasonKey, "resolved");
        Require(store.UnresolvedNotices("p1").Count == 0 && store.UnresolvedNotices("p2").Count == 1,
            "reason-only closure is pair scoped");
        RequireIndexedNoticeProbe(folder);
    }

    private static void RequireIndexedNoticeProbe(string folder)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT 1 FROM notice INDEXED BY nt_pair_reason_path " +
            "WHERE pair_no=1 AND reason_key='probe-reason' AND path_key='PARENT/ITEM.TXT' " +
            "AND state IN ('open','aggregated') LIMIT 1;";
        using var reader = command.ExecuteReader();
        Require(reader.Read() && reader.GetString(3).Contains("SEARCH notice USING INDEX nt_pair_reason_path", StringComparison.Ordinal) &&
            reader.GetString(3).Contains("pair_no=? AND reason_key=? AND path_key=?", StringComparison.Ordinal),
            "point probe constrains all index dimensions, independent of notice population");
    }

    private static void NoticeLookupUpgradeChecks(string root)
    {
        var folder = Fresh(root, "notice-lookup-upgrade");
        using (var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(Pair("p1"));
            store.AddNotices([new("p1", 40, NoticeKind.Info, "item", "Pending", "probe-reason")]);
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX nt_pair_reason_path;";
            command.ExecuteNonQuery();
        }
        using var reopened = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        Require(reopened.HasUnresolvedNotice("p1", "probe-reason", "item"),
            "compatible existing database gains point index without losing notices");
        RequireIndexedNoticeProbe(folder);
    }

    private static void ScanNoticeAggregationRetainsDebt(string root)
    {
        var folder = Fresh(root, "scan-notice-aggregation");
        using var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        var cause = OwnedPointerProof.ObservationMessage;
        var retry = new SyncRetry("Parent/item.txt", "scan", 2, cause, DateTimeOffset.MaxValue);
        store.SaveSyncRetry("p1", retry);
        store.AddNotices([FailureRetryOwner.RetryNotice("p1", retry.Path, cause, 2),
            new("p1", 40, NoticeKind.Info, "", "Pending items", NoticeChoices.PointerObservationSummary)]);
        var item = store.OpenNotices("p1").Single(n => n.Path == retry.Path);
        var summary = store.OpenNotices("p1").Single(n => n.Path == "");
        store.CloseNotice(summary.NoticeId!, NoticeChoices.Ignore);
        store.CloseOwnedPointerObservationNotices("p1", cause);
        Require(store.OpenNotices("p1").Count == 0 && store.NoticeById("p1", item.NoticeId!) is { Closed: false },
            "ignored summary hides its members without resolving their still-failing notices");
        Require(store.SyncRetryAt("p1", retry.Path) == retry &&
            store.HasUnresolvedNotice("p1", "sync-retry", retry.Path), "aggregation retains exact retry debt and authority");
        store.ClearSyncRetry("p1", retry.Path);
        Require(store.NoticeById("p1", item.NoticeId!) is { Closed: true }, "success alone resolves the aggregated item");
        store.ExpireIgnoredOwnedPointerObservationSummary("p1");
        Require(store.NoticeById("p1", summary.NoticeId!) is { Closed: true }, "success retires ignored summary");
    }

    private static void RunCauseSuppressionChecks(string root)
    {
        var folder = Fresh(root, "run-cause-suppression");
        using var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        var now = DateTimeOffset.UtcNow;
        FailureRetryOwner.RecordRunFailure(store, "p1", new IOException("attempt one", 1), now, _ => false);
        var id = FailureRetryOwner.RunNoticeId("p1");
        store.CloseNotice(id, NoticeChoices.Ignore);
        FailureRetryOwner.RecordRunFailure(store, "p1", new IOException("attempt two", 1), now.AddMinutes(1), _ => false);
        Require(store.OpenNotices("p1").Count == 0 &&
            store.NoticeById("p1", id)?.Notice.FailureRetry?.Attempts == 2,
            "run suppression survives variable display text while durable attempts advance");
        FailureRetryOwner.RecordRunFailure(store, "p1", new IOException("attempt three", 2), now.AddMinutes(2), _ => false);
        Require(store.OpenNotices("p1").Single().NoticeId == id, "changed stable error code resurfaces the same run notice");
        Require(FailureCauseIdentity.Of(new StoragePairException("network", "one")) ==
                FailureCauseIdentity.Of(new StoragePairException("network", "two")) &&
            FailureCauseIdentity.Of(new StoragePairException("network", "one")) !=
                FailureCauseIdentity.Of(new StoragePairException("volume", "one")),
            "typed storage cause, not display text, distinguishes episodes");
    }
}
