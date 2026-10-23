using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void PrepareBatchConcurrency(string root)
    {
        var folder = Fresh(root, "prepare-batch-concurrency");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        store.SavePair(Pair("p2"));
        var old = store.WritePrepare("p1", w =>
        {
            w.Add("matched", 0, "old", "{}");
            w.Commit("{}", "old");
        });
        Task? otherPair = null;
        try
        {
            var replacement = store.WritePrepare("p1", writer =>
            {
                otherPair = Task.Run(() => store.MarkFirstSyncDone("p2"));
                Require(otherPair.Wait(TimeSpan.FromSeconds(15)),
                    "another pair progresses before the first prepare batch boundary");
                for (var i = 0; i < 6001; i++) writer.Add("matched", i, "path/" + i, "{}");
                writer.SetCursor("ra", "unpublished");
                Require(!writer.AddUnique("matched", 6001, "PATH/0", "{}"),
                    "AddUnique rejects an ordinal-ignore-case duplicate from an earlier flushed batch");
                // The callback is suspended here. This is a deadlock watchdog, not a throughput benchmark:
                // neither the store gate nor a SQLite writer transaction may span the callback.
                otherPair = Task.Run(() =>
                {
                    Require(store.ReadPrepare("p1") == old, "unpublished batch leaves the old header visible");
                    Require(store.ReadPrepareRows(old.Generation, "matched").Single().Ordinal == 0 &&
                        store.FindPrepareRow(old.Generation, "matched", "old") is not null &&
                        store.ReadPrepareRows(writer.Generation, "matched").Count == 0 &&
                        store.FindPrepareRow(writer.Generation, "matched", "path/0") is null &&
                        store.CountPrepareRows(writer.Generation, "matched") == 0 &&
                        store.PrepareCursor(writer.Generation, "ra") is null,
                        "public row lookups expose only the published generation, not flushed in-flight rows");
                    store.MarkFirstSyncDone("p2");
                    using var secondStore = SqliteStateStore.Open(folder, Fast, new TestClock());
                    Require(secondStore.ReadPrepare("p1") == old,
                        "a second Open preserves the old published header during an active write");
                    using var connection = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False;Default Timeout=2");
                    connection.Open();
                    Require((long)StoreSchema.Scalar(connection, null,
                        "SELECT COUNT(*) FROM prepare_row;")! >= 5000,
                        "a completed prepare batch is committed before the callback finishes");
                    using var transaction = connection.BeginTransaction();
                    transaction.Commit();
                });
                Require(otherPair.Wait(TimeSpan.FromSeconds(15)),
                    "another pair's store call and independent SQLite writer complete while prepare callback waits");
                writer.Commit("{}", "new");
            });
            Require(store.FirstSyncDone("p2") && store.ReadPrepare("p1") == replacement &&
                store.CountPrepareRows(replacement.Generation, "matched") == 6001,
                "batch publication retains every buffered row and independent pair progress");
        }
        finally
        {
            // On the old implementation the watchdog fails and unwinds WritePrepare, releasing its gate.
            // Drain the probe before disposing the store; never leave a background user of a disposed fixture.
            if (otherPair is not null) otherPair.GetAwaiter().GetResult();
        }
    }

    private static void PrepareFlushedFailureCleanup(string root)
    {
        var folder = Fresh(root, "prepare-flushed-failure");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var old = store.WritePrepare("p1", writer =>
        {
            writer.Add("matched", 0, "old", "{}");
            writer.SetCursor("ra", "old-cursor");
            writer.Commit("{\"old\":true}", "old-digest");
        });
        foreach (var sealedFailure in new[] { false, true })
        {
            ExpectThrows<JsonException>(() => store.WritePrepare("p1", writer =>
            {
                for (var i = 0; i < 10001; i++) writer.Add("matched", i, "new/" + i, "{}");
                writer.SetCursor("ra", "unpublished-cursor");
                if (sealedFailure) writer.Commit("{}", "unpublished-digest");
                throw new JsonException("Failure after multiple flushed batches, sealed=" + sealedFailure);
            }));
            Require(store.ReadPrepare("p1") == old &&
                store.FindPrepareRow(old.Generation, "matched", "old") is not null &&
                store.PrepareCursor(old.Generation, "ra") == "old-cursor",
                "failed batched replacement preserves the exact previous header, row and cursor");
            using var connection = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False");
            connection.Open();
            Require((long)StoreSchema.Scalar(connection, null, "SELECT COUNT(*) FROM prepare_row;")! == 1 &&
                (long)StoreSchema.Scalar(connection, null, "SELECT COUNT(*) FROM prepare_cursor;")! == 1,
                "failed batched replacement cleans flushed unpublished rows and cursors immediately");
        }
    }

    private static void PrepareLargeClear(string root)
    {
        var folder = Fresh(root, "prepare-large-clear");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        store.SavePair(Pair("p2"));
        var other = store.WritePrepare("p2", w =>
        {
            w.Add("matched", 0, "keep", "{}");
            w.Commit("{}", "keep");
        });
        var large = store.WritePrepare("p1", w =>
        {
            for (var i = 0; i < 15001; i++) w.Add("matched", i, "path/" + i, "{}");
            w.SetCursor("ra", "pending");
            w.Commit("{}", "large");
        });
        store.ClearPrepare("p1");
        Require(store.ReadPrepare("p1") is null && store.CountPrepareRows(large.Generation, "matched") == 0 &&
            store.PrepareCursor(large.Generation, "ra") is null && store.ReadPrepare("p2") == other &&
            store.CountPrepareRows(other.Generation, "matched") == 1,
            "clear drains multiple payload batches without touching another pair's review");
    }

    private static void PrepareCursorAccessorLifecycle(string root)
    {
        var folder = Fresh(root, "prepare-cursor-accessors");
        PrepareHeader previous;
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1"));
            var generation = store.BeginGeneration("ra", "full");
            store.CompleteGeneration(generation, "accepted-a", true);
            store.SetPrepareCursor("p1", "ra", "no-header");
            Require(store.ReadPrepareCursor("p1", "ra") is null,
                "setting a prepare cursor without a published header creates no visible slot");
            previous = store.WritePrepare("p1", writer =>
            {
                writer.SetCursor("ra", "initial-a");
                writer.SetCursor("rb", "initial-b");
                writer.Commit("{}", "cursor-review");
            });
            store.SetPrepareCursor("p1", "ra", "updated-a");
            Require(store.PrepareCursorForPair("p1", "ra") == "updated-a" &&
                store.ReadPrepareCursor("p1", "rb") == "initial-b" &&
                store.PrepareCursorForPair("missing-pair", "ra") is null &&
                store.ReadPrepareCursor("p1", "missing-root") is null &&
                store.Cursor("ra") == "accepted-a" && store.Cursor("rb") is null,
                "prepare cursor accessors are pair/root-specific and never change accepted scan cursors");
            store.SetPrepareCursor("p1", "rb", null);
            Require(store.ReadPrepareCursor("p1", "rb") is null &&
                store.ReadPrepareCursor("p1", "ra") == "updated-a",
                "a null prepare cursor clears only the selected root slot");
        }
        using var reopened = SqliteStateStore.Open(folder, Fast, new TestClock());
        Require(reopened.ReadPrepareCursor("p1", "ra") == "updated-a" &&
            reopened.ReadPrepareCursor("p1", "rb") is null && reopened.Cursor("ra") == "accepted-a",
            "published prepare cursor updates survive reopen independently of accepted cursors");
        var replacement = reopened.WritePrepare("p1", writer =>
        {
            writer.SetCursor("rb", "replacement-b");
            writer.Commit("{}", "replacement");
        });
        Require(reopened.PrepareCursor(previous.Generation, "ra") is null &&
            reopened.ReadPrepareCursor("p1", "ra") is null &&
            reopened.PrepareCursorForPair("p1", "rb") == "replacement-b",
            "cursor accessors follow only the newly published generation after replacement");
        reopened.ClearPrepare("p1");
        reopened.SetPrepareCursor("p1", "ra", "must-not-resurrect");
        Require(reopened.PrepareCursorForPair("p1", "ra") is null &&
            reopened.ReadPrepareCursor("p1", "rb") is null &&
            reopened.PrepareCursor(replacement.Generation, "rb") is null &&
            reopened.Cursor("ra") == "accepted-a",
            "clear removes prepare cursor slots and later cursor updates cannot resurrect the review");
    }

    private static void PrepareOpenSweepsAbandonedBatches(string root)
    {
        var folder = Fresh(root, "prepare-open-sweep");
        PrepareHeader old;
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1"));
            old = store.WritePrepare("p1", w =>
            {
                w.Add("matched", 0, "keep", "{}");
                w.SetCursor("ra", "keep-cursor");
                w.Commit("{}", "keep-digest");
            });
        }
        // Simulate committed staging batches left by process death, with no active in-process writer.
        using (var connection = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False"))
        {
            connection.Open();
            StoreSchema.Execute(connection, null, """
                WITH RECURSIVE rows(n) AS (SELECT 0 UNION ALL SELECT n+1 FROM rows WHERE n<6000)
                INSERT INTO prepare_row(generation,kind,ordinal,lookup_key,json)
                SELECT 'abandoned','matched',n,CAST(n AS TEXT),'{}' FROM rows;
                INSERT INTO prepare_cursor(generation,root_id,cursor) VALUES('abandoned','ra','wrong');
                """);
        }
        using var reopened = SqliteStateStore.Open(folder, Fast, new TestClock());
        Require(reopened.ReadPrepare("p1") == old &&
            reopened.FindPrepareRow(old.Generation, "matched", "keep") is not null &&
            reopened.PrepareCursor(old.Generation, "ra") == "keep-cursor",
            "Open sweep preserves the published review and its root cursor");
        using var inspect = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False");
        inspect.Open();
        Require((long)StoreSchema.Scalar(inspect, null, "SELECT COUNT(*) FROM prepare_row;")! == 1 &&
            (long)StoreSchema.Scalar(inspect, null, "SELECT COUNT(*) FROM prepare_cursor;")! == 1,
            "Open physically removes abandoned batches and their unpublished cursors");
    }

    private static void PrepareImportSqliteFailureExplained(string root)
    {
        var folder = Fresh(root, "prepare-import-sqlite-error");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        Directory.CreateDirectory(Path.Combine(folder, "v2"));
        var file = Path.Combine(folder, "v2", "first-sync-p1.choices.json");
        WritePrepareFixture(file, 1, "a", "b");
        using (var connection = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False"))
        {
            connection.Open();
            StoreSchema.Execute(connection, null,
                "CREATE TRIGGER fail_prepare BEFORE INSERT ON prepare_row BEGIN SELECT RAISE(ABORT, 'fixture storage failure'); END;");
        }
        Exception? failure = null;
        try { _ = FirstSyncChoicesState.Load(folder, "p1", store); }
        catch (Exception error) { failure = error; }
        Require(failure is SyncCliReviewException &&
            failure.Message.Contains("run prepare again", StringComparison.Ordinal) &&
            failure.Message.Contains("fixture storage failure", StringComparison.Ordinal),
            "SQLite import failures explain the storage reason and offer prepare again instead of leaking SqliteException");
        Require(store.ReadPrepare("p1") is null && File.Exists(file),
            "failed SQL import publishes no header and retains the legacy review file");
    }

    private static void PrepareMalformedLegacyExplained(string root)
    {
        var folder = Fresh(root, "prepare-malformed-legacy");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        Directory.CreateDirectory(Path.Combine(folder, "v2"));
        var file = Path.Combine(folder, "v2", "first-sync-p1.choices.json");
        foreach (var malformed in new[] { "{\"matched\":42}", "{\"matched\":[" })
        {
            File.WriteAllText(file, malformed);
            JsonException? parseFailure = null;
            try { _ = PrepareDatabase.Import(store, "p1", file); }
            catch (JsonException error) { parseFailure = error; }
            Exception? failure = null;
            try { _ = FirstSyncChoicesState.Load(folder, "p1", store); }
            catch (Exception error) { failure = error; }
            Require(parseFailure is not null && failure is SyncCliReviewException &&
                failure.Message.Contains("run prepare again", StringComparison.Ordinal) &&
                failure.Message.Contains(parseFailure.Message, StringComparison.Ordinal),
                "malformed legacy JSON explains the exact parse reason and offers prepare again");
            Require(store.ReadPrepare("p1") is null && File.ReadAllText(file) == malformed,
                "malformed import publishes nothing and preserves the legacy file");
        }
    }

    private static void PrepareRemovedPairExplained(string root)
    {
        var folder = Fresh(root, "prepare-removed-pair-error");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1") with { Enabled = false });
        IEnumerable<string> RemoveDuringWrite()
        {
            store.ArchiveAndRemovePair("p1", Path.Combine(folder, "archive"));
            yield return "A:gone";
        }
        var small = new FirstSyncChoicesState.Prepared("digest", new Dictionary<string, string>(),
            [], [], 0, [], [], []);
        Exception? failure = null;
        try { PrepareDatabase.Save(store, "p1", small, RemoveDuringWrite(), [], [], []); }
        catch (Exception error) { failure = error; }
        Require(failure is SyncCliReviewException &&
            failure.Message.Contains("run prepare again", StringComparison.Ordinal) &&
            failure.Message.Contains("Pair p1 was removed during prepare.", StringComparison.Ordinal),
            "pair removal during prepare explains the reason through the adapter");
        Require(store.Pair("p1") is null && store.ReadPrepare("p1") is null,
            "failed publication never resurrects a removed pair or review");
    }

    private static void PreparePublicationCounts(string root)
    {
        var folder = Fresh(root, "prepare-publication-counts");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var old = store.WritePrepare("p1", writer =>
        {
            writer.Add("matched", 0, "old", "{}");
            writer.SetCursor("ra", "old-cursor");
            writer.Commit("{}", "old-digest");
        });
        foreach (var mutation in new[]
        {
            "DELETE FROM prepare_row WHERE generation=$g AND kind='matched' AND ordinal=0;",
            "DELETE FROM prepare_row WHERE generation=$g AND kind='heldRules';",
            "INSERT INTO prepare_row(generation,kind,ordinal,lookup_key,json) VALUES($g,'matched',9000,'extra','{}');",
            "INSERT INTO prepare_row(generation,kind,ordinal,lookup_key,json) VALUES($g,'unexpected',0,'extra','{}');"
        })
        {
            Exception? failure = null;
            try
            {
                store.WritePrepare("p1", writer =>
                {
                    for (var i = 0; i < 5001; i++) writer.Add("matched", i, "new/" + i, "{}");
                    writer.Add("heldRules", 0, "rule", "{}");
                    writer.Flush();
                    using var connection = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False");
                    connection.Open();
                    StoreSchema.Execute(connection, null, mutation, ("$g", writer.Generation));
                    writer.Commit("{}", "wrong-count");
                });
            }
            catch (Exception error) { failure = error; }
            Require(failure is PreparePublicationException &&
                failure.Message.Contains("count mismatch", StringComparison.Ordinal),
                "publication rejects missing, extra and unexpected-kind durable rows");
            Require(store.ReadPrepare("p1") == old &&
                store.CountPrepareRows(old.Generation, "matched") == 1 &&
                store.FindPrepareRow(old.Generation, "matched", "old") is not null &&
                store.PrepareCursor(old.Generation, "ra") == "old-cursor",
                "count mismatch preserves the old header, rows and cursor");
        }
    }

    private static void PrepareRowViewOrdinalGaps(string root)
    {
        var folder = Fresh(root, "prepare-row-view-gaps");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var choices = new FirstSyncChoices.Parsed(new Dictionary<string, Side?>(), [], []);
        var expected = Enumerable.Range(0, 300).Select(i => "A:path/" + i).ToArray();
        FirstSyncChoicesState.Save(folder, "p1", choices, [], expected, [], store: store);
        var original = store.ReadPrepare("p1")!;
        var header = store.WritePrepare("p1", writer =>
        {
            // Nonzero first ordinal and gaps straddling the 256-row page boundary.
            for (var i = 0; i < expected.Length; i++)
                writer.Add(PrepareDatabase.Victims, 7L + i * 3L, expected[i], JsonSerializer.Serialize(expected[i]));
            writer.Commit(original.MetadataJson, original.Digest);
        });
        var rows = new PrepareDatabase(store, header).Read().DeleteVictims;
        Require(rows.Count == expected.Length && rows.SequenceEqual(expected),
            "row view enumerates sparse ordinals across page boundaries");
        foreach (var index in new[] { 0, 1, 255, 256, 299 })
            Require(rows[index] == expected[index], "row view index is a position, not an ordinal: " + index);
        ExpectThrows<ArgumentOutOfRangeException>(() => { _ = rows[-1]; });
        ExpectThrows<ArgumentOutOfRangeException>(() => { _ = rows[rows.Count]; });
    }
}
