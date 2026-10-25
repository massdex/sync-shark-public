namespace Massdex.SyncShark.V2;

/// <summary>Table-driven contract checks for the pure Planner (planner.md section 8); keys are checked exactly.</summary>
internal static partial class PlannerChecks
{
    private const string PairId = "p1";
    private const long Gen = 5;

    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly LabelRule Label =
        new("{name} [{side} version {time}]{ext}", "d MMM HH.mm", TimeSpan.Zero, "Local", "Dropbox");

    /// <summary>Runs every Planner check; the dispatch flag is --v2-planner-checks.</summary>
    public static Task RunAsync()
    {
        PathRuleChecks.Run();
        FailedMappedRowIsAtomic();
        ContentEvidence_OneOwner();
        Rule01_Unchanged();
        Rule02_EditedOneSide();
        Rule03_SameChangeBothSides();
        Rule04_FirstSyncNewerWins();
        Rule05_FirstSyncTie();
        Rule05b_FirstSyncBoundary();
        Rule06_DeletedOneSide();
        Rule06b_DeletedByKnownAbsence();
        Rule06c_LocalTargetWithDropboxOppositeSkipsRecovery();
        EqualRevision_ProviderMatrix();
        Rule07_DeletedVsEdited();
        Rule08_DeletedVsMoved();
        Rule08b_DeletedVsMovedFolder();
        Rule09_MovedOneSide();
        Rule10_EditAndMoveDifferentPaths();
        Rule11_MovedToTwoPlaces();
        Rule12_MoveOntoIdenticalOccupant();
        Rule12b_IdenticalOccupantCompletesTheMove();
        Rule12c_IdenticalOccupantOnSideA();
        Rule12d_IdenticalOccupantRetiresBaselineWithAdopt();
        Rule13_MoveOntoChangedOccupant();
        Rule14_DestinationOccupied();
        Rule14b_FirstSyncOccupiedIncomingNewer();
        Rule14c_RedirectMovesDependentUpdate();
        Rule14d_FirstSyncOccupiedOccupantNewer();
        Rule14e_FirstSyncOccupiedTieHoldsDependants();
        Rule14f_FirstSyncOccupantMirrorIsOwned();
        Rule14g_RedirectLeavesUnrelatedActions();
        Rule14h_UnconfirmedCollisionSkipped();
        Rule13b_UnconfirmedVacatedDestinationSkipped();
        Rule14i_UnconfirmedDuplicateCreatesAllSkipped();
        Rule14j_IncomingWinsRetiresBaselineWithUpdate();
        Rule14k_OccupantWinsRetiresBaselineWithUpdate();
        Rule15_DeletedBoth();
        Rule16_CreatedBothSame();
        Rule17_CreatedBothDifferent();
        Rule18_OneSideOnly();
        Rule19_OneSideOnlyOtherDeleted();
        Rule19b_LaterDeletionWins();
        CreationObservationNeverDeletes();
        Rule20_CaseOnlyRename();
        CaseEquivalentMoves_AdoptOrUpdate();
        CaseEquivalentChildren_OnlyTopmostFolderNotices();
        Rule23_FolderDeletedUnchanged();
        Rule23b_NestedFolderDeleteCollapses();
        Rule24_FolderDeletedContentsChanged();
        Rule24b_UnknownDescendantKeepsFolder();
        Rule24c_UnhashedDescendantKeepsFolder();
        Rule25_FolderMoved();
        Rule25b_FolderMoveRebasesSource();
        Rule25b2_RestoreOntoOccupiedChildKeepsBoth();
        Rule25c_FolderMoveIsPerSide();
        Rule25d_RedirectedFolderMoveTakesItsChildren();
        Rule25e_RebasedTwoPlaceCopyKeepsBoth();
        Rule25f_RebasedTwoPlaceCopyFirstSyncNewerWins();
        Rule25g_RebasedTwoPlaceCopyFirstSyncTieHolds();
        Rule26_FileReplacedByFolder();
        Rule27_EditedBothDifferent();
        Rule28_MovedAndEditedOneSide();
        Rule29_KindClashBothSides();
        Rule30_Unreadable();
        Rule30b_SameSizeNoHash();
        Rule30c_UnconfirmedNewContentSkipped();
        Rule30d_UnconfirmedEditsSkipped();
        Rule31_Ignored();
        Rule32_UserChoice();
        RunOneSidedChecks();
        RunRebuildableChecks();
        RunStubChecks();
        Unhandled_Throws();

        Join_ById();
        Join_PathFallbackAtomicSave();
        Join_FirstSyncCaseInsensitivePath();
        Join_AncestorDeleteEvidence();
        Join_IncompleteSubtreeNotKnown();
        Join_DuplicateCiPathsOneSide();

        HashRequests_SameSizeNoHash_Requested();
        HashRequests_DifferentSize_NotRequested();

        Label_Format();
        Label_DotFile();
        Label_AttemptSuffix();
        Label_AttemptSuffix_NoExtToken();

        Property_NoDeleteWithoutEvidence(false);
        Property_NoDeleteWithoutEvidence(true);
        Property_HoldOnlyForDecisionRules();
        Property_Determinism();

        PostPrepare_Checks();

        Console.WriteLine("v2 planner checks passed.");
        return Task.CompletedTask;
    }

