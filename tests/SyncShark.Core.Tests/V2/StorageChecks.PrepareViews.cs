using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void PrepareJsonClosureChecks(string root)
    {
        var folder = Fresh(root, "prepare-json-closure");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        var pair = Pair("p1");
        store.SavePair(pair);
        const string oldJson = """
            {"digest":"old-review","paths":{"file":"A"},"oneSided":[],"deleteVictims":[],
             "deleteVictimCount":0,"rebuildables":[],"heldDeletions":[],
             "matched":[{"p":"file","a":"local-stamp","b":"remote-stamp"}],
             "carriedIgnoredDeletes":[{"path":"folder","side":"A","files":1,"folders":2,"bytes":3,"reviewedSignature":"unchanged-signature"}]}
            """;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(oldJson));
        var prepared = PrepareDatabase.Import(store, "p1", input);
        Require(input.CanRead && prepared.Database is IPrepareDatabase && prepared.Matched.Single() ==
            new StoredMatch("file", "local-stamp", "remote-stamp"),
            "old JSON imports compact match fields through the lower contract without owning the stream");
        Require(prepared.RebuildableChoices is null && prepared.Warnings is null &&
            prepared.InventoryCursors.Count == 0 && prepared.InventoryScope is null &&
            prepared.NarrowedChoicesDigest is null && prepared.NarrowedIgnoreRules.Count == 0 && !prepared.NeedsReprepare,
            "omitted fields retain their null defaults and initialized lists");
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(prepared, options));
        var payload = serialized.RootElement;
        Require(payload.GetProperty("digest").GetString() == "old-review" &&
            !payload.TryGetProperty("database", out _) && !payload.TryGetProperty("Database", out _) &&
            payload.GetProperty("matched")[0].EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "p", "a", "b" }) &&
            payload.GetProperty("carriedIgnoredDeletes")[0].GetProperty("reviewedSignature").GetString() == "unchanged-signature",
            "camelCase payload, p/a/b fields, ignored Database and ReviewedSignature stay exact");
        Require(prepared.Database!.Budget("A:folder")?.ReviewedSignature == "unchanged-signature",
            "lazy budget lookup retains the reviewed signature");
        var previous = store.ReadPrepare("p1");
        foreach (var bad in new[]
        {
            oldJson.Replace("\"digest\":\"old-review\"", "\"digest\":\"old-review\",\"digest\":\"duplicate\""),
            oldJson.Replace("\"p\":\"file\"", "\"p\":null"),
            oldJson.Replace("{\"p\":\"file\",\"a\":\"local-stamp\",\"b\":\"remote-stamp\"}",
                "{\"p\":\"file\",\"a\":\"a\",\"b\":\"b\"},{\"p\":\"FILE\",\"a\":\"a\",\"b\":\"b\"}")
        })
        {
            using var failedInput = new MemoryStream(Encoding.UTF8.GetBytes(bad));
            ExpectThrows<JsonException>(() => PrepareDatabase.Import(store, "p1", failedInput));
            Require(failedInput.CanRead && store.ReadPrepare("p1") == previous && prepared.Matched.Count == 1,
                "malformed and duplicate imports preserve published authority and leave caller streams open");
        }
        var calls = 0;
        var invalid = store.WritePrepare("p1", writer => writer.Commit("{}", "invalid"));
        ExpectThrows<JsonException>(() => new PrepareDatabase(store, invalid).Narrow(pair, pair, "old", "new", small =>
        {
            calls++;
            throw new InvalidOperationException("Invalid header must not reach Edge.");
        }));
        Require(calls == 0, "Storage validates its own metadata before invoking the Edge callback");
        ExpectThrows<PreparePublicationException>(() => prepared.Database.Narrow(pair, pair, "old", "new", small =>
        {
            calls++;
            throw new InvalidOperationException("Stale header must not reach Edge.");
        }));
        Require(calls == 0 && !prepared.Database.MatchLookup.ContainsKey("file") && !prepared.Matched.Any(),
            "stale narrowing is refused and old lazy views cannot read a replacement generation");
        foreach (var suffix in new[] { "", ",\"matched\":null,\"carriedIgnoredDeletes\":null,\"warnings\":null,\"rebuildableChoices\":null,\"inventoryCursors\":null" })
        {
            using var defaultsInput = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"digest\":\"defaults\",\"paths\":{},\"oneSided\":[],\"deleteVictims\":[],\"rebuildables\":[],\"heldDeletions\":[]" + suffix + "}"));
            var defaults = PrepareDatabase.Import(store, "p1", defaultsInput);
            Require(defaults.Matched.Count == 0 && defaults.CarriedIgnoredDeletes.Count == 0 &&
                defaults.InventoryCursors.Count == 0 && defaults.Warnings is null && defaults.RebuildableChoices is null,
                "old JSON omitted and explicit-null optional collections retain their original import defaults");
        }
        var file = Path.Combine(folder, "page.json");
        WritePrepareFixture(file, 257, "a", "b");
        var paged = ImportPrepareFile(store, "p1", file);
        var header = store.ReadPrepare("p1")!;
        Require(store.ReadPrepareRows(header.Generation, "matched", digest: header.Digest).Count == 256 &&
            paged.Matched[255].Path == "d/255" && paged.Matched[256].Path == "d/256" && paged.Matched.Count() == 257,
            "lazy views cross the unchanged 256-row boundary without dropping or duplicating rows");
        var current = store.ReadPrepare("p1")!;
        Prepared? observed = null;
        ExpectThrows<InvalidOperationException>(() => new PrepareDatabase(store, current).Narrow(pair, pair, "old", "new", small =>
        {
            observed = small;
            throw new InvalidOperationException("Callback probe.");
        }));
        Require(observed is not null && observed.Digest == current.Digest && observed.Matched.Count == 0 && store.ReadPrepare("p1") == current,
            "Edge receives validated Storage metadata, not caller materialized or stale metadata");
    }

    private static void PrepareEdgeStreamOwnershipChecks(string root)
    {
        var folder = Fresh(root, "prepare-edge-stream");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        // pairs remove requires a disabled pair (CLI-CAPABILITIES); reach the publication race, not that guard.
        store.SavePair(Pair("p1") with { Enabled = false });
        Directory.CreateDirectory(Path.Combine(folder, "v2"));
        var file = Path.Combine(folder, "v2", "first-sync-p1.choices.json");
        File.WriteAllText(file, "{}");
        var priorOpen = FirstSyncChoicesState.OpenReadForTest;
        try
        {
            foreach (var publicationFailure in new[] { false, true })
            {
                MemoryStream? opened = null;
                FirstSyncChoicesState.OpenReadForTest = _ =>
                {
                    opened = new PrepareReadCallbackStream(Encoding.UTF8.GetBytes(publicationFailure
                        ? "{\"digest\":\"old\",\"paths\":{},\"oneSided\":[],\"deleteVictims\":[],\"rebuildables\":[],\"heldDeletions\":[]}"
                        : "{\"matched\":42}"), () =>
                        {
                            if (publicationFailure)
                                store.ArchiveAndRemovePair("p1", Path.Combine(folder, "archive"));
                        });
                    return opened;
                };
                Exception? failure = null;
                try { _ = FirstSyncChoicesState.Load(folder, "p1", store); }
                catch (Exception error) { failure = error; }
                var expected = publicationFailure
                    ? "The first-sync prepare could not be published: Pair p1 was removed during prepare. Review the pair and run prepare again."
                    : "The retained first-sync prepare could not be parsed: Expected prepare array. \u2014 run prepare again.";
                Require(failure is SyncCliReviewException && failure.Message == expected && opened is { CanRead: false },
                    "Edge retains exact error wording and disposes the stream on parse/publication failure" +
                    $"; publicationFailure={publicationFailure}; actual={failure?.GetType().Name}: {failure?.Message}; streamCanRead={opened?.CanRead}");
            }
        }
        finally { FirstSyncChoicesState.OpenReadForTest = priorOpen; }
    }

    private static void PrepareNativeViewsParity(string root)
    {
        var folder = Fresh(root, "prepare-native-views");
        var legacyFolder = Fresh(root, "prepare-native-views-legacy");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        var choices = new FirstSyncChoices.Parsed(new Dictionary<string, Side?>(), [], []);
        HeldDeletion[] held = [new("HeldFolder", Side.A, "Rule"), new("elsewhere", Side.B, "Rule")];
        string[] victims = ["A:gone", "a:GONE", "B:other"];
        SyncCliCarriedIgnoredData[] budgets = [new("heldfolder", "A", 2, 1, 33)];
        var local = F("match", "loc:match", "aabbccdd");
        MatchedPair[] matches = [new("match", local, local with { ObjectId = "dbx:match" })];
        FirstSyncChoicesState.Save(folder, "p1", choices, [], victims, [], held, matches,
            carriedIgnoredDeletes: budgets, store: store);
        FirstSyncChoicesState.Save(legacyFolder, "p1", choices, [], victims, [], held, matches,
            carriedIgnoredDeletes: budgets);
        var sql = FirstSyncChoicesState.Load(folder, "p1", store)!;
        var memory = FirstSyncChoicesState.Load(legacyFolder, "p1")!;
        Require(sql.Database is not null && sql.Digest == memory.Digest && sql.Matched.Count == 1 &&
            sql.DeleteVictimCount == memory.DeleteVictimCount && sql.DeleteVictimCount == 2,
            "native SQL Save/Load preserves digest, matched stamps and deduplicated victim counts");
        var sqlKeys = FirstSyncChoicesState.ReviewedDeleteKeys(sql);
        var memoryKeys = FirstSyncChoicesState.ReviewedDeleteKeys(memory);
        foreach (var key in new[] { "a:GONE", "b:OTHER", "a:HELDFOLDER", "B:elsewhere", "A:HeldFolder/child", "B:HeldFolder" })
            Require(sqlKeys.Contains(key) == memoryKeys.Contains(key), "SQL reviewed set matches in-memory membership for " + key);
        Require(sqlKeys.Count == memoryKeys.Count && !sqlKeys.Contains("A:HeldFolder/child"),
            "held folder review never authorizes a descendant");

        var plan = new Plan("p1", 1, [], [])
        {
            HeldDeletions = [new("HELDFOLDER", Side.A, "Rule"), new("HeldFolder", Side.A, "rule"),
                new("HeldFolder/child", Side.A, "Rule"), new("HeldFolder", Side.B, "Rule")]
        };
        var actual = FirstSyncChoicesState.PartitionHeldDeletions(sql, plan);
        var expected = FirstSyncChoicesState.PartitionHeldDeletions(memory, plan);
        Require(actual.HeldDeletions.SequenceEqual(expected.HeldDeletions) &&
            actual.UnreviewedHeldDeletions.SequenceEqual(expected.UnreviewedHeldDeletions) &&
            actual.HeldDeletions.Count == 1 && actual.UnreviewedHeldDeletions.Count == 3,
            "held rule is case-sensitive while its path is case-insensitive and side/descendants remain distinct");

        var reviewedDelete = new SyncAction("delete", 32, ActionKind.Delete, Side.A, "HELDFOLDER", null, null,
            D("HELDFOLDER", "folder:1"), true, "folder-key")
        { FirstSyncChoice = true, CarriedIgnoredBudget = new CarriedIgnoredContent(99, 99, 999) };
        var absentBudget = reviewedDelete with { ActionId = "missing", Path = "unreviewed" };
        var propagation = reviewedDelete with { ActionId = "ordinary", FirstSyncChoice = false };
        plan = plan with { Actions = [reviewedDelete, absentBudget, propagation] };
        var actualBudget = FirstSyncChoicesState.BindCarriedIgnoredBudgets(sql, plan);
        var expectedBudget = FirstSyncChoicesState.BindCarriedIgnoredBudgets(memory, plan);
        Require(actualBudget.Actions.SequenceEqual(expectedBudget.Actions) &&
            actualBudget.Actions[0].CarriedIgnoredBudget == new CarriedIgnoredContent(2, 1, 33) &&
            actualBudget.Actions[1].CarriedIgnoredBudget == new CarriedIgnoredContent(0, 0, 0) &&
            actualBudget.Actions[2].CarriedIgnoredBudget == propagation.CarriedIgnoredBudget,
            "SQL carried budgets bind reviewed ceilings, default missing to zero, and leave ordinary propagation alone");
        FirstSyncChoicesState.Clear(folder, "p1", store);
        Require(FirstSyncChoicesState.Load(folder, "p1", store) is null, "native Clear removes retained prepare");
    }

    private static void PrepareLegacyMissingMatches(string root)
    {
        var folder = Fresh(root, "prepare-legacy-missing");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        var file = Path.Combine(folder, "old.json");
        foreach (var suffix in new[] { "", ",\"matched\":null" })
        {
            File.WriteAllText(file, "{\"digest\":\"old\",\"paths\":{},\"oneSided\":[],\"deleteVictims\":[]," +
                "\"deleteVictimCount\":0,\"rebuildables\":[],\"heldDeletions\":[]" + suffix + "}");
            var prepared = ImportPrepareFile(store, "p1", file);
            Require(prepared.Matched.Count == 0 && prepared.Database!.MatchLookup.Count == 0,
                "legacy missing/explicit-null matched array imports as an empty view");
        }
    }

    private static void PrepareReaderChunkBoundaries()
    {
        foreach (var chunk in new[] { 1, 7, 65535 })
        foreach (var bom in new[] { false, true })
        {
            // Tiny reads exercise split UTF-8/escapes; the large read exercises buffer growth without quadratic test work.
            var value = new string('x', chunk > 100 ? 70_000 : 25) + "é😀\"\\\n";
            var bytes = Encoding.UTF8.GetBytes("{\"value\":" + JsonSerializer.Serialize(value,
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "}");
            var input = bom ? Encoding.UTF8.GetPreamble().Concat(bytes).ToArray() : bytes;
            using var stream = new PrepareChunkStream(input, chunk);
            var reader = new PrepareJsonReader(stream);
            Require(reader.Token().Token == JsonTokenType.StartObject && reader.Token().Text == "value",
                "incremental reader accepts object header, chunk=" + chunk + " BOM=" + bom);
            using var document = reader.ValueOrArrayEnd();
            Require(document?.RootElement.GetString() == value &&
                reader.Token().Token == JsonTokenType.EndObject && reader.Token().Token == JsonTokenType.None,
                "incremental reader preserves >64KiB escaped Unicode value across chunk boundaries");
        }
    }

    private sealed class PrepareReadCallbackStream(byte[] bytes, Action onRead) : MemoryStream(bytes, writable: false)
    {
        private bool _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_read)
            {
                _read = true;
                onRead();
            }
            return base.Read(buffer, offset, count);
        }
    }

    private sealed class PrepareChunkStream(byte[] bytes, int chunk) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);
    }
}
