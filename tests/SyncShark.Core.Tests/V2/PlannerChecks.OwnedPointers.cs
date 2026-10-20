namespace Massdex.SyncShark.V2;

internal static partial class PlannerChecks
{
    private static void OwnedPointerChecks()
    {
        var local = F("folder/file.txt", "local", "hash", flags: ItemFlags.OwnedPointer);
        var remote = F(local.Path, "remote", "hash", versionId: "revision");
        var baseline = Base("pointer", local, remote);
        Plan PlanRows(params PlanRow[] rows) => Planner.Decide(PairId, Gen, rows, Opt());
        AssertNoActions(PlanRows(Row(baseline, local, remote)));
        AssertNoActions(PlanRows(Row(baseline, local with { Flags = ItemFlags.None }, remote)));
        var deleted = PlanRows(Row(baseline, local, null, Ev(B: true)));
        Require(deleted.Actions.Count == 1 && deleted.Actions[0] is
            { Kind: ActionKind.Delete, Target: Side.A, RecoveryFirst: false },
            "remote deletion removes the pointer without reading or archiving bytes");
        AssertNoActions(PlanRows(Row(baseline, local, null)));
        Require(PlanRows(Row(baseline, null, remote, Ev(A: true))).Actions.Single().Target == Side.B,
            "actual complete local disappearance still propagates, pointer presence is not absence");
        foreach (var localMove in new[] { false, true })
        {
            var moved = PlanRows(Row(baseline,
                localMove ? local with { Path = "other/renamed.txt" } : local,
                localMove ? remote : remote with { Path = "other/renamed.txt" }));
            Require(moved.Actions.Count == 1 && moved.Actions[0].Kind == ActionKind.Move &&
                moved.Actions[0].Target == (localMove ? Side.B : Side.A),
                "pointer rename/move propagates on either side without content copy");
        }
        foreach (var row in new[] {
            Row(null, local, null),
            Row(baseline, local with { Path = "elsewhere.txt" }, null, Ev(B: true)),
            Row(baseline, local with { Path = "left.txt" }, remote with { Path = "right.txt" }),
            Row(baseline, local, remote with { Hash = "edited", Mtime = T0.AddHours(1) }) })
        {
            var held = PlanRows(row, row);
            AssertNoActions(held);
            Require(held.Notices.Any(n => n.ReasonKey == "sync-retry") &&
                held.Notices.Select(n => (n.Path, n.ReasonKey)).Distinct().Count() == held.Notices.Count,
                "unsupported pointer content operation is pending with deduplicated actionable notices");
        }
        var needs = Planner.HashRequests([Row(baseline, local with { Hash = null, Size = 8 }, remote)]);
        Require(needs.PathsA.Count == 0, "even incomplete pointer metadata never requests a content read");
        var folderA = D("folder", "folder-a");
        var folderB = D("folder", "folder-b");
        var folderBase = Base("folder", folderA, folderB);
        var folderDelete = PlanRows(Row(folderBase, folderA, null, Ev(B: true)), Row(baseline, local, null, Ev(B: true)));
        Require(folderDelete.Actions.Any(a => a.Kind == ActionKind.Delete && a.Path == "folder"),
            "remote enclosing-folder deletion is not protected by its pointer child");
        var folderMove = PlanRows(Row(folderBase, folderA, folderB with { Path = "renamed" }),
            Row(baseline, local, remote with { Path = "renamed/file.txt" }));
        Require(folderMove.Actions.Any(a => a.Kind == ActionKind.Move && a.Path == "renamed"),
            "remote enclosing-folder move carries pointers");

        using var store = new InMemoryStateStore();
        var pair = new PairDef(PairId, "Pair A",
            new RootDef("pointer-a", SyncProviders.Local, null, "C:\\Fixture", "id:a"),
            new RootDef("pointer-b", SyncProviders.Dropbox, "account", "/Fixture", "id:b"), [], [], true);
        store.SavePair(pair);
        var gen = store.BeginGeneration(pair.A.RootId, "full");
        store.WriteSnapshot(new Snapshot(pair.A.RootId, Side.A, gen, "cursor", [folderA, local], []));
        store.CompleteGeneration(gen, "cursor", true);
        var exclusions = LinkExclusions.Of(store, pair, true);
        Require(!exclusions.Covers(local.Path) && !exclusions.Contains("folder") &&
            exclusions.NarrowAction(_ => true)("folder") && exclusions.Narrow(_ => true)(local.Path),
            "owned pointers and ancestors remain eligible for identity-based sync");
        Console.WriteLine("PASS owned pointers: structural sync, hydration neutrality, no source/hash reads, deduped pending notices");
    }
}
