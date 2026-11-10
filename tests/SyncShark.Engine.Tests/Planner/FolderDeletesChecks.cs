using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks for FolderDeletes (`--engine-checks folder-deletes`).
/// The first-run T11 table stays with these checks because the registry maps one check to one owner file, so a
/// second file would create a second owner for the same rule (design section 10).</summary>
public static class FolderDeletesChecks
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly RowContext NoContext = new(false, false, false, null, false, false, false, false);
    private static readonly FolderChildFacts Listed = new(InThisScan: true, InScope: true,
        HoldsNestedRoot: false, Restored: false, Unreadable: false);

    public static void Run()
    {
        ClassifyCases();
        FirstRunCases();
        ProjectedFirstRunFolders();
        ProtectionCases();
        SelectionCases();
        PolicyCases();
        CostProbe();
    }

    // T11 first-run finalization (P4-03). No clock is carried in RowContext (P6 phase 1), so every case supplies
    // current facts; time comparisons belong to the Decide/TimeRules checks.
    private static void FirstRunCases()
    {
        FirstRunGuards();
        FirstRunFailureCases();
        FirstRunEvidenceCases();
        FirstRunSweep();
    }

    // T11-a and the common guards: a caller cannot finalize a folder that any earlier rule keeps. The ordered
    // guards stay in one method so their precedence is read in the owner's order.
    private static void FirstRunGuards()
    {
        var folder = FolderEntry("a", "lf");
        var file = FileEntry("a", "lf", 1, T0, "h");
        var saved = Synced("a", "a", true, "lf", 0, T0, "");
        var deletion = new DeletionEvidence.DeletedPath("a", "a");

        RequireNone(FirstRun(FolderRow(deletion,
                Eligible(outOfScope: true, skipped: true, held: true), extra: new[] { file }), true),
            NoneReason.OutOfScope, "an out-of-scope folder leaves before skip, clash and hold facts");
        RequireNone(FirstRun(FolderRow(deletion, Eligible(skipped: true)), true),
            NoneReason.Skipped, "a skipped folder is not finalized");
        RequireNone(FirstRun(FolderRow(deletion, local: folder with { Skip = "skip" }), true),
            NoneReason.Skipped, "a locally skipped folder is not finalized");
        RequireNone(FirstRun(FolderRow(deletion, Eligible(held: true, heldKind: IssueKind.Tie)), true),
            NoneReason.Held, "a held folder stays held");
        RequireNone(FirstRun(FolderRow(deletion, Eligible(holdsNestedRoot: true)), true),
            NoneReason.Held, "a nested pair root keeps the folder");
        RequireNone(FirstRun(FolderRow(deletion, Eligible(completeInventory: false)), true),
            NoneReason.Held, "an incomplete inventory grants no first-run folder authority");
        RequireNone(FirstRun(FolderRow(deletion, Eligible(deletionEligible: false)), true),
            NoneReason.Held, "a row not selected for deletion evidence is not finalized");
        RequireNone(FirstRun(FolderRow(deletion, synced: saved), true),
            NoneReason.Held, "a saved folder uses the ordinary table, not first-run finalization");
        RequireNone(FirstRun(FolderRow(deletion, local: file), true),
            NoneReason.Held, "a file is not first-run folder finalization");
        RequireNone(FirstRun(FolderRow(deletion, dropbox: FolderEntry("a", "rf")), true),
            NoneReason.Held, "a bilateral folder is not local-only");
        RequireNone(FirstRun(new Row("a", null, null, null, Array.Empty<Entry>(), null, Eligible(), deletion), true),
            NoneReason.Held, "a folder absent on the deleting side is not finalized");
        RequireNone(FirstRun(FolderRow(deletion), physicallyEmpty: null),
            NoneReason.Held, "unreadable actual contents hold the folder");
        RequireNone(FirstRun(FolderRow(deletion, Eligible(destination: null, held: true)), true),
            NoneReason.Held, "a hold wins over a missing mapped destination");
        RequireNone(FirstRun(FolderRow(deletion, Eligible(skipped: true), extra: new[] { file }), true),
            NoneReason.Skipped, "a skip wins over the clash fact");

        var clashRow = FolderRow(deletion, extra: new[] { file });
        var clash = FirstRun(clashRow, true);
        RequireNotice(clash, IssueKind.NameClash, TextKey.ClashMappedNames,
            "an ambiguous folder raises the mapped-name clash");
        Check.Require(ReferenceEquals(clash.Row, clashRow), "the first-run action retains the supplied row");
        RequireNotice(FirstRun(FolderRow(deletion, Eligible(held: true), extra: new[] { file }), true),
            IssueKind.NameClash, TextKey.ClashMappedNames, "the clash notice wins over a hold fact");
    }

    // A supplied deletion answer that is not a validated folder deletion is a failure, never an empty folder; the
    // mapped destination and the evidence target must agree under the shared path owner.
    private static void FirstRunFailureCases()
    {
        // A differently cased spelling is the same target under the shared path owner.
        RequireNotice(FirstRun(FolderRow(null, Eligible(destination: null)), true),
            IssueKind.Failure, TextKey.FailChanged, "a folder with no mapped destination is a failure");
        RequireNotice(FirstRun(FolderRow(new DeletionEvidence.DeletedPath("b", "b")), true),
            IssueKind.Failure, TextKey.FailChanged, "evidence for a different path cannot finalize this folder");
        RequireNotice(FirstRun(FolderRow(new DeletionEvidence.Unknown("b", TextKey.ReadLocalFailed)), true),
            IssueKind.Failure, TextKey.FailChanged,
            "a mapped-target mismatch pre-empts an evidence variant's own cause");
        RequireDelete(FirstRun(FolderRow(new DeletionEvidence.DeletedPath("A", "A")), true), "a",
            "the mapped target uses the shared case-insensitive path identity");

        // Non-destructive evidence variants never authorize removal, and an established failure or notice is not
        // replaced by the ignored-content keep notice.
        RequireNotice(FirstRun(FolderRow(new DeletionEvidence.Unknown("a", TextKey.ReadLocalFailed)), true),
            IssueKind.Failure, TextKey.ReadLocalFailed, "an unknown deletion answer keeps its own failure cause");
        RequireNotice(FirstRun(FolderRow(new DeletionEvidence.Unknown("a", TextKey.ReadLocalFailed)),
                false, hasIgnoredContents: true),
            IssueKind.Failure, TextKey.ReadLocalFailed,
            "an established failure is not replaced by the ignored-content keep notice");
        RequireNotice(FirstRun(FolderRow(new DeletionEvidence.LiveAtTarget("a", Location("a"))), true),
            IssueKind.Failure, TextKey.FailChanged, "a live exact target is re-observed, not finalized");
        RequireNotice(FirstRun(FolderRow(new DeletionEvidence.LiveElsewhere("a", Location("b"))), true),
            IssueKind.Failure, TextKey.FailChanged, "a live identity elsewhere is a move, not a deletion");
        RequireNotice(FirstRun(FolderRow(new DeletionEvidence.DeletedFile("a", "Dropbox", "id", null,
                T0.AddDays(-1), null, new string('a', 64), null)), true),
            IssueKind.Failure, TextKey.FailChanged, "a deleted-file answer is not folder deletion authority");
        RequireCopy(FirstRun(FolderRow(new DeletionEvidence.NoRetainedHistory("a")), true), "a", null,
            "a file-history negative keeps and creates the folder with no notice");
    }

    // T11-b to T11-e: the outcome table. Kept together because the ordered subrows are one decision table; the
    // shared row builder and assertions would be duplicated by splitting it.
    private static void FirstRunEvidenceCases()
    {
        var deletion = new DeletionEvidence.DeletedPath("a", "a");

        // T11-b: only a physically empty, validated DeletedPath removes the local folder, never a recursive plan.
        RequireDelete(FirstRun(FolderRow(deletion), true), "a",
            "an empty validated DeletedPath folder is removed locally");
        RequireCopy(FirstRun(FolderRow(deletion), false), "a", null,
            "a nonempty kept folder is conditionally created with no notice");
        RequireCopy(FirstRun(FolderRow(deletion), false, hasIgnoredContents: true), "a",
            TextKey.InfoIgnoredFolderKept,
            "deleting a folder that keeps ignored contents raises the D09 notice");
        RequireCopy(FirstRun(FolderRow(deletion), false, hasIgnoredContents: true, ignoredContentsCovered: true),
            "a", null, "saved keep rules covering every retained ignored entry suppress only the D09 notice");

        // T11-c: a validated no-tombstone answer keeps and syncs the folder, with no D09 notice of its own.
        RequireCopy(FirstRun(FolderRow(new DeletionEvidence.NoFolderTombstone("a")), false), "a", null,
            "a successful no-tombstone answer keeps and syncs the folder");
        RequireCopy(FirstRun(FolderRow(new DeletionEvidence.NoFolderTombstone("a")), true), "a", null,
            "a no-tombstone answer keeps even an empty folder through conditional creation");
        RequireCopy(FirstRun(FolderRow(new DeletionEvidence.NoFolderTombstone("a")), false,
                hasIgnoredContents: true), "a", null,
            "a no-tombstone keep raises no deletion notice for protected ignored contents");

        // T11-d and T11-e: ignored contents need history unless covered or independently restored; unavailable
        // history is an honest keep/create that keeps its own notice.
        RequireCopy(FirstRun(FolderRow(new DeletionEvidence.HistoryUnavailable("a", TextKey.ReadDropboxFailed)),
                true), "a", TextKey.InfoDropboxHistoryUnchecked,
            "unavailable folder history keeps the folder with the unchecked-history notice");
        RequireCopy(FirstRun(FolderRow(new DeletionEvidence.HistoryUnavailable("a", TextKey.ReadDropboxFailed)),
                false, hasIgnoredContents: true), "a", TextKey.InfoDropboxHistoryUnchecked,
            "unchecked history is not replaced by the ignored-content keep notice");

        // Null evidence is "no answer yet": an empty folder asks for evidence, and only the stated shortcuts
        // permit the keep/create.
        RequireNeedEvidence(FirstRun(FolderRow(), physicallyEmpty: true), "a",
            "a physically empty folder without history asks for deletion evidence");
        RequireCopy(FirstRun(FolderRow(), physicallyEmpty: false), "a", null,
            "a nonempty folder without history syncs conditionally as new");
        RequireCopy(FirstRun(FolderRow(null, Eligible(destination: "dest")), physicallyEmpty: false), "dest", null,
            "a keep/create names the mapped destination, not the local spelling");
        RequireDelete(FirstRun(FolderRow(new DeletionEvidence.DeletedPath("dest", "dest"),
            Eligible(destination: "dest")), true), "a",
            "the local delete names the folder's own path, not the mapped destination");
        RequireNeedEvidence(FirstRun(FolderRow(), false, hasIgnoredContents: true), "a",
            "ignored contents need history before a keep unless saved rules cover them");
        RequireCopy(FirstRun(FolderRow(), false, hasIgnoredContents: true, ignoredContentsCovered: true), "a",
            null, "saved keep rules covering all ignored contents permit the keep/create");
    }

    // Every supplied fact combination stays away from recursive removal: the only local delete is the validated
    // empty DeletedPath selection, and no DeferFolder is ever planned. A physically empty folder with ignored
    // contents is impossible input guarded by Debug.Assert in the owner, so the sweep never supplies it.
    private static void FirstRunSweep()
    {
        var variants = new DeletionEvidence?[]
        {
            null,
            new DeletionEvidence.DeletedPath("a", "a"),
            new DeletionEvidence.NoFolderTombstone("a"),
            new DeletionEvidence.HistoryUnavailable("a", TextKey.ReadDropboxFailed),
            new DeletionEvidence.NoRetainedHistory("a"),
            new DeletionEvidence.LiveAtTarget("a", Location("a")),
            new DeletionEvidence.LiveElsewhere("a", Location("b")),
            new DeletionEvidence.DeletedFile("a", "Dropbox", "id", null, T0.AddDays(-1), null,
                new string('a', 64), null),
            new DeletionEvidence.Unknown("a", TextKey.ReadLocalFailed)
        };
        foreach (var variant in variants)
        foreach (var physicallyEmpty in new bool?[] { true, false, null })
        foreach (var hasIgnored in new[] { false, true })
        foreach (var covered in new[] { false, true })
        {
            if (physicallyEmpty == true && hasIgnored) continue;
            var action = FirstRun(FolderRow(variant), physicallyEmpty, hasIgnored, covered);
            var emptyDeletion = variant is DeletionEvidence.DeletedPath && physicallyEmpty == true;
            Check.Require(action.Kind != ActionKind.DeferFolder,
                "first-run folder finalization never plans a recursive delete");
            Check.Require(action.Kind is ActionKind.None or ActionKind.Notice
                    or ActionKind.NeedDeletionEvidence or ActionKind.EnsureDropboxFolder or ActionKind.DeleteLocal,
                "first-run finalization returns a first-run decision: " + action.Kind);
            Check.Require((action.Kind == ActionKind.DeleteLocal) == emptyDeletion,
                "only an empty validated DeletedPath folder is removed: " + variant + "/" + physicallyEmpty + "/"
                + hasIgnored);
        }
    }

    private const string ProjectionPath = "Parent";
    private const string ProjectionId = "folder-id";
    private const string ProjectionPair = "pair-a";
    private const string ProjectionRemote = "/Remote";
    private const string ProjectionConnection = "connection";

    private static void ProjectedFirstRunFolders()
    {
        var pair = new PairConfig(ProjectionPair, ProjectionPair,
            Path.Combine(Path.GetTempPath(), nameof(FolderDeletesChecks)), ProjectionRemote, ProjectionConnection,
            [], [], FirstRunPolicy.NewestWins, true, null);
        var scope = new PathScope(pair, [pair]);
        var local = FolderEntry(ProjectionPath, ProjectionId);
        var source = new Row(PathNames.Key(ProjectionPath), local, null, null, [], null, NoContext);
        foreach (var first in new[] { false, true })
        foreach (var retained in new[] { false, true })
        {
            // A matched folder: RowContextsChecks owns the selection cells, this check owns what selection then does.
            var projected = source with { Context = NoContext with
            {
                FirstRun = first, CompleteInventory = true, DeletionEligible = first || retained,
                FirstRunDeletion = retained ? IssueSubject.FirstRunDeletionKind.Item : IssueSubject.FirstRunDeletionKind.None,
                Destination = ProjectionPath
            } };
            var action = Decide.For(projected, FirstRunPolicy.NewestWins, T0, localContentDirty: false);
            Check.Require(projected.Context.FirstRun == first &&
                projected.Context.DeletionEligible == (first || retained) &&
                action.Kind == (first || retained ? ActionKind.DeferFolder : ActionKind.EnsureDropboxFolder),
                nameof(ProjectedFirstRunFolders));
            if (!first && !retained) continue;
            (DeletionEvidence Evidence, bool Empty, bool Ignored, ActionKind Kind, TextKey? Info)[] cases =
            [
                (new DeletionEvidence.DeletedPath(ProjectionPath, ProjectionPath), true, false,
                    ActionKind.DeleteLocal, null),
                (new DeletionEvidence.NoFolderTombstone(ProjectionPath), true, false,
                    ActionKind.EnsureDropboxFolder, null),
                (new DeletionEvidence.DeletedPath(ProjectionPath, ProjectionPath), false, true,
                    ActionKind.EnsureDropboxFolder, TextKey.InfoIgnoredFolderKept),
                (new DeletionEvidence.HistoryUnavailable(ProjectionPath, TextKey.ReadDropboxFailed), true, false,
                    ActionKind.EnsureDropboxFolder, TextKey.InfoDropboxHistoryUnchecked)
            ];
            foreach (var item in cases)
            {
                var result = FirstRun(projected with { Deletion = item.Evidence }, item.Empty,
                    hasIgnoredContents: item.Ignored);
                Check.Require(result.Kind == item.Kind && result.Target == ProjectionPath &&
                    result.InfoAfter == item.Info, nameof(ProjectedFirstRunFolders));
            }
        }
    }

    private static void PolicyCases()
    {
        Check.Require(FolderDeletes.PlanJustified(FolderEntry("a", "lf"), null, false),
            "a present folder with no counterpart is justified without a saved record");
        Check.Require(FolderDeletes.PlanJustified(FolderEntry("a", "lf"), FolderEntry("a", "rf"), true),
            "a present folder with a restored counterpart descendant is justified");
        Check.Require(!FolderDeletes.PlanJustified(FolderEntry("a", "lf"), FolderEntry("a", "rf"), false),
            "a counterpart folder with no restored descendant is not justified");
        Check.Require(!FolderDeletes.PlanJustified(null, null, false), "an absent deleting side is not justified");
        Check.Require(!FolderDeletes.PlanJustified(FileEntry("a", "lf", 1, T0, "h"), null, false),
            "a file on the deleting side is not justified");

        Check.Require(FolderDeletes.DeletingSide(RowOf(FileEntry("a", "l", 1, T0, "h"), null, null))
            == Side.Local, "a present local entry deletes from Local");
        Check.Require(FolderDeletes.DeletingSide(RowOf(null, FileEntry("a", "d", 1, T0, "h"), null))
            == Side.Dropbox, "a missing local entry deletes from Dropbox");
        Check.Require(FolderDeletes.DeletingSide(RowOf(null, null, null)) == Side.Dropbox,
            "a both-absent row deletes from Dropbox");
    }

    // Measured evidence for the migrated per-item paths: each loop runs the owner calls over the same row and requires zero allocation.
    private static void CostProbe()
    {
        var record = Synced("a/file.txt", "a/file.txt", false, "l1", 10, T0, "h1");
        var entry = FileEntry("a/file.txt", "l1", 10, T0, "h1");
        var row = RowOf(entry, null, record);
        _ = FolderDeletes.Classify(row, Side.Local, Listed, localContentDirty: false);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < 10_000; i++)
        {
            _ = FolderDeletes.Classify(row, Side.Local, Listed, localContentDirty: false);
            _ = row.PathOn(Side.Local);
            _ = row.EntryOn(Side.Local);
        }
        var classify = GC.GetTotalAllocatedBytes(precise: true) - before;
        Check.Require(classify == 0, "Classify, EntryOn and PathOn allocate nothing per item: " + classify);

        var unreadable = entry with { Skip = TextKey.SkipUnreadable.ToString() };
        foreach (var side in Enum.GetValues<Side>())
        foreach (var present in new Entry?[] { unreadable, null })
        {
            var protectedRow = Row.ForOneSide(side, record.KeyOn(side), present, record, Array.Empty<Entry>(), null, NoContext);
            var facts = Listed with { InScope = false, Unreadable = present is null };
            _ = FolderDeletes.Classify(protectedRow, side, facts, localContentDirty: false);
            before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < 10_000; i++)
            {
                _ = FolderDeletes.Classify(protectedRow, side, facts, localContentDirty: false);
            }
            var protectedBytes = GC.GetTotalAllocatedBytes(precise: true) - before;
            Check.Require(protectedBytes == 0, "unreadable and saved-only classification allocate nothing: " + protectedBytes);
        }
    }

    private static void ClassifyCases()
    {
        var fileRecord = Synced("a/file.txt", "a/file.txt", false, "l1", 10, T0, "h1");
        var unchanged = FileEntry("a/file.txt", "l1", 10, T0, "h1");
        Check.Require(Verdict(unchanged, null, fileRecord) == FolderVerdict.Removed,
            "an unchanged synced child is Removed");

        var touched = FileEntry("a/file.txt", "l2", 10, T0, "h1");
        Check.Require(Verdict(touched, null, fileRecord, localHash: "h1") == FolderVerdict.Removed,
            "a metadata-only local change is not C");

        var resized = FileEntry("a/file.txt", "l1", 11, T0, "h1");
        Check.Require(Verdict(resized, null, fileRecord) == FolderVerdict.Survives,
            "a local size change is C and Survives");

        var edited = FileEntry("a/file.txt", "l2", 10, T0, "h2");
        Check.Require(Verdict(edited, null, fileRecord, localHash: "h2") == FolderVerdict.Survives,
            "a local content change is C and Survives");
        Check.Require(Verdict(edited, null, fileRecord) == FolderVerdict.Survives,
            "an unresolved local hash protects an edit");

        var flipped = FolderEntry("a/file.txt", "l1");
        Check.Require(Verdict(flipped, null, fileRecord) == FolderVerdict.Survives,
            "a flipped local kind is F and Survives");

        var folderRecord = Synced("a/sub", "a/sub", true, "l2", 0, T0, "");
        var unchangedFolder = FolderEntry("a/sub", "l2");
        Check.Require(Verdict(unchangedFolder, null, folderRecord) == FolderVerdict.Removed,
            "an unchanged saved folder is Removed (folders have no C)");

        var dropboxRecord = Synced("a/d.txt", "a/d.txt", false, "l1", 10, T0, "h1");
        var dropboxSame = FileEntry("a/d.txt", "d1", 10, T0, "h1");
        Check.Require(Verdict(null, dropboxSame, dropboxRecord, Side.Dropbox) == FolderVerdict.Removed,
            "an unchanged Dropbox child is Removed");
        var dropboxEdited = FileEntry("a/d.txt", "d1", 10, T0, "h2");
        Check.Require(Verdict(null, dropboxEdited, dropboxRecord, Side.Dropbox) == FolderVerdict.Survives,
            "a Dropbox content change is C and Survives");
        var dropboxFlipped = FolderEntry("a/d.txt", "d1");
        Check.Require(Verdict(null, dropboxFlipped, dropboxRecord, Side.Dropbox) == FolderVerdict.Survives,
            "a flipped Dropbox kind is F and Survives");

        var skippedSynced = FileEntry("a/skip.txt", "l9", 20, T0, "h9", "skip");
        var skippedRecord = Synced("a/skip.txt", "a/skip.txt", false, "l9", 10, T0, "h9");
        Check.Require(Verdict(skippedSynced, null, skippedRecord) == FolderVerdict.Removed,
            "a skipped child stays Removed even when it looks changed");
        var editedOutOfScope = FileEntry("a/out.txt", "l8", 20, T0, "h8");
        var outOfScopeRecord = Synced("a/out.txt", "a/out.txt", false, "l8", 10, T0, "h1");
        Check.Require(Verdict(editedOutOfScope, null, outOfScopeRecord,
                facts: new FolderChildFacts(true, false, false, false, false)) == FolderVerdict.Removed,
            "an out-of-scope child is Removed before the saved-content check");

        var unsynced = FileEntry("a/new.txt", "l9", 5, T0, "h9");
        Check.Require(Verdict(unsynced, null, null) == FolderVerdict.Removed,
            "an unsynced child present at listing time is Removed");
        Check.Require(Verdict(unsynced, null, null, facts: new FolderChildFacts(false, true, false, false, false)) == FolderVerdict.Survives,
            "an unsynced child absent from the scan arrived later and Survives");
        var skipped = FileEntry("a/new.txt", "l9", 5, T0, "h9", "skip");
        Check.Require(Verdict(skipped, null, null, facts: new FolderChildFacts(false, true, false, false, false)) == FolderVerdict.Removed,
            "a skipped child is never arrived later");
        Check.Require(Verdict(unsynced, null, null, facts: new FolderChildFacts(false, false, false, false, false)) == FolderVerdict.Removed,
            "an out-of-scope child is never arrived later");
        Check.Require(Verdict(unsynced, null, null, facts: new FolderChildFacts(true, true, false, false, false)) == FolderVerdict.Removed,
            "a child in this scan did not arrive later");

        Check.Require(Verdict(unchanged, null, fileRecord, facts: new FolderChildFacts(true, true, true, false, false)) == FolderVerdict.Survives,
            "a nested-pair root Survives");
        Check.Require(Verdict(unchanged, null, fileRecord, facts: new FolderChildFacts(true, true, false, true, false)) == FolderVerdict.Survives,
            "a child restored by the row pass Survives even though it looks unchanged");

        foreach (var side in Enum.GetValues<Side>())
        {
            var newRow = Row.ForOneSide(side, "a/new.txt", unsynced, null, Array.Empty<Entry>(), null, NoContext);
            Check.Require(FolderDeletes.Classify(newRow, side, Listed, localContentDirty: false) == FolderVerdict.Removed,
                "a listed unsaved child is removed on either side: " + side);
            Check.Require(FolderDeletes.Classify(newRow, side, Listed with { InThisScan = false }, localContentDirty: false) == FolderVerdict.Survives,
                "a late unsaved child survives on either side: " + side);
            Check.Require(FolderDeletes.Classify(newRow, side, Listed with { InThisScan = false, InScope = false }, localContentDirty: false) == FolderVerdict.Removed,
                "an ignored late arrival is removed on either side: " + side);
            var skippedRow = Row.ForOneSide(side, "a/new.txt", skipped, null, Array.Empty<Entry>(), null, NoContext);
            Check.Require(FolderDeletes.Classify(skippedRow, side, Listed with { InThisScan = false }, localContentDirty: false) == FolderVerdict.Removed,
                "an ordinary skipped late arrival is removed on either side: " + side);
            var folderRow = Row.ForOneSide(side, "a/sub", unchangedFolder, folderRecord, Array.Empty<Entry>(), null, NoContext);
            Check.Require(FolderDeletes.Classify(folderRow, side, Listed, localContentDirty: false) == FolderVerdict.Removed,
                "unchanged saved folders are removed on either side: " + side);
            var flippedRow = Row.ForOneSide(side, "a/sub", unsynced with { Path = "a/sub" }, folderRecord,
                Array.Empty<Entry>(), null, NoContext);
            Check.Require(FolderDeletes.Classify(flippedRow, side, Listed, localContentDirty: false) == FolderVerdict.Survives,
                "a saved folder replaced by a file survives on either side: " + side);
            var changedRow = Row.ForOneSide(side, "a/file.txt", edited, fileRecord, Array.Empty<Entry>(), "h2", NoContext);
            Check.Require(FolderDeletes.Classify(changedRow, side, Listed with { InScope = false }, localContentDirty: false) == FolderVerdict.Removed,
                "out-of-scope changed content remains removable on either side: " + side);
            var changedSkipped = Row.ForOneSide(side, "a/file.txt", edited with { Skip = "skip" }, fileRecord,
                Array.Empty<Entry>(), "h2", NoContext);
            Check.Require(FolderDeletes.Classify(changedSkipped, side, Listed, localContentDirty: false) == FolderVerdict.Removed,
                "ordinary skipped changed content remains removable on either side: " + side);
        }
    }

    private static void ProtectionCases()
    {
        var record = Synced("a/file.txt", "a/file.txt", false, "l1", 10, T0, "h1");
        var entry = FileEntry("a/file.txt", "l1", 10, T0, "h1");
        var skips = new string?[] { null, "", "skip" }
            .Concat(Enum.GetValues<TextKey>().Select(key => key.ToString())).ToArray();
        foreach (var skip in skips)
        {
            Check.Require(IssuePolicy.ProtectsSubtree(skip) == (skip == TextKey.SkipUnreadable.ToString()),
                "only the persisted unreadable reason protects a subtree: " + skip);
        }
        foreach (var side in Enum.GetValues<Side>())
        foreach (var saved in new SyncedEntry?[] { null, record })
        foreach (var inScope in new[] { false, true })
        foreach (var inScan in new[] { false, true })
        foreach (var skip in skips)
        {
            var present = entry with { Skip = skip };
            var row = Row.ForOneSide(side, "a/file.txt", present, saved, Array.Empty<Entry>(), null, NoContext);
            var facts = new FolderChildFacts(inScan, inScope, false, false, false);
            var expected = skip == TextKey.SkipUnreadable.ToString()
                || (saved is null && inScope && !inScan && skip is null)
                ? FolderVerdict.Survives : FolderVerdict.Removed;
            Check.Require(FolderDeletes.Classify(row, side, facts, localContentDirty: false) == expected,
                "unreadable children and in-scope unskipped unsaved late arrivals survive: " + side + "/" + skip);
            Check.Require(FolderDeletes.Classify(row, side, facts with { Unreadable = true }, localContentDirty: false) == FolderVerdict.Survives,
                "unreadability protects present children before scope, skip and scan facts: " + side + "/" + skip);
            Check.Require(FolderDeletes.Classify(row, side, facts with { HoldsNestedRoot = true }, localContentDirty: false) == FolderVerdict.Survives,
                "nested ownership protects present children before removal rules: " + side + "/" + skip);
            Check.Require(FolderDeletes.Classify(row, side, facts with { Restored = true }, localContentDirty: false) == FolderVerdict.Survives,
                "restoration protects present children before removal rules: " + side + "/" + skip);
        }
        foreach (var side in Enum.GetValues<Side>())
        foreach (var saved in new SyncedEntry?[] { null, record })
        foreach (var inScope in new[] { false, true })
        {
            var row = Row.ForOneSide(side, "a/file.txt", null, saved, Array.Empty<Entry>(), null, NoContext);
            var facts = new FolderChildFacts(false, inScope, false, false, false);
            Check.Require(FolderDeletes.Classify(row, side, facts, localContentDirty: false) == FolderVerdict.Removed,
                "a readable absent child is removed, with or without saved facts: " + side);
            Check.Require(FolderDeletes.Classify(row, side, facts with { Unreadable = true }, localContentDirty: false) == FolderVerdict.Survives,
                "unreadability protects an absent child, including saved-only and out-of-scope rows: " + side);
            Check.Require(FolderDeletes.Classify(row, side, facts with { HoldsNestedRoot = true }, localContentDirty: false) == FolderVerdict.Survives,
                "nested ownership protects an absent child: " + side);
            Check.Require(FolderDeletes.Classify(row, side, facts with { Restored = true }, localContentDirty: false) == FolderVerdict.Survives,
                "restoration protects an absent child: " + side);
        }
    }

    private static void SelectionCases()
    {
        var verdicts = Enum.GetValues<FolderVerdict>();
        var flags = new[] { false, true };
        CheckPolicies();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < 10_000; i++) CheckPolicies();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Check.Require(allocated == 0, "all scalar decision cases allocate nothing per item: " + allocated);

        // Reuse the full truth tables so every policy path is included in the allocation probe.
        [MethodImpl(MethodImplOptions.NoInlining)]
        void CheckPolicies()
        {
            Check.Require(FolderDeletes.DeleteWhole(false), "no survivors permits whole deletion");
            Check.Require(!FolderDeletes.DeleteWhole(true), "any survivor prevents whole deletion");
            foreach (var verdict in verdicts)
            foreach (var protectedBelow in flags)
            foreach (var selectedAncestor in flags)
            {
                var expected = (verdict, protectedBelow, selectedAncestor) == (FolderVerdict.Removed, false, false);
                if (FolderDeletes.SelectBranch(verdict, protectedBelow, selectedAncestor) != expected)
                    Check.Require(false, "branch selection matrix: " + verdict + "/" + protectedBelow + "/" + selectedAncestor);
            }
            foreach (var verdict in verdicts)
            foreach (var present in flags)
            foreach (var restored in flags)
            foreach (var unreadable in flags)
            {
                var expected = (verdict, present, restored, unreadable) == (FolderVerdict.Survives, true, false, false);
                if (FolderDeletes.FollowUp(verdict, present, restored, unreadable) != expected)
                    Check.Require(false, "survivor follow-up matrix: " + verdict + "/" + present + "/" + restored + "/" + unreadable);
            }
        }
    }

    private static FolderVerdict Verdict(Entry? local, Entry? dropbox, SyncedEntry? synced, Side side = Side.Local,
        string? localHash = null, FolderChildFacts? facts = null) =>
        FolderDeletes.Classify(RowOf(local, dropbox, synced, localHash), side, facts ?? Listed, localContentDirty: false);

    private static Row RowOf(Entry? local, Entry? dropbox, SyncedEntry? synced, string? localHash = null) =>
        new("", local, dropbox, synced, Array.Empty<Entry>(), localHash, NoContext);

    private static RowContext Eligible(string? destination = "a", bool outOfScope = false, bool skipped = false,
        bool held = false, bool holdsNestedRoot = false, bool completeInventory = true,
        bool deletionEligible = true, IssueKind? heldKind = null) =>
        new(outOfScope, skipped, held, null, holdsNestedRoot, true, completeInventory, deletionEligible,
            destination, heldKind);

    private static Row FolderRow(DeletionEvidence? deletion = null, RowContext? context = null,
        Entry? local = null, Entry? dropbox = null, SyncedEntry? synced = null,
        IReadOnlyList<Entry>? extra = null) =>
        new("a", local ?? FolderEntry("a", "lf"), dropbox, synced, extra ?? Array.Empty<Entry>(), null,
            context ?? Eligible(), deletion);

    private static SyncAction FirstRun(Row row, bool? physicallyEmpty,
        bool hasIgnoredContents = false, bool ignoredContentsCovered = false) =>
        FolderDeletes.FirstRun(row, physicallyEmpty, hasIgnoredContents, ignoredContentsCovered);

    private static IdentityLocation Location(string path) => new("Dropbox", path, FolderEntry(path, "id"));

    private static void RequireNone(SyncAction action, NoneReason reason, string what) =>
        Check.Require(action.Kind == ActionKind.None && action.None == reason,
            what + " (got " + action.Kind + "/" + action.None + ")");

    private static void RequireNotice(SyncAction action, IssueKind kind, TextKey cause, string what) =>
        Check.Require(action.Kind == ActionKind.Notice && action.NoticeKind == kind && action.Cause == cause,
            what + " (got " + action.Kind + "/" + action.NoticeKind + "/" + action.Cause + ")");

    private static void RequireNeedEvidence(SyncAction action, string target, string what) =>
        Check.Require(action.Kind == ActionKind.NeedDeletionEvidence && action.Target == target,
            what + " (got " + action.Kind + "/" + action.Target + ")");

    private static void RequireCopy(SyncAction action, string target, TextKey? infoAfter, string what) =>
        Check.Require(action.Kind == ActionKind.EnsureDropboxFolder && action.Target == target
            && action.InfoAfter == infoAfter,
            what + " (got " + action.Kind + "/" + action.Target + "/" + action.InfoAfter + ")");

    private static void RequireDelete(SyncAction action, string target, string what) =>
        Check.Require(action.Kind == ActionKind.DeleteLocal && action.Target == target,
            what + " (got " + action.Kind + "/" + action.Target + ")");

    private static Entry FileEntry(string path, string id, long size, DateTimeOffset modified, string hash,
        string? skip = null) =>
        new(path, false, size, modified, id, "", hash, skip);

    private static Entry FolderEntry(string path, string id) =>
        new(path, true, 0, T0, id, "", "", null);

    private static SyncedEntry Synced(string localPath, string dropboxPath, bool isFolder, string localId, long size,
        DateTimeOffset modified, string dropboxContentHash) =>
        new(PathNames.Key(localPath), localPath, PathNames.DropboxKey(dropboxPath), dropboxPath, isFolder, localId,
            size, modified.UtcTicks, localId + "d", dropboxContentHash);
}