    // ---- decision-table rules (each asserts its notices, sources and exact keys) ----

    private static void Rule01_Unchanged()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("x", "idA", "h1"), F("x", "idB", "h1"));
        AssertEmpty(Decide(row));
    }

    private static void Rule02_EditedOneSide()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("x", "idA", "h2"), F("x", "idB", "h1"));
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.Update, Side.B, "x", null, false, 2, "x", "k1"));
        AssertEmptyNotices(plan);
    }

    private static void Rule03_SameChangeBothSides()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("x", "idA", "h2"), F("x", "idB", "h2"));
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.Adopt, Side.B, "x", null, false, 3, "x", "k1"));
        AssertEmptyNotices(plan);
    }

    private static void Rule04_FirstSyncNewerWins()
    {
        var row = Row(null, F("x", "idA", "h2", mtime: T0 + TimeSpan.FromSeconds(10)), F("x", "idB", "h3", mtime: T0));
        var plan = Planner.Decide(PairId, Gen, new[] { row }, Opt(firstSync: true));
        AssertActions(plan, new Exp(ActionKind.Update, Side.B, "x", null, true, 4, "x", NK(Side.A, "idA")));
        AssertNotices(plan, new NExp(4, NoticeKind.AutoConflict, "x"));
    }

    private static void Rule05_FirstSyncTie()
    {
        var row = Row(null, F("x", "idA", "h2", mtime: T0 + TimeSpan.FromSeconds(2)), F("x", "idB", "h3", mtime: T0));
        var plan = Planner.Decide(PairId, Gen, new[] { row }, Opt(firstSync: true));
        AssertActions(plan, new Exp(ActionKind.Hold, Side.B, "x", null, false, 5));
        AssertNotices(plan, new NExp(5, NoticeKind.UserDecision, "x"));
    }

    private static void Rule05b_FirstSyncBoundary()
    {
        var row = Row(null, F("x", "idA", "h2", mtime: T0 + TimeSpan.FromMilliseconds(2001)), F("x", "idB", "h3", mtime: T0));
        var plan = Planner.Decide(PairId, Gen, new[] { row }, Opt(firstSync: true));
        AssertActions(plan, new Exp(ActionKind.Update, Side.B, "x", null, true, 4, "x", NK(Side.A, "idA")));
    }

    private static void Rule06_DeletedOneSide()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), null, F("x", "idB", "h1"), Ev(A: true));
        var plan = Decide(row);
        // Dropbox-target deletes retain app Recovery provenance even when the hashes match.
        AssertActions(plan, new Exp(ActionKind.Delete, Side.B, "x", null, true, 6, null, "k1"));
        AssertEmptyNotices(plan);
    }

    private static void Rule06b_DeletedByKnownAbsence()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), null, F("x", "idB", "h1"),
            new Evidence(false, false, KnownA: true, KnownB: true));
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.Delete, Side.B, "x", null, true, 6, null, "k1"));
        AssertEmptyNotices(plan);
    }

    /// <summary>
    /// Decision 69 extended to ordinary baseline propagation: a rule-6 delete skips Recovery only when the
    /// target is local, the opposite side is Dropbox, and the current hash exactly matches the deleted
    /// revision; any other target keeps the Recovery copy, as does a different or unknown hash.
    /// </summary>
    private static void Rule06c_LocalTargetWithDropboxOppositeSkipsRecovery()
    {
        // Local is A and Dropbox is B; B's baseline row is the revision Dropbox deleted.
        var identical = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("x", "idA", "h1"), null, Ev(B: true));
        AssertActions(Decide(identical), new Exp(ActionKind.Delete, Side.A, "x", null, false, 6, null, "k1"));

        var different = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h2")), F("x", "idA", "h1"), null, Ev(B: true));
        AssertActions(Decide(different), new Exp(ActionKind.Delete, Side.A, "x", null, true, 6, null, "k1"));

        var unknown = Row(Base("k1", F("x", "idA", null), F("x", "idB", "h1")), F("x", "idA", null), null, Ev(B: true));
        AssertActions(Decide(unknown), new Exp(ActionKind.Delete, Side.A, "x", null, true, 6, null, "k1"));
    }

    private static void Rule07_DeletedVsEdited()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), null, F("x", "idB", "h2"), Ev(A: true));
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.CopyNew, Side.A, "x", null, false, 7, "x", "k1"));
        AssertNotices(plan, new NExp(7, NoticeKind.EditBeatsDelete, "x"));
    }

    private static void Rule08_DeletedVsMoved()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), null, F("y", "idB", "h1"), Ev(A: true));
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.CopyNew, Side.A, "y", null, false, 8, "y", "k1"));
        AssertNotices(plan, new NExp(8, NoticeKind.Info, "y"));
    }

    private static void Rule08b_DeletedVsMovedFolder()
    {
        var row = Row(Base("k1", D("F", "idA"), D("F", "idB")), null, D("G", "idB"), Ev(A: true));
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.MkDir, Side.A, "G", null, false, 8, "G", "k1"));
        AssertNotices(plan, new NExp(8, NoticeKind.Info, "G"));
    }

    private static void Rule09_MovedOneSide()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("y", "idA", "h1"), F("x", "idB", "h1"));
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.Move, Side.B, "y", "x", false, 9, "y", "k1"));
        AssertEmptyNotices(plan);
    }

    private static void Rule10_EditAndMoveDifferentPaths()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("y", "idA", "h1"), F("x", "idB", "h2"));
        var plan = Decide(row);
        AssertActions(plan,
            new Exp(ActionKind.CopyNew, Side.A, "x", null, false, 10, "x", SK("k1", Side.B)),
            new Exp(ActionKind.CopyNew, Side.B, "y", null, false, 10, "y", "k1"));
        AssertNotices(plan, new NExp(10, NoticeKind.KeepBoth, "y"));
    }

    private static void Rule11_MovedToTwoPlaces()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("y", "idA", "h1"), F("z", "idB", "h1"));
        var plan = Decide(row);
        AssertActions(plan,
            new Exp(ActionKind.CopyNew, Side.A, "z", null, false, 11, "z", SK("k1", Side.B)),
            new Exp(ActionKind.CopyNew, Side.B, "y", null, false, 11, "y", "k1"));
        AssertNotices(plan, new NExp(11, NoticeKind.KeepBoth, "y"));
    }

    private static void Rule15_DeletedBoth()
    {
        var row = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), null, null, Ev(A: true, B: true));
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.Forget, Side.B, "x", null, false, 15, null, "k1"));
        AssertEmptyNotices(plan);
    }

    private static void Rule16_CreatedBothSame()
    {
        var row = Row(null, F("x", "idA", "h1"), F("x", "idB", "h1"));
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.Adopt, Side.B, "x", null, false, 16, "x", NK(Side.A, "idA")));
        AssertEmptyNotices(plan);
    }

    private static void Rule17_CreatedBothDifferent()
    {
        var observed = T0.AddDays(1);
        var unspecifiedClock = new PlanOptions(TimeSpan.FromSeconds(2), false, Label, 10);
        Require(unspecifiedClock.ObservedAt == DateTimeOffset.MinValue &&
            Planner.Decide(PairId, Gen, [Row(null, F("x", "idA", "h1", mtime: T0),
                F("x", "idB", "h2", mtime: T0.AddMinutes(1)))], unspecifiedClock)
                .Actions.Single().Kind == ActionKind.Hold,
            "an unspecified planner clock holds rather than silently trusting the machine clock");
        var options = Opt() with { ObservedAt = observed };
        foreach (var winnerSide in new[] { Side.A, Side.B })
        {
            var a = F("x", "idA", "h1", mtime: winnerSide == Side.A ? T0.AddSeconds(10) : T0);
            var b = F("x", "idB", "h2", mtime: winnerSide == Side.B ? T0.AddSeconds(10) : T0);
            var loserSide = winnerSide == Side.A ? Side.B : Side.A;
            var winner = winnerSide == Side.A ? a : b;
            var loser = winnerSide == Side.A ? b : a;
            var plan = Planner.Decide(PairId, Gen, [Row(null, a, b)], options);
            AssertActions(plan, new Exp(ActionKind.Update, loserSide, "x", null, true, 4, "x",
                NK(winnerSide, winner.ObjectId)));
            AssertNotices(plan, new NExp(4, NoticeKind.AutoConflict, "x"));
            var action = plan.Actions.Single();
            var retained = plan.Notices.Single().RetainedVersion;
            Require(retained is not null && retained.ActionId == action.ActionId && retained.Side == loserSide &&
                retained.Loser == loser && retained.Winner == winner && action.Source == winner &&
                action.ExpectedTarget == loser, "global newest-wins notice retains exact action, loser and winner identities");
        }
        foreach (var seconds in new[] { 0, 1, 2 })
        {
            var plan = Planner.Decide(PairId, Gen,
                [Row(null, F("x", "idA", "h1", mtime: T0), F("x", "idB", "h2", mtime: T0.AddSeconds(seconds)))], options);
            AssertActions(plan, new Exp(ActionKind.Hold, Side.B, "x", null, false, 5));
            AssertNotices(plan, new NExp(5, NoticeKind.UserDecision, "x"));
        }
        foreach (var unknown in new[] { DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
                     DateTimeOffset.UnixEpoch, DateTimeOffset.FromFileTime(0), observed.AddDays(1) })
        foreach (var unknownSide in new[] { Side.A, Side.B })
        {
            var plan = Planner.Decide(PairId, Gen, [Row(null,
                F("x", "idA", "h1", mtime: unknownSide == Side.A ? unknown : T0),
                F("x", "idB", "h2", mtime: unknownSide == Side.B ? unknown : T0))], options);
            AssertActions(plan, new Exp(ActionKind.Hold, Side.B, "x", null, false, 5));
            AssertNotices(plan, new NExp(5, NoticeKind.UserDecision, "x"));
            Require(plan.Notices.All(n => n.RetainedVersion is null),
                "sentinel or implausibly future time never fabricates a winning or losing version");
        }
    }

    /// <summary>Unconfirmed content (no hashes, same size): hashes requested; at Decide no effect, only an Info notice,
    /// in ongoing and first sync alike (never keep-both, newer-wins or Hold).</summary>
    private static void Rule30c_UnconfirmedNewContentSkipped()
    {
        var row = Row(null, F("x", "idA", null), F("x", "idB", null, mtime: T0 + TimeSpan.FromSeconds(30)));
        var request = Planner.HashRequests(new[] { row });
        Require(request.PathsA.SequenceEqual(new[] { "x" }) && request.PathsB.SequenceEqual(new[] { "x" }), "Rule30c request");
        foreach (var firstSync in new[] { false, true })
        {
            var plan = Decide(firstSync, row);
            AssertNoActions(plan);
            AssertNotices(plan, new NExp(30, NoticeKind.Info, "x"));
            Require(plan.Notices[0].ReasonKey == PlanNotices.ContentUnconfirmed.Key, "Rule30c detail");
        }
    }

    private static void Rule18_OneSideOnly()
    {
        var fileRow = Row(null, F("x", "idA", "h1"), null);
        var folderRow = Row(null, D("d", "idAf"), null);
        var plan = Planner.Decide(PairId, Gen, new[] { fileRow, folderRow }, Opt());
        AssertActions(plan,
            new Exp(ActionKind.MkDir, Side.B, "d", null, false, 18, "d", NK(Side.A, "idAf")),
            new Exp(ActionKind.CopyNew, Side.B, "x", null, false, 18, "x", NK(Side.A, "idA")));
        AssertEmptyNotices(plan);
    }

    private static void Rule19_OneSideOnlyOtherDeleted()
    {
        var row = Row(null, F("x", "idA", "h1"), null, Ev(B: true));
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.CopyNew, Side.B, "x", null, false, 19, "x", NK(Side.A, "idA")));
        AssertNotices(plan, new NExp(19, NoticeKind.EditBeatsDelete, "x"));
    }

    private static void Rule19b_LaterDeletionWins()
    {
        var evidence = Ev(B: true, atB: T0 + TimeSpan.FromHours(1)) with { DeletedHashB = "h1" };
        var row = Row(null, F("x", "idA", "h1", mtime: T0), null, evidence);
        var plan = Decide(row);
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "x", null, false, 19));
        AssertEmptyNotices(plan);
        if (!plan.Actions.Single().NeedsReview) throw new Exception("rule 19 must retain deletion review");
        var edited = Decide(row with { Ev = evidence with { DeletedHashB = "different" } });
        AssertActions(edited, new Exp(ActionKind.CopyNew, Side.B, "x", null, false, 19, "x", NK(Side.A, "idA")));
        AssertNotices(edited, new NExp(19, NoticeKind.EditBeatsDelete, "x"));
        if (edited.Actions.Single().NeedsReview) throw new Exception("upload restore needs no deletion review");
        foreach (var unknown in new[] { row with { Ev = evidence with { DeletedHashB = null } },
                     row with { A = row.A! with { Hash = null } } })
        {
            var pending = Decide(unknown);
            AssertActions(pending);
            AssertNotices(pending, new NExp(30, NoticeKind.Info, "x"));
        }
    }

    private static void Rule20_CaseOnlyRename()
    {
        foreach (var changed in new[] { Side.A, Side.B })
        {
            var a = F(changed == Side.A ? "doc.txt" : "Doc.txt", "idA", "h1");
            var b = F(changed == Side.B ? "doc.txt" : "Doc.txt", "idB", "h1");
            var row = Row(Base("k1", a with { Path = "Doc.txt" }, b with { Path = "Doc.txt" }), a, b);
            var plan = Decide(row);
            AssertActions(plan, new Exp(ActionKind.Move, Opposite(changed), "doc.txt", "Doc.txt", false, 20, "doc.txt", "k1"));
            AssertNotices(plan);
        }
    }
}
