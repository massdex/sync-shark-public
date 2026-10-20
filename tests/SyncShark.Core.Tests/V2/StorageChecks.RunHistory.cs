using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Checks the bounded, pair-keyed run summaries and their sequence paging .</summary>
internal static partial class StorageChecks
{
    internal static void RunHistoryChecks(string root)
    {
        RunHistoryPaging(Fresh(root, "run-history"));
        RunHistoryRetention(Fresh(root, "run-history-retention"));
        RunHistoryPresenceGuard(root);
    }

    private static RunSummary Run(string runId, string pairId, DateTimeOffset started, int uploaded = 0,
        int failed = 0, int held = 0, int uncertain = 0, int deferred = 0) =>
        new(0, runId, pairId, started, started.AddSeconds(1), RunOutcomes.Complete, uploaded + failed + held + 1,
            uploaded, 0, 0, 0, 0, held, failed, uncertain, 0, deferred);

    private static void RunHistoryPaging(string folder)
    {
        var clock = new TestClock { UtcNow = T0 };
        using var store = SqliteStateStore.Open(folder, Fast with { RunSummaryMaximumRows = 10 }, clock);
        // Four runs, two sharing one start instant: the sequence, never the timestamp, orders the pages.
        store.RecordRunSummary(Run("r1", "p1", T0.AddSeconds(1)));
        store.RecordRunSummary(Run("r2", "p1", T0.AddSeconds(2)));
        store.RecordRunSummary(Run("r3", "p2", T0.AddSeconds(2)));
        store.RecordRunSummary(Run("r4", "p1", T0.AddSeconds(3)));
        store.RecordRunSummary(Run("r1", "p1", T0.AddSeconds(1)));
        var all = store.ReadRunHistory(null, 10);
        Require(all.Select(row => row.RunId).SequenceEqual(["r4", "r3", "r2", "r1"]) && all.All(row => row.Seq > 0),
            "run summaries page newest first by their unique sequence");
        var first = store.ReadRunHistory(null, 2);
        Require(first.Select(row => row.RunId).SequenceEqual(["r4", "r3"]), "a bounded page returns the newest rows");
        var older = store.ReadRunHistory(null, 10, first[^1].Seq);
        Require(older.Select(row => row.RunId).SequenceEqual(["r2", "r1"]),
            "the exclusive sequence cursor returns the older rows without skipping a shared timestamp");
        Require(store.ReadRunHistory("p1", 10).Select(row => row.RunId).SequenceEqual(["r4", "r2", "r1"]),
            "a pair filter pages only that pair");
        Require(store.ReadRunHistoryPage("p1", 2, 1).Select(row => row.RunId).SequenceEqual(["r2", "r1"]),
            "offset paging matches the window's Load More contract");
        Require(store.ReadRunHistoryAfter(0, 10).Select(row => row.RunId).SequenceEqual(["r1", "r2", "r3", "r4"]),
            "the forward log cursor reads oldest first");
        Require(store.ReadRunHistoryAfter(store.ReadRunHistoryAfter(0, 10)[1].Seq, 10)
                .Select(row => row.RunId).SequenceEqual(["r3", "r4"]),
            "the forward cursor resumes after the exact row");
        Require(store.RunSummaryById("r3") is { PairId: "p2" } && store.RunSummaryById("missing") is null,
            "one summary is readable by its run id");
        var plans = store.RunHistoryQueryPlans();
        Require(plans.Any(line => line.Contains("run_summary_pair", StringComparison.Ordinal)) &&
            !plans.Any(line => line.Contains("TEMP B-TREE", StringComparison.Ordinal)),
            "run-history paging uses the pair and sequence indexes without a sort");
        Console.WriteLine("PASS run summary paging");
    }

    private static void RunHistoryRetention(string folder)
    {
        var clock = new TestClock { UtcNow = T0 };
        using var store = SqliteStateStore.Open(folder,
            Fast with { RunSummaryMaximumRows = 2, HistoryRetention = TimeSpan.FromDays(1) }, clock);
        store.RecordRunSummary(Run("a", "p1", T0.AddSeconds(1)));
        store.RecordRunSummary(Run("b", "p1", T0.AddSeconds(2)));
        store.RecordRunSummary(Run("c", "p1", T0.AddSeconds(3)));
        Require(store.ReadRunHistory(null, 10).Select(row => row.RunId).SequenceEqual(["c", "b"]),
            "the row cap keeps only the newest summaries");
        clock.UtcNow = T0.AddDays(2);
        store.RecordRunSummary(Run("d", "p1", clock.UtcNow));
        Require(store.ReadRunHistory(null, 10).Select(row => row.RunId).SequenceEqual(["d"]),
            "the age window expires summaries on the next append");
        clock.UtcNow = clock.UtcNow.AddDays(2);
        store.Prune();
        Require(store.ReadRunHistory(null, 10).Count == 0, "Prune also expires run summaries");
        Console.WriteLine("PASS run summary retention");
    }

    private static void RunHistoryPresenceGuard(string root)
    {
        var folder = Fresh(root, "run-history-presence");
        var clock = new TestClock { UtcNow = T0 };
        using (var store = SqliteStateStore.Open(folder, Fast, clock))
        {
            store.SavePair(Pair("p1"));
            store.RecordRunSummary(Run("keep", "p1", T0));
        }
        DropTable(DbPath(folder), "run_summary");
        using (var store = SqliteStateStore.Open(folder, Fast, clock))
        {
            Require(store.ReadRunHistory(null, 10).Count == 0 && store.Pair("p1") is not null,
                "a missing run_summary table is recreated on open and existing rows are preserved");
        }
        Console.WriteLine("PASS run summary presence guard");
    }

    private static void DropTable(string dbPath, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE " + table + ";";
        command.ExecuteNonQuery();
    }
}
