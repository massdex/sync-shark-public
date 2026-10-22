namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void NameMappingDeltaScope(string root)
    {
        using var store = OpenSqlite(root, "mapping-scope", new TestClock());
        var pair = Pair("p1");
        store.SavePair(pair);
        var cloud = DropboxItems.ToItem(new PilotRemoteEntry("a:b", PilotEntryKind.File, "id:colon", "v", 1, "11", T0));
        var folder = DropboxItems.ToItem(new PilotRemoteEntry("folder:", PilotEntryKind.Directory, "id:folder", null, 0, null, T0));
        var child = DropboxItems.ToItem(new PilotRemoteEntry("folder:/child", PilotEntryKind.File, "id:child", "v", 1, "11", T0));
        var rows = new[] { cloud, folder, child, F("unrelated/deep/data", "unrelated", "11") };
        var gen = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new("rb", Side.B, gen, "c", rows, []));
        store.CompleteGeneration(gen, "c", true);
        Require(store.LiveChildren("rb", "").Count == 2, "root sibling query never loads descendant rows");
        var loader = new RunScope(store, _ => true, _ => true);
        var scope = loader.LoadTouched(pair, ["a_b"], [], []);
        Require(scope.B.Count == 1 && scope.B[0].Path == "a:b", "root underscore delta loads only its mapped sibling, not unrelated subtree");
        scope = loader.LoadTouched(pair, ["folder_/child"], [], []);
        Require(scope.B.Any(i => i.Path == "folder:/child") && scope.B.All(i => i.Path != "unrelated/deep/data"),
            "mapped-parent delta resolves ancestor spelling without subtree widening");
        Require(scope.B.All(i => i.Path != "folder:") && scope.ContextB.Single(i => i.Path == "folder:").IdentityJson is not null,
            "ancestor mapping facts are outside planner rows and cannot plan an unrelated folder effect");
    }

    public static void NameMappingRoundTrip(string root)
    {
        NameMappingDeltaScope(root);
        var local = F("a_b.txt", "local", "11");
        var cloud = DropboxItems.ToItem(new PilotRemoteEntry("a:b.txt", PilotEntryKind.File, "id:cloud", "rev", 1, "11", T0));
        using (var store = OpenSqlite(root, "mapped", new TestClock()))
        {
            store.SavePair(Pair("p1"));
            SeedBaseline(store, "p1", "mapping", local, cloud);
            var gen = store.BeginGeneration("rb", "snapshot");
            store.WriteSnapshot(new Snapshot("rb", Side.B, gen, "cursor", [cloud], []));
            store.CompleteGeneration(gen, "cursor", true);
        }
        using (var store = SqliteStateStore.Open(Path.Combine(root, "mapped"), Fast, new TestClock()))
        {
            var basis = store.Baselines("p1");
            Require(cloud.IdentityJson is not null && basis.Single().B.IdentityJson is not null &&
                store.Live("rb").Single().IdentityJson is not null &&
                basis.Single().B.IdentityJson == cloud.IdentityJson && store.Live("rb").Single().IdentityJson == cloud.IdentityJson,
                "mapping fact survives real SQLite reopen on entity and link spellings");
            var edited = local with { Hash = "22", Mtime = T0.AddMinutes(1), VersionId = "v2" };
            var rows = Planner.Join(basis, [edited], [cloud], new Dictionary<string, DateTimeOffset?>(),
                new Dictionary<string, DateTimeOffset?>(), new(true, true, new HashSet<string>(), new HashSet<string>()), false);
            var label = new LabelRule("{name} [{side}]{ext}", "s", TimeSpan.Zero, "Local", "Dropbox");
            var plan = Planner.Decide("p1", 2, rows, new(TimeSpan.Zero, false, label, 10));
            Require(plan.Actions.Count == 1 && plan.Actions[0] is { Kind: ActionKind.Update, Path: "a:b.txt" },
                "persisted mapping authorizes later changed content without false move or collision");
        }
    }
}
