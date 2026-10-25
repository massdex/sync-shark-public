namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    // Parent test runner may call this with its disposable test root.
    public static void RunFlaggedSummaryChecks(string root)
    {
        var dataRoot = Fresh(root, "flagged-summary");
        using var store = SqliteStateStore.Open(dataRoot, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        var refused = F("refused", "id:refused", "11") with { Flags = ItemFlags.ProviderNameRefused };
        var folder = F("folder", "id:folder", "22") with
            { Kind = ItemKind.Folder, Flags = ItemFlags.ProviderNameRefused | ItemFlags.Ignored };
        var ignored = F("ignored", "id:ignored", "33") with { Flags = ItemFlags.Ignored };
        var plain = F("plain", "id:plain", "44");
        var placeholder = F("online-only", "id:placeholder", "55") with { Flags = ItemFlags.Placeholder };
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "cursor", [refused, folder, ignored, plain, placeholder], []));
        var otherGen = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, otherGen, "cursor", [ignored], []));

        var summary = store.LiveFlaggedSummary("ra", ItemFlags.ProviderNameRefused, 1);
        Require(summary.Count == 2 && summary.Examples.Count == 1 &&
            (summary.Examples[0].Flags & ItemFlags.ProviderNameRefused) != 0,
            "summary counts every match but decodes only the example limit");
        var combined = store.LiveFlaggedSummary("ra", ItemFlags.ProviderNameRefused | ItemFlags.Ignored, 10);
        Require(combined.Count == 3 && combined.Examples.Count == 3 &&
            combined.Examples.Select(item => item.Path).Distinct().Count() == 3,
            "combined mask matches any bit and does not double-count multi-flag rows");
        foreach (var limit in new[] { 0, -1 })
        {
            var countOnly = store.LiveFlaggedSummary("ra", ItemFlags.ProviderNameRefused, limit);
            Require(countOnly.Count == 2 && countOnly.Examples.Count == 0, "nonpositive limit is count-only");
        }
        foreach (var rootId in new[] { "rb", "unknown" })
        {
            var empty = store.LiveFlaggedSummary(rootId, ItemFlags.ProviderNameRefused, 10);
            Require(empty.Count == 0 && empty.Examples.Count == 0, "summary isolates roots and accepts unknown roots");
        }
        Require(store.LiveFlaggedSummary("ra", ItemFlags.None, 10).Count == 0, "zero mask matches nothing");
        Require(store.LiveFlaggedFolders("ra", ItemFlags.ProviderNameRefused).Select(item => item.Path)
            .SequenceEqual([folder.Path]), "folder query excludes refused files in SQL");
        Require(store.LiveFlaggedFolders("rb", ItemFlags.ProviderNameRefused).Count == 0 &&
            store.LiveFlaggedFolders("unknown", ItemFlags.ProviderNameRefused).Count == 0,
            "folder query isolates roots");

        var plans = store.HotQueryPlans();
        foreach (var name in new[] { "LiveFlaggedCount", "LiveFlaggedExamples", "LiveFlaggedFolders" })
        {
            var query = plans.Where(line => line.StartsWith(name + ":")).ToArray();
            Require(query.Any(line => line.Contains("USING INDEX fs_refused (root_no=?")) &&
                !query.Any(line => line.Contains("SCAN f") || line.Contains("TEMP B-TREE")),
                name + " uses the partial index without a whole-root scan or sort");
        }
        store.Dispose();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + DbPath(dataRoot)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM file_state INDEXED BY fs_refused WHERE (flags & 64) != 0;";
            Require((long)command.ExecuteScalar()! == 2, "refusal index excludes online-only placeholders");
            command.CommandText = "DROP INDEX fs_refused;";
            command.ExecuteNonQuery();
        }
        using var upgraded = SqliteStateStore.Open(dataRoot, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        Require(upgraded.Live("ra").Count == 5 && upgraded.LiveFlaggedSummary("ra", ItemFlags.ProviderNameRefused, 20).Count == 2,
            "additive refusal index migration preserves all rows and flags");
        Console.WriteLine("PASS Storage_FlaggedSummary");
    }
    public static void RunRefusalNoticeChecks(string root)
    {
        WrittenNameRefusalChecks(root);
        using var store = SqliteStateStore.Open(Fresh(root, "refusal-notices"), Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        var decision = new Notice("p1", 5, NoticeKind.UserDecision, "conflict", "original explanation", "tie");
        store.AddNotices([decision]);
        store.AddNotices([decision with { Detail = "unrelated planner refresh" }]);
        Require(store.OpenNotices("p1").Single().Detail == decision.Detail,
            "unrelated open decision is not rewritten");
        var annotated = decision with { Detail = decision.Detail + " " + ProviderNameRefusedException.Detail };
        store.AddNotices([annotated]);
        store.AddNotices([decision]);
        Require(store.OpenNotices("p1").Single().Detail == annotated.Detail,
            "plain planner re-raise preserves refusal explanation");
    }

}
