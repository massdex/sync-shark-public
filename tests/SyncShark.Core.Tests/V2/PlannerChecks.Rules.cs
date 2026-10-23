namespace Massdex.SyncShark.V2;

/// <summary>Planner checks for rules 26-32, Join, hash requests and keep-both labels.</summary>
internal static partial class PlannerChecks
{
    private static void Rule26_FileReplacedByFolder()
    {
        var row = Row(Base("k1", F("x", "idAf", "h1"), F("x", "idB", "h1")), D("x", "idAf"), F("x", "idB", "h1"));
        var plan = Decide(row);
        AssertActions(plan,
            new Exp(ActionKind.Delete, Side.B, "x", null, true, 26, null, "k1"),
            new Exp(ActionKind.MkDir, Side.B, "x", null, false, 26, "x", "k1"));
        AssertEmptyNotices(plan);
    }

    private static void Rule27_EditedBothDifferent()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("x", "idA", "h2"), F("x", "idB", "h3"));
        AssertKeepBothSplit(Decide(row));
    }

    /// <summary>Both edited (by version id), no hashes: hashes requested; at Decide no effect, Info notice only.</summary>
    private static void Rule30d_UnconfirmedEditsSkipped()
    {
        var basis = Base("k1", F("x", "idA", "h1", versionId: "v1"), F("x", "idB", "h1", versionId: "v1"));
        var row = Row(basis, F("x", "idA", null, versionId: "v2"), F("x", "idB", null, versionId: "v3"));
        var request = Planner.HashRequests(new[] { row });
        Require(request.PathsA.SequenceEqual(new[] { "x" }) && request.PathsB.SequenceEqual(new[] { "x" }), "Rule30d request");
        foreach (var firstSync in new[] { false, true })
        {
            var plan = Decide(firstSync, row);
            AssertNoActions(plan);
            AssertNotices(plan, new NExp(30, NoticeKind.Info, "x"));
        }
    }

    private static void AssertKeepBothSplit(Plan plan)
    {
        AssertActions(plan,
            new Exp(ActionKind.KeepBoth, Side.A, KeepBothLabel.For("x", "Dropbox", T0, Label, 1), null, false, 27, "x", SK("k1", Side.B)),
            new Exp(ActionKind.Forget, Side.B, "x", null, false, 27, null, "k1"),
            new Exp(ActionKind.KeepBoth, Side.B, KeepBothLabel.For("x", "Local", T0, Label, 1), null, false, 27, "x", SK("k1", Side.A)));
        AssertNotices(plan, new NExp(27, NoticeKind.KeepBoth, "x"));
    }

    private static void Rule28_MovedAndEditedOneSide()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("y", "idA", "h2"), F("x", "idB", "h1"));
        var plan = Decide(row);
        AssertActions(plan,
            new Exp(ActionKind.Update, Side.B, "y", null, false, 28, "y", "k1"),
            new Exp(ActionKind.Move, Side.B, "y", "x", false, 28, "y", "k1"));
        AssertEmptyNotices(plan);
    }

    private static void Rule29_KindClashBothSides()
    {
        var clash = Row(Base("k1", F("x", "idAf", "h1"), F("x", "idB", "h1")), D("x", "idAf"), F("x", "idB", "h2"));
        var child = Row(Base("k2", F("x/c", "idAc", "h1"), F("x/c", "idBc", "h1")), F("x/c", "idAc", "h2"), F("x/c", "idBc", "h1"));
        var plan = Planner.Decide(PairId, Gen, new[] { clash, child }, Opt());
        AssertActions(plan, new Exp(ActionKind.Hold, Side.B, "x", null, false, 29));
        AssertNotices(plan, new NExp(29, NoticeKind.UserDecision, "x"));

        var movedClash = Row(Base("m1", D("original", "folderA"), D("original", "folderB")),
            F("first", "replacement", "new"), D("second", "folderB"));
        var movedChild = Row(Base("m2", F("second/child", "childA", "h1"), F("second/child", "childB", "h1")),
            null, F("second/child", "childB", "h1"));
        var movedPlan = Planner.Decide(PairId, Gen, [movedClash, movedChild], Opt());
        Require(movedPlan.Actions.Any(a => a.Kind == ActionKind.Hold && a.Rule == 29) &&
            !movedPlan.Actions.Any(a => a.Kind == ActionKind.Delete),
            "kind clash protects the moved second folder footprint, not only the primary path");
        var mappedClash = movedClash with { B = D("second:folder", "folderB") with
            { IdentityJson = DropboxNameMapping.Fact("second:folder") } };
        var mappedChild = Row(Base("m3", F("second_folder/child", "mappedA", "h1"), F("second_folder/child", "mappedB", "h1")),
            null, F("second_folder/child", "mappedB", "h1"));
        var mappedPlan = Planner.Decide(PairId, Gen, [mappedClash, mappedChild], Opt());
        Require(!mappedPlan.Actions.Any(a => a.Kind == ActionKind.Delete), "kind clash also holds mapped folder spelling");
        var unbaselinedPlan = Planner.Decide(PairId, Gen, [mappedClash with { Base = null }, mappedChild], Opt());
        Require(unbaselinedPlan.Actions.Any(a => a.Kind == ActionKind.Hold && a.Rule == 14) &&
            !unbaselinedPlan.Actions.Any(a => a.Kind == ActionKind.Delete),
            "no-baseline kind clash reuses all-footprint protection for rule 14");
    }

    private static void Rule30_Unreadable()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("x", "idA", "h2"),
            F("x", "idB", "h1", flags: ItemFlags.Locked));
        var plan = Decide(row);
        AssertNoActions(plan);
        AssertNotices(plan, new NExp(30, NoticeKind.Info, "x"));
    }

    private static void Rule30b_SameSizeNoHash()
    {
        var basis = Base("k1", F("x", "idA", null, mtime: T0 + TimeSpan.FromSeconds(1)),
            F("x", "idB", null, mtime: T0 + TimeSpan.FromSeconds(1)));
        var row = Row(basis, F("x", "idA", null, mtime: T0), F("x", "idB", null, mtime: T0));
        var plan = Decide(row);
        AssertNoActions(plan);
        AssertNotices(plan, new NExp(30, NoticeKind.Info, "x"));
    }

    private static void Rule31_Ignored()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("x", "idA", "h2", flags: ItemFlags.Ignored),
            F("x", "idB", "h1"));
        var plan = Decide(row);
        AssertNoActions(plan);
        AssertEmptyNotices(plan);
    }

    private static void Rule32_UserChoice()
    {
        var row = Row(null, F("x", "idA", "h2", mtime: T0 + TimeSpan.FromSeconds(1)), F("x", "idB", "h3", mtime: T0));
        var winners = new Dictionary<string, Side?> { ["x"] = Side.B };
        var plan = Planner.Decide(PairId, Gen, new[] { row }, Opt(firstSync: true, winners: winners));
        AssertActions(plan, new Exp(ActionKind.Update, Side.A, "x", null, true, 32, "x", NK(Side.B, "idB")));
        AssertNotices(plan, new NExp(32, NoticeKind.AutoConflict, "x"));

        var none = Planner.Decide(PairId, Gen, new[] { row },
            Opt(firstSync: true, winners: new Dictionary<string, Side?> { ["x"] = null }));
        AssertEmpty(none);
    }

    private static void Unhandled_Throws()
    {
        var row = Row(null, null, null);
        var plan = Planner.Decide(PairId, Gen, new[] { row }, Opt());
        Require(plan.Actions.Count == 0 && plan.Notices.Count == 1 && plan.Notices[0].ReasonKey == "sync-retry",
            "unknown planner row is held with one notice rather than aborting the run");
    }

    // ---- Join ----------------------------------------------------------------

    private static void Join_ById()
    {
        var b = new[] { Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")) };
        var rows = Planner.Join(b, new[] { F("x", "idA", "h1") }, new[] { F("x", "idB", "h1") },
            Set(), Set(), Complete(), false);
        Require(rows.Count == 1 && rows[0].Base?.ObjectKey == "k1" && rows[0].A?.ObjectId == "idA", "Join_ById");
    }

    private static void Join_PathFallbackAtomicSave()
    {
        var b = new[] { Base("k1", F("x", "idA1", "h1"), F("x", "idB1", "h1")) };
        var rows = Planner.Join(b, new[] { F("x", "idA2", "h2") }, new[] { F("x", "idB1", "h1") },
            Set(), Set(), Complete(), false);
        Require(rows.Count == 1 && rows[0].A?.ObjectId == "idA2", "Join_PathFallbackAtomicSave");
    }

    private static void Join_FirstSyncCaseInsensitivePath()
    {
        var rows = Planner.Join(Array.Empty<Baseline>(), new[] { F("X.txt", "idA", "h1") }, new[] { F("x.txt", "idB", "h1") },
            Set(), Set(), Complete(), true);
        Require(rows.Count == 1 && rows[0].Base is null && rows[0].A?.Path == "X.txt" && rows[0].B?.Path == "x.txt",
            "Join_FirstSyncCaseInsensitivePath");
    }

    private static void Join_AncestorDeleteEvidence()
    {
        var b = new[] { Base("k1", F("f/c", "idA", "h1"), F("f/c", "idB", "h1")) };
        var rows = Planner.Join(b, Array.Empty<ItemState>(), new[] { F("f/c", "idB", "h1") },
            Set("f"), Set(), Complete(), false);
        Require(rows.Count == 1 && rows[0].Ev.DeletedA, "Join_AncestorDeleteEvidence");
    }

    private static void Join_IncompleteSubtreeNotKnown()
    {
        var b = new[] { Base("k1", F("f/c", "idA", "h1"), F("f/c", "idB", "h1")) };
        var c = new Completeness(true, true, new HashSet<string> { "f" }, new HashSet<string>());
        var rows = Planner.Join(b, Array.Empty<ItemState>(), new[] { F("f/c", "idB", "h1") }, Set(), Set(), c, false);
        Require(rows.Count == 1 && !rows[0].Ev.DeletedA && !rows[0].Ev.KnownA, "Join_IncompleteSubtreeNotKnown");
        var incomplete = Enumerable.Range(0, 20000).Select(i => $"blocked/{i}").ToHashSet();
        incomplete.Add("DEEP/HELD");
        var items = Enumerable.Range(0, 40000).Select(i => F($"clear/{i}", $"id-{i}", "h1"))
            .Append(F("deep/held/child", "held", "h1")).ToArray();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var many = Planner.Join([], items, [], Set(), Set(), new(true, true, incomplete, new HashSet<string>()), false);
        Require(many.Count == items.Length && many.Count(r => !r.Ev.KnownA) == 1,
            "coverage handles thousands of subtrees with case-insensitive ancestor semantics");
        Require(timer.Elapsed < TimeSpan.FromSeconds(10), "coverage scales with path depth, not rows times incomplete subtrees");
    }

    private static void Join_DuplicateCiPathsOneSide()
    {
        var rows = Planner.Join(Array.Empty<Baseline>(),
            new[] { F("x", "idA1", "h1"), F("X", "idA2", "h1") }, new[] { F("x", "idB", "h1") },
            Set(), Set(), Complete(), false);
        var paired = rows.Single(r => r.Base is null && r.A is not null && r.B is not null);
        Require(rows.Count == 2 && paired.A!.Path == "X", "Join_DuplicateCiPathsOneSide");
    }

    // ---- HashRequests --------------------------------------------------------

    private static void HashRequests_SameSizeNoHash_Requested()
    {
        var row = Row(null, F("x", "idA", null), F("x", "idB", null));
        var request = Planner.HashRequests(new[] { row });
        Require(request.PathsA.Count == 1 && request.PathsA[0] == "x" &&
            request.PathsB.Count == 1 && request.PathsB[0] == "x", "HashRequests_SameSizeNoHash_Requested");
    }

    private static void HashRequests_DifferentSize_NotRequested()
    {
        var row = Row(null, F("x", "idA", null, size: 1), F("x", "idB", null, size: 2));
        var request = Planner.HashRequests(new[] { row });
        Require(request.PathsA.Count == 0 && request.PathsB.Count == 0, "HashRequests_DifferentSize_NotRequested");
    }

    // ---- label ---------------------------------------------------------------

    private static void Label_Format()
    {
        var rule = new LabelRule("{name} [{side} version {time}]{ext}", "d MMM HH.mm", TimeSpan.Zero, "Local", "Dropbox");
        var label = KeepBothLabel.For("report.docx", "Dropbox", new DateTimeOffset(2026, 10, 14, 18, 25, 0, TimeSpan.Zero), rule, 1);
        Require(label == "report [Dropbox version 14 Oct 18.25].docx", "Label_Format: " + label);
    }

    private static void Label_DotFile()
    {
        var label = KeepBothLabel.For(".gitignore", "Local", T0, Label, 1);
        Require(label == ".gitignore [Local version 14 Oct 12.00]", "Label_DotFile: " + label);
    }

    private static void Label_AttemptSuffix()
    {
        var label = KeepBothLabel.For("report.docx", "Local", T0, Label, 2);
        Require(label == "report [Local version 14 Oct 12.00] (2).docx", "Label_AttemptSuffix: " + label);
    }

    // A format without {ext} still gives each attempt a new name: the suffix goes at the end.
    private static void Label_AttemptSuffix_NoExtToken()
    {
        var rule = new LabelRule("{name} [{side}]", "d MMM HH.mm", TimeSpan.Zero, "Local", "Dropbox");
        var first = KeepBothLabel.For("report.docx", "Local", T0, rule, 1);
        var second = KeepBothLabel.For("report.docx", "Local", T0, rule, 2);
        Require(first == "report [Local]" && second == "report [Local] (2)",
            "Label_AttemptSuffix_NoExtToken: " + first + " / " + second);
    }
}
