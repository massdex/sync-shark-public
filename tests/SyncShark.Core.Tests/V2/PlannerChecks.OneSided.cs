namespace Massdex.SyncShark.V2;

/// <summary>Planner checks for rule 32's per-folder first-sync choices on one-sided rows (first-sync-choices.md).</summary>
internal static partial class PlannerChecks
{
    private static void RunOneSidedChecks()
    {
        R32_OneSided_RemoveDeletesRecoveryFirst();
        R32_OneSided_LongestPrefixWinsIgnoringCase();
        R32_OneSided_SideScoped();
        R32_OneSided_NoChoiceUnchanged();
        R32_OneSided_PrefixBoundary();
        R32_OneSided_CopyBeatsDeletionEvidence();
        R32_OneSided_RemoveBeatsOlderEvidence();
        R32_OneSided_FolderCollapses();
        R32_OneSided_CopiedChildKeepsFolder();
        R32_OneSided_ExactNoChangeBeatsPrefix();
        R32_OneSided_CaseVariantNotRemoved();
        R32_OneSided_IgnoredForBaselineAndIncremental();
        R32_OneSided_KeptCaseVariantBlocksCollapse();
        R32_OneSided_IncompleteSideNotRemoved();
        R32_OneSided_IncompleteCopyNotDeleted();
        R32_OneSided_IncompleteSubtreeNotRemoved();
        R32_OneSided_UnicodePrefixIgnoresCase();
    }

    /// <summary>A first-sync row with no baseline whose sides are both covered by complete inventories.</summary>
    private static PlanRow CoveredRow(ItemState? a, ItemState? b, Evidence? ev = null) =>
        Row(null, a, b, ev ?? new Evidence(false, false, true, true));

    private static OneSidedChoice Remove(string prefix, Side side) => new(prefix, side, true);

    private static OneSidedChoice Copy(string prefix, Side side) => new(prefix, side, false);

    private static Plan DecideChoices(bool firstSync, OneSidedChoice[] choices, params PlanRow[] rows) =>
        Planner.Decide(PairId, Gen, rows, Opt(firstSync, oneSided: choices));

