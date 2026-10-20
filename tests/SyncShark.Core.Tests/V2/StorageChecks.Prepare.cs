using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private const string DropPrepareSchema =
        "DROP TABLE prepare_row; DROP TABLE prepare_cursor; DROP TABLE prepare_header;";

    private static void PrepareStorageChecks(string root)
    {
        PrepareV5Migration(root, false);
        PrepareV5Migration(root, true);
        PrepareAtomicReplacement(root);
        PrepareBatchConcurrency(root);
        PrepareFlushedFailureCleanup(root);
        PrepareLargeClear(root);
        PrepareRowViewOrdinalGaps(root);
        PrepareImportSqliteFailureExplained(root);
        PrepareMalformedLegacyExplained(root);
        PrepareRemovedPairExplained(root);
        PreparePublicationCounts(root);
        PrepareOpenSweepsAbandonedBatches(root);
        PrepareCursorAccessorLifecycle(root);
        PreparePairRemovalArchive(root);
        PrepareImportAndPlannerParity(root);
        PrepareDigestBytesUnchanged();
        PrepareNativeViewsParity(root);
        PrepareLegacyMissingMatches(root);
        PrepareReaderChunkBoundaries();
        PrepareStreamingRetainedMemory(root);
    }

    private static void PrepareV5Migration(string root, bool flagged)
    {
        var folder = Fresh(root, "prepare-v5-" + flagged);
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1"));
            var generation = store.BeginGeneration("ra", "full");
            store.CompleteGeneration(generation, "preserve-cursor", true);
            store.MarkFirstSyncDone("p1");
        }
        using (var connection = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = DropPrepareSchema + "DROP INDEX IF EXISTS fs_flagged;" +
                (flagged ? "CREATE INDEX fs_flagged ON file_state(root_no,flags) WHERE flags<>0;" : "") +
                "PRAGMA user_version=5;";
            command.ExecuteNonQuery();
        }
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            Require(store.FirstSyncDone("p1") && store.Cursor("ra") == "preserve-cursor",
                "v5 migration preserves pair and root state with optional flagged index=" + flagged);
            store.WritePrepare("p1", writer => writer.Commit("{}", "v6-digest"));
        }
        using (var connection = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False"))
        {
            connection.Open();
            Require((long)StoreSchema.Scalar(connection, null, "PRAGMA user_version;")! == 11,
                "migration stamps combined schema v11");
            Require((long)StoreSchema.Scalar(connection, null,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='fs_flagged';")! == 1,
                "combined heldperf/v6 upgrade preserves or creates its required flagged index");
        }
        using var reopened = SqliteStateStore.Open(folder, Fast, new TestClock());
        Require(reopened.ReadPrepare("p1")?.Digest == "v6-digest", "v6 prepare survives reopen");
    }

    private static void PrepareAtomicReplacement(string root)
    {
        var folder = Fresh(root, "prepare-atomic");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var previous = store.WritePrepare("p1", writer =>
        {
            Require(writer.AddUnique("matched", 0, "D/File", "{\"a\":1}"), "first key inserted");
            Require(!writer.AddUnique("matched", 1, "d/FILE", "{}"), "keys use ordinal ignore case");
            writer.Add("matched", 2, "D/Second", "{}");
            writer.Add("matched", 3, "ſ", "{}");
            writer.Add("matched", 4, "S", "{}");
            writer.SetCursor("ra", "pending-a");
            writer.SetCursor("rb", "pending-b");
            writer.Commit("{}", "old-digest");
        });
        Require(store.FindPrepareRow(previous.Generation, "matched", "d/file")?.Ordinal == 0 &&
            store.FindPrepareRow(previous.Generation, "matched", "ſ")?.Ordinal == 3 &&
            store.FindPrepareRow(previous.Generation, "matched", "s")?.Ordinal == 4,
            "indexed prepare lookups preserve PathKey's Unicode semantics");
        Require(store.ReadPrepareRows(previous.Generation, "matched", 0, 1).Single().Ordinal == 2,
            "keyset pages tolerate ordinal gaps");
        ExpectThrows<InvalidOperationException>(() => store.WritePrepare("p1", writer =>
        {
            writer.Add("matched", 0, "new", "{}");
            writer.SetCursor("ra", "wrong-cursor");
            writer.Commit("{}", "unpublished");
            throw new InvalidOperationException("Failure after sealing must still roll back.");
        }));
        ExpectThrows<JsonException>(() => store.WritePrepare("p1", writer =>
        {
            writer.Add("matched", 0, "valid", "{}");
            writer.Add("matched", 1, "bad", "{");
        }));
        Require(store.ReadPrepare("p1") == previous && store.CountPrepareRows(previous.Generation, "matched") == 4 &&
            store.PrepareCursor(previous.Generation, "ra") == "pending-a" &&
            store.PrepareCursor(previous.Generation, "rb") == "pending-b" && store.Cursor("ra") is null,
            "failed replacements preserve rows, header and each pending cursor without accepting them");
        var replacement = store.WritePrepare("p1", writer =>
        {
            writer.Add("matched", 0, "replacement", "{}");
            writer.Commit("{}", "replacement-digest");
        });
        Require(replacement.Generation != previous.Generation &&
            store.CountPrepareRows(previous.Generation, "matched") == 0 &&
            store.PrepareCursor(previous.Generation, "ra") is null,
            "replacement atomically retires the old generation and cursors");
        store.ClearPrepare("p1");
        Require(store.ReadPrepare("p1") is null && store.CountPrepareRows(replacement.Generation, "matched") == 0,
            "clear removes the header and payload rows");
        using var connection = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False");
        connection.Open();
        Require((long)StoreSchema.Scalar(connection, null, "SELECT COUNT(*) FROM prepare_row;")! == 0 &&
            (long)StoreSchema.Scalar(connection, null, "SELECT COUNT(*) FROM prepare_cursor;")! == 0,
            "rollback, replacement and clear leave no orphan rows or cursors");
    }

    private static void PreparePairRemovalArchive(string root)
    {
        var folder = Fresh(root, "prepare-pair-removal");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        foreach (var id in new[] { "p1", "p2", "p3" })
        {
            store.SavePair(Pair(id) with { Enabled = false });
            store.WritePrepare(id, writer =>
            {
                writer.Add("matched", 0, "path", "{}");
                writer.SetCursor("ra", "pending-" + id);
                writer.Commit("{}", "digest-" + id);
            });
        }
        var removed = store.ReadPrepare("p1")!;
        var enclosing = store.ReadPrepare("p2")!;
        ExpectThrows<IOException>(() => store.ArchiveAndRemovePair("p1", Path.Combine(folder, "failed-archive"), false,
            beforeRemoval: () => throw new IOException("legacy invalidation failed"), invalidatePrepares: ["p2"]));
        Require(store.Pair("p1") is not null && store.ReadPrepare("p1") == removed && store.ReadPrepare("p2") == enclosing,
            "failed removal keeps own and enclosing SQL prepares");
        var archive = store.ArchiveAndRemovePair("p1", Path.Combine(folder, "archive"), false,
            beforeRemoval: null, invalidatePrepares: ["p2"]);
        Require(store.Pair("p1") is null && store.ReadPrepare("p1") is null && store.ReadPrepare("p2") is null &&
            store.ReadPrepare("p3") is not null && store.CountPrepareRows(removed.Generation, "matched") == 0 &&
            store.CountPrepareRows(enclosing.Generation, "matched") == 0,
            "pair removal atomically clears own/enclosing prepares without clearing unrelated pairs");
        Require(archive.RowCounts["prepare_header"] == 1 && archive.RowCounts["prepare_row"] == 1 &&
            archive.RowCounts["prepare_cursor"] == 1, "archive includes reviewed prepare header, rows and root cursors");
        using (var connection = new SqliteConnection($"Data Source={archive.ArchivePath};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            Require((string?)StoreSchema.Scalar(connection, null,
                "SELECT digest FROM prepare_header WHERE pair_id='p1';") == "digest-p1" &&
                (string?)StoreSchema.Scalar(connection, null, "SELECT cursor FROM prepare_cursor;") == "pending-p1",
                "verified archive retains removed pair's prepare contents");
        }
        var direct = store.ReadPrepare("p3")!;
        store.ArchiveAndRemovePair("p3", Path.Combine(folder, "direct-archive"));
        Require(store.ReadPrepare("p3") is null && store.CountPrepareRows(direct.Generation, "matched") == 0,
            "direct public pair removal also clears prepare payloads");
    }

    private static void PrepareImportAndPlannerParity(string root)
    {
        var folder = Fresh(root, "prepare-planner-parity");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var local = new ItemState("d/x", ItemKind.File, "local:x", "v1", "aabbccdd", 12, T0, ItemFlags.None);
        var a = Content.SideStamp(local);
        var file = Path.Combine(folder, "legacy.json");
        WritePrepareFixture(file, 1, a, "remote:x|v:1");
        var prepared = PrepareDatabase.Import(store, "p1", file);
        var sql = prepared.Database!.MatchLookup;
        var memory = new Dictionary<string, PrepareMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["d/x"] = new(a, "remote:x|v:1")
        };
        Require(sql.Count == 1 && sql["D/X"] == memory["D/X"], "SQL match view equals the in-memory match");
        foreach (var changed in new[] { false, true })
        foreach (var remove in new[] { false, true })
        {
            var row = new PlanRow(null, changed ? local with { Hash = "different", VersionId = "v2" } : local,
                null, new Evidence(false, true, true, true));
            var options = new PlanOptions(TimeSpan.FromSeconds(2), true,
                new LabelRule("{name} [{side} version {time}]{ext}", "d MMM HH.mm", TimeSpan.Zero, "A", "B"), 20,
                OneSided: [new OneSidedChoice("d", Side.A, remove)], MatchedAtPrepare: memory);
            var expected = Planner.Decide("p1", 1, [row], options);
            var actual = Planner.Decide("p1", 1, [row], options with { MatchedAtPrepare = sql });
            Require(JsonSerializer.Serialize(actual) == JsonSerializer.Serialize(expected),
                "SQL/in-memory prepare matches produce identical planner outcomes changed=" + changed + " remove=" + remove);
            if (!changed)
                Require(actual.Actions.Single() is { Kind: ActionKind.Delete, Rule: 6, FirstSyncChoice: false },
                    "unchanged prepare match still propagates deletion before first-sync choices (decision 71)");
        }
        var old = store.ReadPrepare("p1");
        File.WriteAllText(file, "{\"matched\":[{\"p\":\"new\",\"a\":\"a\",\"b\":\"b\"}],\"deleteVictims\":[");
        ExpectThrows<JsonException>(() => PrepareDatabase.Import(store, "p1", file));
        Require(store.ReadPrepare("p1") == old && sql["D/X"] == memory["D/X"],
            "malformed legacy JSON rolls back imported rows without losing the previous reviewed prepare");
    }

    private static void PrepareDigestBytesUnchanged()
    {
        var choices = new FirstSyncChoices.Parsed(
            new Dictionary<string, Side?> { ["z/é\"<>&"] = Side.B, ["a"] = null },
            [new OneSidedChoice("d", Side.A, true)], [new RebuildableChoice("cache")]);
        RebuildableFolder[] folders = [new("cache", "d/cache", 0, 0)];
        // Cross the serializer's normal buffer boundary, with escaping/non-ASCII in the canonical bytes.
        var held = Enumerable.Range(0, 600).Reverse()
            .Select(i => new HeldDeletion("d/é\"<>&/" + i.ToString("D4"), Side.B, "rule")).ToArray();
        var oldTuples = new List<string[]>();
        foreach (var item in held.Reverse()) oldTuples.Add(["heldDeletion", "B", item.Path, "rule"]);
        oldTuples.Add(["oneSided", "A", "d", FirstSyncChoices.ActionText(true)]);
        oldTuples.Add(["path", "a", "none"]);
        oldTuples.Add(["path", "z/é\"<>&", "B"]);
        oldTuples.Add(["rebuildable", "cache"]);
        oldTuples.Add(["rebuildablePath", "cache", "d/cache"]);
        var oldDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(oldTuples))).ToLowerInvariant();
        Require(FirstSyncChoices.Digest(choices, folders, held) == oldDigest,
            "streamed digest is byte-identical to the previous buffered canonical JSON digest");
    }

    private static void PrepareStreamingRetainedMemory(string root)
    {
        const int count = 200_000;
        var folder = Fresh(root, "prepare-streaming-memory");
        using var store = SqliteStateStore.Open(folder, Fast with { StoreCacheMb = 1 }, new TestClock());
        store.SavePair(Pair("p1"));
        var file = Path.Combine(folder, "legacy-large.json");
        // Warm serializer/parser/type caches outside the retained-memory sample.
        WritePrepareFixture(file, 1, "a-stamp", "b-stamp");
        _ = PrepareDatabase.Import(store, "p1", file).Database!.MatchLookup["d/x"];
        WritePrepareFixture(file, count, "a-stamp", "b-stamp");
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var prepared = PrepareDatabase.Import(store, "p1", file);
        var lookup = prepared.Database!.MatchLookup;
        Require(lookup.Count == count && lookup["D/199999"].A == "a-stamp" && !lookup.ContainsKey("missing"),
            "200k streamed matches support indexed membership without materialization");
        // Exhaust the view so accidental enumeration caching is also measured; no ToArray/ToDictionary here.
        var enumerated = 0;
        foreach (var entry in lookup) enumerated++;
        Require(enumerated == count, "bounded pages enumerate every match");
        var retained = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - before);
        GC.KeepAlive(prepared);
        GC.KeepAlive(lookup);
        Require(retained < count * 32L,
            $"200k prepare retains <32 managed bytes/item; retained={retained}, bytes/item={(double)retained / count:F2}. " +
            "This GC measurement excludes SQLite native memory (configured 1 MiB cache), not peak allocations.");
        Console.WriteLine($"PASS prepare streaming retained managed memory: {retained} bytes for {count} matches");
    }

    private static void WritePrepareFixture(string file, int count, string a, string b)
    {
        using var stream = File.Create(file);
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteString("digest", "fixture-digest");
        json.WriteStartObject("paths"); json.WriteEndObject();
        json.WriteStartArray("oneSided"); json.WriteEndArray();
        json.WriteStartArray("deleteVictims"); json.WriteEndArray();
        json.WriteNumber("deleteVictimCount", 0);
        json.WriteStartArray("rebuildables"); json.WriteEndArray();
        json.WriteStartArray("heldDeletions"); json.WriteEndArray();
        json.WriteStartArray("matched");
        for (var i = 0; i < count; i++)
        {
            json.WriteStartObject();
            json.WriteString("p", count == 1 ? "d/x" : "d/" + i);
            json.WriteString("a", a);
            json.WriteString("b", b);
            json.WriteEndObject();
            if ((i & 255) == 255) json.Flush();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
