using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void PrepareNativeViewsParity(string root)
    {
        var folder = Fresh(root, "prepare-native-views");
        var legacyFolder = Fresh(root, "prepare-native-views-legacy");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var choices = new FirstSyncChoices.Parsed(new Dictionary<string, Side?>(), [], []);
        HeldDeletion[] held = [new("HeldFolder", Side.A, "Rule"), new("elsewhere", Side.B, "Rule")];
        string[] victims = ["A:gone", "a:GONE", "B:other"];
        SyncCliCarriedIgnoredData[] budgets = [new("heldfolder", "A", 2, 1, 33)];
        var local = F("match", "loc:match", "aabbccdd");
        MatchedPair[] matches = [new("match", Content.SideStamp(local), Content.SideStamp(local with { ObjectId = "dbx:match" }))];
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
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var file = Path.Combine(folder, "old.json");
        foreach (var suffix in new[] { "", ",\"matched\":null" })
        {
            File.WriteAllText(file, "{\"digest\":\"old\",\"paths\":{},\"oneSided\":[],\"deleteVictims\":[]," +
                "\"deleteVictimCount\":0,\"rebuildables\":[],\"heldDeletions\":[]" + suffix + "}");
            var prepared = PrepareDatabase.Import(store, "p1", file);
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

    private sealed class PrepareChunkStream(byte[] bytes, int chunk) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);
    }
}