    private static void R32_OneSided_RemoveDeletesRecoveryFirst()
    {
        var item = F("a/x", "idA", "h1");
        var plan = DecideChoices(true, new[] { Remove("", Side.A) }, CoveredRow(item, null));
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "a/x", null, true, 32));
        Require(plan.Actions[0].ExpectedTarget == item, "R32 remove expects the current item");
        AssertEmptyNotices(plan);
    }

    private static void R32_OneSided_LongestPrefixWinsIgnoringCase()
    {
        var plan = DecideChoices(true, new[] { Remove("", Side.A), Copy("B/", Side.A) },
            CoveredRow(F("b/x", "id1", "h1"), null), CoveredRow(F("c/y", "id2", "h1"), null));
        AssertActions(plan,
            new Exp(ActionKind.Delete, Side.A, "c/y", null, true, 32),
            new Exp(ActionKind.CopyNew, Side.B, "b/x", null, false, 32, "b/x", NK(Side.A, "id1")));
        AssertEmptyNotices(plan);
    }

    private static void R32_OneSided_SideScoped()
    {
        var plan = DecideChoices(true, new[] { Remove("", Side.B) },
            CoveredRow(F("x", "idA", "h1"), null), CoveredRow(null, F("y", "idB", "h1")));
        AssertActions(plan,
            new Exp(ActionKind.CopyNew, Side.B, "x", null, false, 18, "x", NK(Side.A, "idA")),
            new Exp(ActionKind.Delete, Side.B, "y", null, true, 32));
        AssertEmptyNotices(plan);
    }

    private static void R32_OneSided_NoChoiceUnchanged()
    {
        var copied = CoveredRow(F("x", "idA", "h1"), null);
        var evidence = CoveredRow(F("y", "idA2", "h1"), null, Ev(B: true));
        foreach (var choices in new[] { Array.Empty<OneSidedChoice>(), new[] { Remove("q", Side.A) } })
        {
            var plan = DecideChoices(true, choices, copied, evidence);
            AssertActions(plan,
                new Exp(ActionKind.CopyNew, Side.B, "x", null, false, 18, "x", NK(Side.A, "idA")),
                new Exp(ActionKind.CopyNew, Side.B, "y", null, false, 19, "y", NK(Side.A, "idA2")));
            AssertNotices(plan, new NExp(19, NoticeKind.EditBeatsDelete, "y"));
        }
    }

    private static void R32_OneSided_PrefixBoundary()
    {
        var plan = DecideChoices(true, new[] { Remove("a/", Side.A) },
            CoveredRow(F("a/x", "id1", "h1"), null), CoveredRow(F("ab/y", "id2", "h1"), null));
        AssertActions(plan,
            new Exp(ActionKind.Delete, Side.A, "a/x", null, true, 32),
            new Exp(ActionKind.CopyNew, Side.B, "ab/y", null, false, 18, "ab/y", NK(Side.A, "id2")));
        AssertEmptyNotices(plan);
    }

    /// <summary>A later deletion on B would delete A's file (rule 19); an explicit copy keeps it (decision 52).</summary>
    private static void R32_OneSided_CopyBeatsDeletionEvidence()
    {
        var row = CoveredRow(F("x", "idA", "h1", mtime: T0), null,
            Ev(B: true, atB: T0 + TimeSpan.FromHours(1)) with { DeletedHashB = "h1" });
        var plan = DecideChoices(true, new[] { Copy("", Side.A) }, row);
        AssertActions(plan, new Exp(ActionKind.CopyNew, Side.B, "x", null, false, 32, "x", NK(Side.A, "idA")));
        AssertEmptyNotices(plan);
    }

    /// <summary>An older deletion on B would copy A's file (rule 19); an explicit remove deletes it (decision 52).</summary>
    private static void R32_OneSided_RemoveBeatsOlderEvidence()
    {
        var row = CoveredRow(F("x", "idA", "h1", mtime: T0 + TimeSpan.FromHours(1)), null, Ev(B: true, atB: T0));
        var plan = DecideChoices(true, new[] { Remove("", Side.A) }, row);
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "x", null, true, 32));
        AssertEmptyNotices(plan);
    }

    private static void R32_OneSided_FolderCollapses()
    {
        var plan = DecideChoices(true, new[] { Remove("", Side.A) },
            CoveredRow(D("d", "idD"), null), CoveredRow(D("d/e", "idE"), null),
            CoveredRow(F("d/x", "id1", "h1"), null), CoveredRow(F("d/e/y", "id2", "h1"), null));
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "d", null, true, 23));
        Require(plan.Actions[0].ExpectedTarget?.Kind == ItemKind.Folder, "R32 collapsed delete names the folder");
        AssertEmptyNotices(plan);
    }

    private static void R32_OneSided_CopiedChildKeepsFolder()
    {
        var plan = DecideChoices(true, new[] { Remove("", Side.A), Copy("d/k", Side.A) },
            CoveredRow(D("d", "idD"), null), CoveredRow(F("d/x", "id1", "h1"), null),
            CoveredRow(D("d/k", "idK"), null), CoveredRow(F("d/k/y", "id2", "h1"), null));
        AssertActions(plan,
            new Exp(ActionKind.Delete, Side.A, "d/x", null, true, 32),
            new Exp(ActionKind.MkDir, Side.B, "d", null, false, 24, "d"),
            new Exp(ActionKind.MkDir, Side.B, "d/k", null, false, 32, "d/k", NK(Side.A, "idK")),
            new Exp(ActionKind.CopyNew, Side.B, "d/k/y", null, false, 32, "d/k/y", NK(Side.A, "id2")));
        AssertNotices(plan, new NExp(24, NoticeKind.EditBeatsDelete, "d"));
    }

    private static void R32_OneSided_ExactNoChangeBeatsPrefix()
    {
        var winners = new Dictionary<string, Side?> { ["D/X"] = null, ["d/z"] = Side.A };
        var plan = Planner.Decide(PairId, Gen,
            new[] { CoveredRow(F("d/x", "id1", "h1"), null), CoveredRow(F("d/y", "id2", "h1"), null), CoveredRow(F("d/z", "id3", "h1"), null) },
            Opt(firstSync: true, winners: winners, oneSided: new[] { Remove("", Side.A) }));
        AssertActions(plan,
            new Exp(ActionKind.Delete, Side.A, "d/y", null, true, 32),
            new Exp(ActionKind.CopyNew, Side.B, "d/z", null, false, 32, "d/z", NK(Side.A, "id3")));
        AssertNotices(plan, new NExp(32, NoticeKind.AutoConflict, "d/z"));
    }

    /// <summary>B holds a case variant of A's one-sided path (Join pairs only one of A's two variants): never removed.</summary>
    private static void R32_OneSided_CaseVariantNotRemoved()
    {
        var plan = DecideChoices(true, new[] { Remove("", Side.A) },
            CoveredRow(F("x", "idA1", "h1"), null), CoveredRow(F("X", "idA2", "h1"), F("x", "idB", "h1")));
        Require(plan.Actions.All(a => a.Kind != ActionKind.Delete && a.Rule != 32),
            "R32 case variant: " + string.Join(", ", plan.Actions.Select(a => $"{a.Kind} {a.Target} '{a.Path}' rule={a.Rule}")));
    }

    private static void R32_OneSided_IgnoredForBaselineAndIncremental()
    {
        var incremental = DecideChoices(false, new[] { Remove("", Side.A) }, CoveredRow(F("x", "idA", "h1"), null));
        AssertActions(incremental, new Exp(ActionKind.CopyNew, Side.B, "x", null, false, 18, "x", NK(Side.A, "idA")));
        AssertEmptyNotices(incremental);

        var baseline = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("x", "idA", "h1"), null, Ev(B: true));
        var plan = DecideChoices(true, new[] { Copy("", Side.A) }, baseline);
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "x", null, false, 6, null, "k1"));
        AssertEmptyNotices(plan);
    }

    /// <summary>A kept (ignored) d/X beside a removed d/x: no Delete of d may cover it, so d stays (rule 24).</summary>
    private static void R32_OneSided_KeptCaseVariantBlocksCollapse()
    {
        var plan = DecideChoices(true, new[] { Remove("", Side.A) },
            CoveredRow(D("d", "idD"), null), CoveredRow(F("d/x", "id1", "h1"), null),
            CoveredRow(F("d/X", "id2", "h1", flags: ItemFlags.Ignored), null));
        AssertActions(plan,
            new Exp(ActionKind.Delete, Side.A, "d/x", null, true, 32),
            new Exp(ActionKind.MkDir, Side.B, "d", null, false, 24, "d"));
        AssertNotices(plan, new NExp(24, NoticeKind.EditBeatsDelete, "d"));
    }

    /// <summary>B's inventory is incomplete: A's one-sided file is not confirmed absent, so the row is deferred.</summary>
    private static void R32_OneSided_IncompleteSideNotRemoved()
    {
        var rows = Planner.Join(Array.Empty<Baseline>(), new[] { F("x", "idA", "h1") }, Array.Empty<ItemState>(), Set(), Set(),
            new Completeness(true, false, new HashSet<string>(), new HashSet<string>()), true);
        var plan = DecideChoices(true, new[] { Remove("", Side.A) }, rows.ToArray());
        AssertNoActions(plan);                                                    // neither removed nor copied
        AssertNotices(plan, new NExp(30, NoticeKind.Info, "x"));
    }

    /// <summary>Copy with B unconfirmed and a later B deletion: the choice still beats the evidence (no rule-19 Delete).</summary>
    private static void R32_OneSided_IncompleteCopyNotDeleted()
    {
        var evidence = new Evidence(false, true, false, false, null, T0 + TimeSpan.FromHours(1));
        var plan = DecideChoices(true, new[] { Copy("", Side.A) }, Row(null, F("x", "idA", "h1", mtime: T0), null, evidence));
        AssertNoActions(plan);
        AssertNotices(plan, new NExp(30, NoticeKind.Info, "x"));
    }

    /// <summary>B's inventory skipped subtree s: only the covered file is removed.</summary>
    private static void R32_OneSided_IncompleteSubtreeNotRemoved()
    {
        var rows = Planner.Join(Array.Empty<Baseline>(), new[] { F("s/y", "id1", "h1"), F("z", "id2", "h1") },
            Array.Empty<ItemState>(), Set(), Set(),
            new Completeness(true, true, new HashSet<string>(), new HashSet<string> { "s" }), true);
        var plan = DecideChoices(true, new[] { Remove("", Side.A) }, rows.ToArray());
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "z", null, true, 32));
        AssertNotices(plan, new NExp(30, NoticeKind.Info, "s/y"));
    }

    /// <summary>Prefix matching ignores case beyond ASCII ("Ä/" matches "ä/x").</summary>
    private static void R32_OneSided_UnicodePrefixIgnoresCase()
    {
        var plan = DecideChoices(true, new[] { Remove("Ä/", Side.A) },
            CoveredRow(F("ä/x", "id1", "h1"), null), CoveredRow(F("a/y", "id2", "h1"), null));
        AssertActions(plan,
            new Exp(ActionKind.Delete, Side.A, "ä/x", null, true, 32),
            new Exp(ActionKind.CopyNew, Side.B, "a/y", null, false, 18, "a/y", NK(Side.A, "id2")));
        AssertEmptyNotices(plan);
    }
}
