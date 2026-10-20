namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    // Bounded offline entrypoint. Caller supplies a disposable test root, never the live store.
    public static void RunHeldPerformanceChecks(string root)
    {
        HotLookups_UseIndexes(root);
        var folder = Fresh(root, "flagged-index-existing-v6");
        var link = F("Docs/link", "id:link", "11") with { Flags = ItemFlags.Link };
        var stub = F("Docs/stub", "id:stub", "22") with { Flags = ItemFlags.Placeholder };
        var both = F("other/both", "id:both", "33") with { Flags = ItemFlags.Link | ItemFlags.Placeholder };
        var ignored = F("ignored", "id:ignored", "44") with { Flags = ItemFlags.Ignored };
        var plain = F("plain", "id:plain", "55");
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1"));
            var gen = store.BeginGeneration("ra", "snapshot");
            store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "cursor", [link, stub, both, ignored, plain], []));
            store.CompleteGeneration(gen, "cursor", true);
            var other = store.BeginGeneration("rb", "snapshot");
            store.WriteSnapshot(new Snapshot("rb", Side.B, other, "other-cursor", [link], []));
            store.CompleteGeneration(other, "other-cursor", true);
            SeedBaseline(store, "p1", "plain", plain, plain);
        }
        var initial = StorageDamage.FlaggedIndexState(DbPath(folder));
        Require(initial.Version == StoreSchema.Version, "fresh combined store includes current schema item retry state");
        StorageDamage.RemoveFlaggedIndex(DbPath(folder));
        for (var reopen = 0; reopen < 2; reopen++)
        {
            using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
            Require(store.Live("ra").Count == 5 && store.Baselines("p1").Count == 1 && store.Cursor("ra") == "cursor",
                "index addition and repeated opens preserve live rows, baselines and cursors");
            Require(store.LiveFlagged("ra", ItemFlags.Link).Select(item => item.Path)
                .SequenceEqual([link.Path, both.Path]), "flagged query selects matching bits in path order");
            Require(store.LiveFlagged("ra", ItemFlags.Link | ItemFlags.Placeholder).Select(item => item.Path)
                .SequenceEqual([link.Path, stub.Path, both.Path]), "combined mask returns each matching item once");
            Require(store.LiveFlagged("ra", ItemFlags.None).Count == 0 &&
                store.LiveFlagged("rb", ItemFlags.Placeholder).Count == 0 &&
                store.LiveFlagged("rb", ItemFlags.Link).Count == 1,
                "zero masks, nonmatching flags and other roots do not leak rows");
            var plans = store.HotQueryPlans().Where(line => line.StartsWith("LiveFlagged:")).ToArray();
            Require(plans.Any(line => line.Contains("USING INDEX fs_flagged (root_no=?)")) &&
                !plans.Any(line => line.Contains("SCAN f") || line.Contains("USING PRIMARY KEY (root_no=?)")),
                "real LiveFlagged SQL uses only the partial flagged-row index");
        }
        var repaired = StorageDamage.FlaggedIndexState(DbPath(folder));
        Require(repaired.Version == initial.Version && repaired.Sql.Contains("WHERE flags != 0"),
            "existing v6 regains the partial nonzero-flags index without a version bump");
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock())) { }
        Require(StorageDamage.FlaggedIndexState(DbPath(folder)) == repaired,
            "index initialization is idempotent across repeated opens");
        Console.WriteLine("PASS HeldPerformance_FlaggedIndexExistingV6");
    }
}
