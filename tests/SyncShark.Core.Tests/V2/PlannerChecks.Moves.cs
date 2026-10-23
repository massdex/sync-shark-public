namespace Massdex.SyncShark.V2;

/// <summary>Planner checks for occupied destinations (rules 4, 5, 12-14) and folder deletes and moves (rules 23-25).</summary>
internal static partial class PlannerChecks
{
    // ---- occupied destinations ----------------------------------------------

    private static void Rule12_MoveOntoIdenticalOccupant()
    {
        var row1 = Row(Base("k1", F("p", "idA1", "h1"), F("p", "idB1", "h1")), F("q", "idA1", "h1"), F("p", "idB1", "h1"));
        var row2 = Row(Base("k2", F("q", "idA2", "h1"), F("q", "idB2", "h1")), null, F("q", "idB2", "h1"), Ev(A: true));
        var plan = Decide(false, row1, row2);
        AssertActions(plan,
            new Exp(ActionKind.Move, Side.B, "q", "p", false, 9, "q", "k1"),
            new Exp(ActionKind.Delete, Side.B, "q", null, true, 12, null, "k2"));
        AssertEmptyNotices(plan);
    }

    /// <summary>Finding 4: the move completes onto the identical occupant; the old path goes recovery-first, with the
    /// moved item as the Delete's Source (its evidence); the mirror create of the occupant is dropped.</summary>
    private static void Rule12b_IdenticalOccupantCompletesTheMove()
    {
        var row1 = Row(Base("k1", F("p", "idA", "h1"), F("p", "idB", "h1")), F("q", "idA", "h1"), F("p", "idB", "h1"));
        var row2 = Row(null, null, F("q", "idB2", "h1"));
        var plan = Decide(false, row1, row2);
        AssertActions(plan,
            new Exp(ActionKind.Delete, Side.B, "p", null, true, 12, "q"),
            new Exp(ActionKind.Adopt, Side.B, "q", null, false, 12, "q", "k1"));
        var adopt = plan.Actions.Single(a => a.Kind == ActionKind.Adopt);
        Require(adopt.Source!.ObjectId == "idA" && adopt.ExpectedTarget!.ObjectId == "idB2", "Rule12b adopt pair");
        Require(plan.Actions.Single(a => a.Kind == ActionKind.Delete).ExpectedTarget!.ObjectId == "idB", "Rule12b victim");
        AssertEmptyNotices(plan);
        // C19: the old default supplied unknown coverage while expecting ordinary creation.
        // Preserve a positive unknown-coverage check rather than hiding that boundary in fixtures.
        var unknown = Decide(false, row2 with { Ev = new Evidence(false, false, false, false) });
        AssertNoActions(unknown);
        AssertNotices(unknown, new NExp(30, NoticeKind.Info, "q"));
        Require(unknown.Notices.Single().ReasonKey == PlanNotices.CannotRead.Key,
            "unobserved counterpart retains the exact scan retry handoff");
    }

    /// <summary>Finding 4: Adopt stays Target B / Source A when the completed move lands on side A.</summary>
    private static void Rule12c_IdenticalOccupantOnSideA()
    {
        var row1 = Row(Base("k1", F("p", "idA", "h1"), F("p", "idB", "h1")), F("p", "idA", "h1"), F("q", "idB", "h1"));
        var row2 = Row(null, F("q", "idA2", "h1"), null);
        var plan = Decide(false, row1, row2);
        AssertActions(plan,
            new Exp(ActionKind.Delete, Side.A, "p", null, true, 12, "q"),
            new Exp(ActionKind.Adopt, Side.B, "q", null, false, 12, "q", "k1"));
        var adopt = plan.Actions.Single(a => a.Kind == ActionKind.Adopt);
        Require(adopt.Source!.ObjectId == "idA2" && adopt.ExpectedTarget!.ObjectId == "idB", "Rule12c adopt pair");
        AssertEmptyNotices(plan);
    }

