namespace Massdex.SyncShark.V2;

/// <summary>Planner checks for creates whose destination a folder move rebased onto an occupied path.</summary>
internal static partial class PlannerChecks
{
    private static string LocalGc => KeepBothLabel.For("G/c", "Local", T0, Label, 1);
    private static string DropboxGc => KeepBothLabel.For("G/c", "Dropbox", T0, Label, 1);

    /// <summary>Rule25b with A's child present: the restore rebased to A:G/c meets A's own new G/c; both are kept.</summary>
    private static void Rule25b2_RestoreOntoOccupiedChildKeepsBoth()
    {
        var editedChild = Row(Base("k2", F("F/c", "idAc", "h1"), F("F/c", "idBc", "h1")), null, F("F/c", "idBc", "h2"), Ev(A: true));
        var newOnA = Row(null, F("G/c", "idAn", "h9"), null);
        var plan = Decide(false, FolderFToGOnA(), editedChild, newOnA);
        AssertActions(plan,
            new Exp(ActionKind.CopyNew, Side.A, DropboxGc, null, false, 14, "G/c", "k2"),
            new Exp(ActionKind.Move, Side.B, "G", "F", false, 25, "G", "k1"),
            new Exp(ActionKind.CopyNew, Side.B, LocalGc, null, false, 14, "G/c", NK(Side.A, "idAn")));
        AssertNotices(plan, new NExp(7, NoticeKind.EditBeatsDelete, "F/c"),
            new NExp(14, NoticeKind.KeepBoth, DropboxGc), new NExp(14, NoticeKind.KeepBoth, LocalGc));
    }

    /// <summary>
    /// Folder move plus opposite-side child edit (A: G/c=h1 via the rename, B: F/c=h2): rule 10's two copies both land
    /// on G/c once B's folder moves. Ongoing: each side keeps its own G/c and gets the other version under a label.
    /// </summary>
    private static void Rule25e_RebasedTwoPlaceCopyKeepsBoth()
    {
        var plan = Decide(false, FolderFToGOnA(), MovedVsEditedChild(T0));
        AssertActions(plan,
            new Exp(ActionKind.CopyNew, Side.A, DropboxGc, null, false, 14, "G/c", SK("k2", Side.B)),
            new Exp(ActionKind.Move, Side.B, "G", "F", false, 25, "G", "k1"),
            new Exp(ActionKind.CopyNew, Side.B, LocalGc, null, false, 14, "G/c", "k2"));
        AssertNotices(plan, new NExp(10, NoticeKind.KeepBoth, "G/c"),
            new NExp(14, NoticeKind.KeepBoth, DropboxGc), new NExp(14, NoticeKind.KeepBoth, LocalGc));
    }

    /// <summary>The same collision in first sync: B's newer version overwrites A:G/c (older to Recovery).</summary>
    private static void Rule25f_RebasedTwoPlaceCopyFirstSyncNewerWins()
    {
        var plan = Decide(true, FolderFToGOnA(), MovedVsEditedChild(T0 + TimeSpan.FromSeconds(10)));
        AssertActions(plan,
            new Exp(ActionKind.Update, Side.A, "G/c", null, true, 4, "G/c", "k2"),
            new Exp(ActionKind.Move, Side.B, "G", "F", false, 25, "G", "k1"));
        var update = plan.Actions.Single(a => a.Kind == ActionKind.Update);
        Require(update.Source!.ObjectId == "idBc" && update.ExpectedTarget!.ObjectId == "idAc", "Rule25f versions");
        AssertNotices(plan, new NExp(4, NoticeKind.AutoConflict, "G/c"));
    }

    /// <summary>The same collision in first sync within the tie window: held for the user, nothing else for the row.</summary>
    private static void Rule25g_RebasedTwoPlaceCopyFirstSyncTieHolds()
    {
        var plan = Decide(true, FolderFToGOnA(), MovedVsEditedChild(T0 + TimeSpan.FromSeconds(1)));
        AssertActions(plan,
            new Exp(ActionKind.Move, Side.B, "G", "F", false, 25, "G", "k1"),
            new Exp(ActionKind.Hold, Side.B, "G/c", null, false, 5));
        AssertNotices(plan, new NExp(5, NoticeKind.UserDecision, "G/c"));
    }

    /// <summary>A move onto a path vacated by a Delete whose victim's content is unconfirmed: no rule 12/13, the move does
    /// nothing, the victim's own (rule 6) Delete stands, the victim's hash is requested.</summary>
    private static void Rule13b_UnconfirmedVacatedDestinationSkipped()
    {
        var mover = Row(Base("k1", F("p", "idA", "h1"), F("p", "idB", "h1")), F("q", "idA", "h1"), F("p", "idB", "h1"));
        var victim = Row(Base("k2", F("q", "idA2", "h7"), F("q", "idB2", "h7")), null, F("q", "idB2", null), Ev(A: true));
        var request = Planner.HashRequests(new[] { mover, victim });
        Require(request.PathsA.Count == 0 && request.PathsB.SequenceEqual(new[] { "q" }), "Rule13b request");
        var plan = Decide(false, mover, victim);
        AssertActions(plan, new Exp(ActionKind.Delete, Side.B, "q", null, true, 6, null, "k2"));
        AssertNotices(plan, new NExp(30, NoticeKind.Info, "q"));
    }

    /// <summary>Two creates at one path whose versions are unconfirmed (a redirected folder's restored child and a new
    /// file at the unredirected path): every create of the group is skipped and both hashes are requested.</summary>
    private static void Rule14i_UnconfirmedDuplicateCreatesAllSkipped()
    {
        var newFolderOnB = Row(null, null, D("G", "idBg"));
        var editedChild = Row(Base("k2", F("F/c", "idAc", "h1", versionId: "v1"), F("F/c", "idBc", "h1", versionId: "v1")),
            null, F("F/c", "idBc", null, versionId: "v2"), Ev(A: true));
        var newOnB = Row(null, null, F("G/c", "idBn", null));
        var rows = new[] { FolderFToGOnA(), newFolderOnB, editedChild, newOnB };
        var request = Planner.HashRequests(rows);
        Require(request.PathsA.Count == 0 && request.PathsB.SequenceEqual(new[] { "F/c", "G/c" }), "Rule14i request");
        var label = KeepBothLabel.For("G", "Local", T0, Label, 1);
        var plan = Decide(false, rows);
        AssertActions(plan, new Exp(ActionKind.Move, Side.B, label, "F", false, 14, "G", "k1"));
        AssertNotices(plan, new NExp(7, NoticeKind.EditBeatsDelete, "F/c"), new NExp(14, NoticeKind.KeepBoth, label),
            new NExp(30, NoticeKind.Info, "G/c"));
    }

    private static PlanRow FolderFToGOnA() =>
        Row(Base("k1", D("F", "idAf"), D("F", "idBf")), D("G", "idAf"), D("F", "idBf"));

    private static PlanRow MovedVsEditedChild(DateTimeOffset editedMtime) =>
        Row(Base("k2", F("F/c", "idAc", "h1"), F("F/c", "idBc", "h1")), F("G/c", "idAc", "h1"),
            F("F/c", "idBc", "h2", mtime: editedMtime));
}