    /// <summary>Rule 12 onto another row's baselined occupant: the Adopt retires that correspondence on Done, so no
    /// separate Forget precedes the link and the plan never holds two claims on one object.</summary>
    private static void Rule12d_IdenticalOccupantRetiresBaselineWithAdopt()
    {
        var mover = Row(Base("k1", F("p", "idA1", "h1"), F("p", "idB1", "h1")), F("q", "idA1", "h1"),
            F("p", "idB1", "h1"));
        var occupant = Row(Base("k2", F("q", "idA2", "h9"), F("q", "idB2", "h2")), null, F("q", "idB2", "h1"));
        var plan = Decide(false, mover, occupant);
        AssertActions(plan,
            new Exp(ActionKind.Delete, Side.B, "p", null, true, 12, "q"),
            new Exp(ActionKind.Adopt, Side.B, "q", null, false, 12, "q", "k1"));
        var adopt = plan.Actions.Single(action => action.Kind == ActionKind.Adopt);
        Require(adopt.ObsoleteBaselineKey == "k2" &&
            plan.Actions.All(action => action.Kind != ActionKind.Forget),
            "Rule12d Adopt carries the replaced baseline and no Forget action precedes it");
        AssertEmptyNotices(plan);
    }

    /// <summary>First-sync incoming newer over another row's baselined occupant: the winning Update retires the
    /// occupant's correspondence on Done instead of emitting a separate Forget.</summary>
    private static void Rule14j_IncomingWinsRetiresBaselineWithUpdate()
    {
        var plan = Decide(true, FolderFToGOnA(), RebasedIncoming(T0 + TimeSpan.FromSeconds(10)),
            RebasedOccupant(T0));
        AssertActions(plan,
            new Exp(ActionKind.Update, Side.A, "G/c", null, true, 4, "G/c", "kc"),
            new Exp(ActionKind.Move, Side.B, "G", "F", false, 25, "G", "k1"));
        var update = plan.Actions.Single(action => action.Kind == ActionKind.Update);
        Require(update.Source!.ObjectId == "idBc" && update.ExpectedTarget!.ObjectId == "idAo" &&
            update.ObsoleteBaselineKey == "ko" && plan.Actions.All(action => action.Kind != ActionKind.Forget),
            "Rule14j winning Update carries the occupant's replaced baseline");
    }

    /// <summary>First-sync occupant newer: the occupant's Update retires the losing incoming correspondence on Done,
    /// never through a standalone Forget.</summary>
    private static void Rule14k_OccupantWinsRetiresBaselineWithUpdate()
    {
        var plan = Decide(true, FolderFToGOnA(), RebasedIncoming(T0), RebasedOccupant(T0 + TimeSpan.FromSeconds(10)));
        AssertActions(plan,
            new Exp(ActionKind.Move, Side.B, "G", "F", false, 25, "G", "k1"),
            new Exp(ActionKind.Update, Side.B, "G/c", null, true, 4, "G/c", "ko"));
        var update = plan.Actions.Single(action => action.Kind == ActionKind.Update);
        Require(update.Source!.ObjectId == "idAo" && update.ExpectedTarget!.ObjectId == "idBc" &&
            update.ObsoleteBaselineKey == "kc" && plan.Actions.All(action => action.Kind != ActionKind.Forget),
            "Rule14k occupant's Update carries the incoming row's replaced baseline");
    }

    /// <summary>A rebased create (rule 7 on B) landing after the folder move on A:G/c.</summary>
    private static PlanRow RebasedIncoming(DateTimeOffset mtime) =>
        Row(Base("kc", F("F/c", "idAc", "h0"), F("F/c", "idBc", "h0")), null,
            F("F/c", "idBc", "h3", mtime: mtime), Ev(A: true));

    /// <summary>The independent occupant of A:G/c with its own baseline; its one mirror create is dropped by the clash.</summary>
    private static PlanRow RebasedOccupant(DateTimeOffset mtime) =>
        Row(Base("ko", F("G/c", "idAo", "h0"), F("G/c", "idBo", "h0")),
            F("G/c", "idAo", "h2", mtime: mtime), null, Ev(B: true));

    private static void Rule13_MoveOntoChangedOccupant()
    {
        var row1 = Row(Base("k1", F("p", "idA1", "h1"), F("p", "idB1", "h1")), F("q", "idA1", "h1"), F("p", "idB1", "h1"));
        var row2 = Row(Base("k2", F("q", "idA2", "h9"), F("q", "idB2", "h9")), null, F("q", "idB2", "h9"), Ev(A: true));
        var plan = Decide(false, row1, row2);
        AssertActions(plan,
            new Exp(ActionKind.Move, Side.B, "q", "p", false, 9, "q", "k1"),
            new Exp(ActionKind.Delete, Side.B, "q", null, true, 13, null, "k2"));
        AssertNotices(plan, new NExp(13, NoticeKind.AutoConflict, "q"));
    }

    private static void Rule14_DestinationOccupied()
    {
        var row1 = Row(Base("k1", F("p", "idA1", "h1"), F("p", "idB1", "h1")), F("q", "idA1", "h1"), F("p", "idB1", "h1"));
        var row2 = Row(Base("k2", F("q", "idA2", "h7"), F("q", "idB2", "h7")), null, F("q", "idB2", "h7"));
        var label = KeepBothLabel.For("q", "Local", T0, Label, 1);
        var plan = Decide(false, row1, row2);
        AssertNoActions(plan);
        AssertNotices(plan, new NExp(14, NoticeKind.UserDecision, "q"));
        Require(plan.Notices.Single().ConflictVersions?.Incoming?.FromPath == "p", "Rule14 preserves incoming move identity");
    }

    /// <summary>Finding 2: first sync, the moved file is newer: the occupant goes to Recovery and the move stays a move.</summary>
    private static void Rule14b_FirstSyncOccupiedIncomingNewer()
    {
        var plan = Decide(true, MovedPToQ(T0 + TimeSpan.FromSeconds(10)), OccupantQ(T0));
        AssertActions(plan,
            new Exp(ActionKind.Move, Side.B, "q", "p", false, 9, "q", "k1"),
            new Exp(ActionKind.Delete, Side.B, "q", null, true, 4, "q", "k2"));
        AssertNotices(plan, new NExp(4, NoticeKind.AutoConflict, "q"));
    }

    private static void Rule14c_RedirectMovesDependentUpdate()
    {
        var row1 = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("y", "idA", "h2"), F("x", "idB", "h1"));
        var row2 = Row(Base("k2", F("y", "idA2", "h7"), F("y", "idB2", "h7")), null, F("y", "idB2", "h7"));
        var label = KeepBothLabel.For("y", "Local", T0, Label, 1);
        var plan = Decide(false, row1, row2);
        AssertNoActions(plan);
        Require(plan.Notices.Single().ConflictVersions?.A.Hash == "h2", "Rule14c retains edited incoming content, not baseline content");
        AssertNotices(plan, new NExp(14, NoticeKind.UserDecision, "y"));
    }

    /// <summary>Finding 2: first sync, the occupant is newer: it overwrites the moved file (older to Recovery) and the
    /// moved file's old copy on the other side goes to Recovery too; nothing is left at the old path.</summary>
    private static void Rule14d_FirstSyncOccupiedOccupantNewer()
    {
        var plan = Decide(true, MovedPToQ(T0), OccupantQ(T0 + TimeSpan.FromSeconds(10)));
        AssertActions(plan,
            new Exp(ActionKind.Update, Side.A, "q", null, true, 4, "q", "k2"),
            new Exp(ActionKind.Delete, Side.B, "p", null, true, 4, "q", "k1"));
        var moveUpdate = plan.Actions.Single(action => action.Kind == ActionKind.Update);
        var moveDelete = plan.Actions.Single(action => action.Kind == ActionKind.Delete);
        Require(moveUpdate.ObsoleteBaselineKey is null && moveDelete.ObjectKey == "k1",
            "Rule14d a Move's paired Delete owns the stale key; its Update retires nothing");
        AssertNotices(plan, new NExp(4, NoticeKind.AutoConflict, "q"));
    }

    /// <summary>Finding 2: a first-sync tie holds the path; the moved-and-edited row's Update is held with it.</summary>
    private static void Rule14e_FirstSyncOccupiedTieHoldsDependants()
    {
        var row1 = Row(Base("k1", F("p", "idA", "h1"), F("p", "idB", "h1")),
            F("q", "idA", "h2", mtime: T0 + TimeSpan.FromSeconds(1)), F("p", "idB", "h1"));
        var plan = Decide(true, row1, OccupantQ(T0));
        AssertActions(plan, new Exp(ActionKind.Hold, Side.B, "q", null, false, 5));
        AssertNotices(plan, new NExp(5, NoticeKind.UserDecision, "q"));
        foreach (var badTime in new[] { DateTimeOffset.MinValue, DateTimeOffset.MaxValue })
        foreach (var incomingBad in new[] { false, true })
        {
            var invalid = Decide(true, MovedPToQ(incomingBad ? badTime : T0),
                OccupantQ(incomingBad ? T0 : badTime));
            AssertActions(invalid, new Exp(ActionKind.Hold, Side.B, "q", null, false, 5));
            AssertNotices(invalid, new NExp(5, NoticeKind.UserDecision, "q"));
        }
    }

    /// <summary>Finding 2: an occupant with no history mirrors the move as a create; the move's resolution owns it.</summary>
    private static void Rule14f_FirstSyncOccupantMirrorIsOwned()
    {
        var occupant = Row(null, null, F("q", "idB2", "h7", mtime: T0 + TimeSpan.FromSeconds(10)));
        var plan = Decide(true, MovedPToQ(T0), occupant);
        AssertActions(plan,
            new Exp(ActionKind.Update, Side.A, "q", null, true, 4, "q", NK(Side.B, "idB2")),
            new Exp(ActionKind.Delete, Side.B, "p", null, true, 4, "q", "k1"));
        AssertNotices(plan, new NExp(4, NoticeKind.AutoConflict, "q"));
    }

    /// <summary>Finding 3: a redirect moves only its own dependants; the occupant's own move keeps its paths.</summary>
    private static void Rule14g_RedirectLeavesUnrelatedActions()
    {
        var row1 = Row(Base("k1", F("x", "idA", "h1"), F("x", "idB", "h1")), F("y", "idA", "h1"), F("x", "idB", "h1"));
        var row2 = Row(Base("k2", F("y", "idA2", "h7"), F("y", "idB2", "h7")), F("z", "idA2", "h7"), F("y", "idB2", "h7"));
        var label = KeepBothLabel.For("y", "Local", T0, Label, 1);
        var plan = Decide(false, row1, row2);
        AssertActions(plan, new Exp(ActionKind.Move, Side.B, "z", "y", false, 9, "z", "k2"));
        Require(plan.Actions.Single(a => a.ObjectKey == "k2").ExpectedTarget!.Path == "y", "Rule14g independent occupant move continues");
        AssertNotices(plan, new NExp(14, NoticeKind.UserDecision, "y"));
    }

    /// <summary>A10: a move onto another row's file whose content cannot be confirmed (no hashes, same size) requests
    /// both hashes and, until they arrive, does nothing in either mode (no keep-both, no newer-wins).</summary>
    private static void Rule14h_UnconfirmedCollisionSkipped()
    {
        var mover = Row(Base("k1", F("p", "idA", "h1"), F("p", "idB", "h1")), F("q", "idA", null), F("p", "idB", "h1"));
        var occupant = Row(Base("k2", F("q", "idA2", "h7"), F("q", "idB2", "h7")), null, F("q", "idB2", null));
        var request = Planner.HashRequests(new[] { mover, occupant });
        Require(request.PathsA.SequenceEqual(new[] { "q" }) && request.PathsB.SequenceEqual(new[] { "q" }), "Rule14h request");
        foreach (var firstSync in new[] { false, true })
        {
            var plan = Decide(firstSync, mover, occupant);
            AssertNoActions(plan);
            AssertNotices(plan, new NExp(30, NoticeKind.Info, "q"));
            Require(plan.Notices[0].ReasonKey == PlanNotices.ContentUnconfirmed.Key, "Rule14h detail");
        }
    }

    private static PlanRow MovedPToQ(DateTimeOffset mtime) =>
        Row(Base("k1", F("p", "idA", "h1"), F("p", "idB", "h1")), F("q", "idA", "h1", mtime: mtime), F("p", "idB", "h1"));

    private static PlanRow OccupantQ(DateTimeOffset mtime) =>
        Row(Base("k2", F("q", "idA2", "h7"), F("q", "idB2", "h7", mtime: mtime)), null, F("q", "idB2", "h7", mtime: mtime));

    // ---- folder deletes (rules 23 and 24) ------------------------------------

    private static void Rule23_FolderDeletedUnchanged()
    {
        var folder = Row(Base("k1", D("F", "idAf"), D("F", "idBf")), null, D("F", "idBf"), Ev(A: true));
        var child = Row(Base("k2", F("F/c", "idAc", "h1"), F("F/c", "idBc", "h1")), null, F("F/c", "idBc", "h1"), Ev(A: true));
        var plan = Decide(false, folder, child);
        AssertActions(plan, new Exp(ActionKind.Delete, Side.B, "F", null, true, 23, null, "k1"));
        AssertEmptyNotices(plan);
    }

    /// <summary>Finding 5: nested folders with every item deleted collapse to one Delete; no Hold.</summary>
    private static void Rule23b_NestedFolderDeleteCollapses()
    {
        var rows = new[]
        {
            Row(Base("k1", D("F", "a1"), D("F", "b1")), null, D("F", "b1"), Ev(A: true)),
            Row(Base("k2", D("F/G", "a2"), D("F/G", "b2")), null, D("F/G", "b2"), Ev(A: true)),
            Row(Base("k3", F("F/G/c", "a3", "h1"), F("F/G/c", "b3", "h1")), null, F("F/G/c", "b3", "h1"), Ev(A: true)),
            Row(Base("k4", F("F/d", "a4", "h1"), F("F/d", "b4", "h1")), null, F("F/d", "b4", "h1"), Ev(A: true)),
        };
        var plan = Decide(false, rows);
        AssertActions(plan, new Exp(ActionKind.Delete, Side.B, "F", null, true, 23, null, "k1"));
        AssertEmptyNotices(plan);
    }

    private static void Rule24_FolderDeletedContentsChanged()
    {
        var folder = Row(Base("k1", D("F", "idAf"), D("F", "idBf")), null, D("F", "idBf"), Ev(A: true));
        var edited = Row(Base("k2", F("F/c", "idAc", "h1"), F("F/c", "idBc", "h1")), null, F("F/c", "idBc", "h2"), Ev(A: true));
        var other = Row(Base("k3", F("F/o", "idAo", "h1"), F("F/o", "idBo", "h1")), null, F("F/o", "idBo", "h1"), Ev(A: true));
        var plan = Decide(false, folder, edited, other);
        AssertActions(plan,
            new Exp(ActionKind.MkDir, Side.A, "F", null, false, 24, "F", "k1"),
            new Exp(ActionKind.CopyNew, Side.A, "F/c", null, false, 7, "F/c", "k2"),
            new Exp(ActionKind.Delete, Side.B, "F/o", null, true, 6, null, "k3"));
        AssertNotices(plan, new NExp(7, NoticeKind.EditBeatsDelete, "F/c"), new NExp(24, NoticeKind.EditBeatsDelete, "F"));
    }

    /// <summary>Finding 5: a descendant with no deletion evidence keeps the folder (rule 24), never a rule-23 Hold.</summary>
    private static void Rule24b_UnknownDescendantKeepsFolder()
    {
        var folder = Row(Base("k1", D("F", "idAf"), D("F", "idBf")), null, D("F", "idBf"), Ev(A: true));
        var unknown = Row(Base("k2", F("F/c", "idAc", "h1"), F("F/c", "idBc", "h1")), null, F("F/c", "idBc", "h1"));
        var plan = Decide(false, folder, unknown);
        AssertActions(plan, new Exp(ActionKind.MkDir, Side.A, "F", null, false, 24, "F", "k1"));
        AssertNotices(plan, new NExp(24, NoticeKind.EditBeatsDelete, "F"));
    }

    /// <summary>Finding 6: an edited descendant whose hash is unresolved is requested for hashing and, until it is
    /// resolved, blocks the whole-folder delete (folder kept, Info notice for the file).</summary>
    private static void Rule24c_UnhashedDescendantKeepsFolder()
    {
        var folder = Row(Base("k1", D("F", "idAf"), D("F", "idBf")), null, D("F", "idBf"), Ev(A: true));
        var unhashed = Row(Base("k2", F("F/c", "idAc", "h1"), F("F/c", "idBc", "h1")), null,
            F("F/c", "idBc", null, mtime: T0 + TimeSpan.FromSeconds(5)), Ev(A: true));
        var other = Row(Base("k3", F("F/o", "idAo", "h1"), F("F/o", "idBo", "h1")), null, F("F/o", "idBo", "h1"), Ev(A: true));
        var request = Planner.HashRequests(new[] { folder, unhashed, other });
        Require(request.PathsB.SequenceEqual(new[] { "F/c" }) && request.PathsA.Count == 0, "Rule24c hash request");
        var plan = Decide(false, folder, unhashed, other);
        AssertActions(plan,
            new Exp(ActionKind.MkDir, Side.A, "F", null, false, 24, "F", "k1"),
            new Exp(ActionKind.Delete, Side.B, "F/o", null, true, 6, null, "k3"));
        AssertNotices(plan, new NExp(30, NoticeKind.Info, "F/c"), new NExp(24, NoticeKind.EditBeatsDelete, "F"));
    }

    // ---- folder moves (rule 25) ----------------------------------------------

    private static void Rule25_FolderMoved()
    {
        var folder = Row(Base("k1", D("F", "idAf"), D("F", "idBf")), D("G", "idAf"), D("F", "idBf"));
        var movedChild = Row(Base("k2", F("F/c", "idAc", "h1"), F("F/c", "idBc", "h1")), F("G/c", "idAc", "h1"), F("F/c", "idBc", "h1"));
        var deletedChild = Row(Base("k3", F("F/e", "idAe", "h1"), F("F/e", "idBe", "h1")), null, F("F/e", "idBe", "h1"), Ev(A: true));
        var plan = Decide(false, folder, movedChild, deletedChild);
        AssertActions(plan,
            new Exp(ActionKind.Move, Side.B, "G", "F", false, 25, "G", "k1"),
            new Exp(ActionKind.Delete, Side.B, "G/e", null, true, 6, null, "k3"));
        AssertEmptyNotices(plan);
    }

    private static void Rule25b_FolderMoveRebasesSource()
    {
        var folder = Row(Base("k1", D("F", "idAf"), D("F", "idBf")), D("G", "idAf"), D("F", "idBf"));
        var editedChild = Row(Base("k2", F("F/c", "idAc", "h1"), F("F/c", "idBc", "h1")), null, F("F/c", "idBc", "h2"), Ev(A: true));
        var plan = Decide(false, folder, editedChild);
        AssertActions(plan,
            new Exp(ActionKind.Move, Side.B, "G", "F", false, 25, "G", "k1"),
            new Exp(ActionKind.CopyNew, Side.A, "G/c", null, false, 7, "G/c", "k2"));
        AssertNotices(plan, new NExp(7, NoticeKind.EditBeatsDelete, "F/c"));
    }

    /// <summary>Finding 1: a folder move on B neither suppresses nor rewrites side-A moves, nor B creates of new items.</summary>
    private static void Rule25c_FolderMoveIsPerSide()
    {
        var folder = Row(Base("k1", D("F", "idAf"), D("F", "idBf")), D("G", "idAf"), D("F", "idBf"));
        var newOnA = Row(null, F("F/n", "idAn", "h1"), null);
        var movedOnB = Row(Base("k5", F("F/x", "idAx", "h1"), F("F/x", "idBx", "h1")), F("F/x", "idAx", "h1"), F("H/x", "idBx", "h1"));
        var mirrorOnB = Row(Base("k6", F("F/y", "idAy", "h1"), F("F/y", "idBy", "h1")), F("F/y", "idAy", "h1"), F("G/y", "idBy", "h1"));
        var plan = Decide(false, folder, newOnA, movedOnB, mirrorOnB);
        AssertActions(plan,
            new Exp(ActionKind.Move, Side.A, "G/y", "F/y", false, 9, "G/y", "k6"),
            new Exp(ActionKind.Move, Side.A, "H/x", "F/x", false, 9, "H/x", "k5"),
            new Exp(ActionKind.CopyNew, Side.B, "F/n", null, false, 18, "F/n", NK(Side.A, "idAn")),
            new Exp(ActionKind.Move, Side.B, "G", "F", false, 25, "G", "k1"));
        Require(plan.Actions.Single(a => a.ObjectKey == "k5").ExpectedTarget!.Path == "F/x", "Rule25c side A untouched");
        AssertEmptyNotices(plan);
    }

    /// <summary>Findings 3 and 7: a folder move redirected to a label takes the children it carries, not unrelated creates.</summary>
    private static void Rule25d_RedirectedFolderMoveTakesItsChildren()
    {
        var folder = Row(Base("k1", D("F", "idAf"), D("F", "idBf")), D("G", "idAf"), D("F", "idBf"));
        var newFolderOnB = Row(null, null, D("G", "idBg"));
        var deletedChild = Row(Base("k3", F("F/e", "idAe", "h1"), F("F/e", "idBe", "h1")), null, F("F/e", "idBe", "h1"), Ev(A: true));
        var newOnA = Row(null, F("G/n", "idAn", "h1"), null);
        var label = KeepBothLabel.For("G", "Local", T0, Label, 1);
        var plan = Decide(false, folder, newFolderOnB, deletedChild, newOnA);
        AssertNoActions(plan);
        Require(plan.Notices.Single().ConflictVersions?.A.ObjectId == "idAf" &&
            plan.Notices.Single().ConflictVersions?.B.ObjectId == "idBg", "Rule25d folder collision preserves both tree identities");
        AssertNotices(plan, new NExp(14, NoticeKind.UserDecision, "G"));
    }
}
