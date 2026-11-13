using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks for Decide (`--engine-checks decide`). The plan keeps every
/// Decide oracle in this one physical file, so the decision tables stay together rather than split per section.</summary>
public static class DecideChecks
{
    // The Executor samples the current comparison time and supplies it to Decide (P6 phase 1).
    private static readonly DateTimeOffset ComparedAt = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Before = ComparedAt.AddDays(-2);
    private static readonly FirstRunPolicy Newest = FirstRunPolicy.NewestWins;
    // Dropbox content hashes are 64 hexadecimal characters; these values only need to be valid and distinct.
    private const string ContentA = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string ContentB = "0000000000000000000000000000000000000000000000000000000000000002";

    internal static bool BoolSink;
    internal static bool? NullableBoolSink;
    internal static SyncAction? ActionSink;

    public static void Run()
    {
        BatchAdmission();
        Steps();
        HeldFailures();
        HeldFolderIdentity();
        NoRecordTable();
        AncestorSpelling();
        WithRecordTable();
        ReturningOriginals();
        FolderDeleteDirtyChild();
        NeedLocalHash();
        OldAppPatternOne();
        OldAppPatternTwo();
        SharedClassifier();
        SavedStamp();
        FreeIncoming();
        Measured();
        NamePolicy();
        UnreliableTime();
        FirstRunDeletion();
        RetainedFolderDecisions();
    }

    private static void AncestorSpelling()
    {
        foreach (var policy in Enum.GetValues<FirstRunPolicy>())
        foreach (var firstRun in new[] { false, true })
        {
            var local = Folder("Parent/Child");
            var remote = Folder("parent/Child") with { Id = "remote" };
            var row = MakeRow(local, remote, context: Ctx(firstRun: firstRun));
            Check.Require(Decide.For(row, policy, ComparedAt, false).Kind == ActionKind.Record,
                "Ancestor-only casing records the pair without an ineffective leaf move.");
            var saved = new SyncedEntry(PathNames.Key(local.Path), local.Path, PathNames.DropboxKey(remote.Path),
                remote.Path, true, local.Id, local.Size, local.Modified.UtcTicks, remote.Id, remote.ContentHash);
            Check.Require(Decide.For(row with { Synced = saved }, policy, ComparedAt, false).IsInSync(),
                "After one record pass, identical observations settle without repeating a move.");
        }
    }

    private static void ReturningOriginals()
    {
        foreach (var far in new[] { Side.Local, Side.Dropbox })
        foreach (var (nearHash, farHash, expected) in new[]
        {
            (ContentA, ContentB, far == Side.Local ? ActionKind.Upload : ActionKind.Download),
            (ContentA, ContentA, ActionKind.Record),
            (ContentB, ContentA, far == Side.Local ? ActionKind.Download : ActionKind.Upload),
            (ContentB, ContentB, ActionKind.Record),
            (ContentB, "different", ActionKind.KeepBoth)
        })
        {
            var local = new Entry("Original", false, 4, Before, "near-local", "", far == Side.Local ? farHash : nearHash, null);
            var remote = local with { Id = "near-remote", ContentHash = far == Side.Dropbox ? farHash : nearHash };
            var saved = new SyncedEntry(PathNames.Key("Original"), "Original", PathNames.DropboxKey("Original"),
                "Original", false, local.Id, 4, Before.UtcTicks, remote.Id, ContentA);
            saved = far == Side.Local
                ? saved with { LocalKey = PathNames.Key("Partner"), LocalPath = "Partner", LocalId = "old-local" }
                : saved with { DropboxKey = PathNames.DropboxKey("Partner"), DropboxPath = "Partner", DropboxId = "old-remote" };
            var row = MakeRow(local, remote, saved, localHash: local.ContentHash);
            Check.Require(Decide.CrossedFar(local, remote, saved) == far, "Returning original has one crossed near endpoint.");
            var action = Decide.For(row, Newest, ComparedAt, localContentDirty: local.ContentHash != ContentA);
            Check.Require(action.Kind == expected && ReferenceEquals(action.Row, row) &&
                action.InfoAfter == (expected == ActionKind.KeepBoth ? TextKey.InfoKeptBoth : (TextKey?)null),
                "All returning-original content cases reuse Table B and preserve the actual binding and notice policy.");
            Check.Require(Decide.CrossedFar(null, remote, saved) is null &&
                Decide.CrossedFar(local, null, saved) is null &&
                Decide.CrossedFar(local with { IsFolder = true }, remote, saved) is null &&
                Decide.CrossedFar(local, remote with { Skip = "skip" }, saved) is null &&
                Decide.CrossedFar(local, remote with { Path = "Unrelated" }, saved) is null &&
                Decide.CrossedFar(local, remote, saved with { IsFolder = true }) is null &&
                Decide.CrossedFar(local with { Id = saved.LocalId }, remote with { Id = saved.DropboxId }, saved) is null,
                "Missing, skipped, unrelated, folder and same-identity relocation inputs do not qualify.");
        }
    }

    private static void BatchAdmission()
    {
        var local = File("Parent/Child");
        var remote = File("Parent/Child", id: "remote");
        var saved = SavedFile("Parent/Child");
        foreach (var kind in Enum.GetValues<ActionKind>())
        {
            var ordinary = MakeRow(local, remote, saved);
            var action = new SyncAction(kind, ordinary);
            var forbidden = kind is ActionKind.ReplaceKind or ActionKind.MoveLocal or ActionKind.MoveDropbox or
                ActionKind.MoveUnlinked or ActionKind.DeleteLocal or ActionKind.DeleteDropbox or ActionKind.ForgetOnly;
            var admitted = Decide.AdmitInBatch(action, false, null, true);
            Check.Require(forbidden ? admitted.None == NoneReason.OutOfScope : admitted == action,
                "batch gates every destructive action kind, retaining ordinary actions");
            foreach (var missing in new[] { MakeRow(local, null, saved), MakeRow(null, remote, saved) })
                Check.Require(Decide.AdmitInBatch(action with { Row = missing }, false, null, true).None == NoneReason.OutOfScope,
                    "saved missing-side rows cannot enter any batch action or folder plan");
        }
        var first = MakeRow(local, null, context: Ctx(firstRun: true, completeInventory: true, deletionEligible: true));
        var deletion = new SyncAction(ActionKind.DeleteLocal, first);
        Check.Require(Decide.AdmitInBatch(deletion, false, null, true) == deletion &&
            Decide.AdmitInBatch(deletion, false, null, false).None == NoneReason.OutOfScope &&
            Decide.AdmitInBatch(deletion with { Row = first with { Context = first.Context with { CompleteInventory = false } } },
                false, null, true).None == NoneReason.OutOfScope,
            "only Retry with complete first-run evidence admits checked Local deletion");
        var create = new SyncAction(ActionKind.Upload, MakeRow(local, null));
        Check.Require(Decide.AdmitInBatch(create, true, null, true).None == NoneReason.OutOfScope &&
            Decide.AdmitInBatch(create, false, Side.Dropbox, true).None == NoneReason.OutOfScope,
            "bound-elsewhere and incomplete missing-side rows stay unfinished");
        var preserve = new SyncAction(ActionKind.DeferFolder, MakeRow(Folder("Parent"), null,
            context: Ctx(firstRun: true) with { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice }));
        Check.Require(Decide.AdmitInBatch(preserve, false, null, false) == preserve,
            "legitimate preserved-folder deferral permits children");
    }

    // Measured per-site evidence for the per-item predicates and tables this change introduced or rewired.
    private static void Measured()
    {
        var saved = SavedFile("a.txt", 4);
        Entry? absent = null;
        SyncedEntry? noBinding = null;
        // Both entries keep the saved size so LocalChanged reaches the stamp comparison; without a hash it is unknown.
        var idDiff = File("a.txt", 4, id: "other");
        var ticksOnly = File("a.txt", 4, Before.AddHours(1));
        Check.Require(Decide.LocalChanged(idDiff, saved, null, localContentDirty: false) is null,
            "stamp probe: an id-only change is unknown without the hash");
        Check.Require(Decide.LocalChanged(ticksOnly, saved, null, localContentDirty: false) is null,
            "stamp probe: a ticks-only change is unknown without the hash");

        var freeOwner = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                Sink(Decide.IsFreeIncoming(idDiff, absent, saved, noBinding));
                Sink(Decide.IsFreeIncoming(absent, idDiff, noBinding, saved));
            }
        });
        var freeInline = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                Sink((saved is not null || noBinding is not null) &&
                    (idDiff is not null && absent is null && idDiff.Id != saved?.LocalId ||
                     absent is not null && idDiff is null && absent.Id != noBinding?.DropboxId));
                Sink((noBinding is not null || saved is not null) &&
                    (absent is not null && idDiff is null && absent.Id != noBinding?.LocalId ||
                     idDiff is not null && absent is null && idDiff.Id != saved?.DropboxId));
            }
        });
        Check.Require(freeOwner == freeInline,
            $"Decide.IsFreeIncoming allocates no more than its inline expression ({freeOwner} B vs {freeInline} B).");

        var changedOwner = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                Sink(Decide.LocalChanged(idDiff, saved, null, localContentDirty: false));
                Sink(Decide.LocalChanged(ticksOnly, saved, null, localContentDirty: false));
            }
        });
        var changedInline = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                Sink(InlineChanged(idDiff, saved));
                Sink(InlineChanged(ticksOnly, saved));
            }
        });
        Check.Require(changedOwner == changedInline,
            $"Decide.LocalChanged and its stamp helper allocate no more than their inline expression " +
            $"({changedOwner} B vs {changedInline} B).");

        var unchanged = MakeRow(File("a.txt", 4), File("a.txt", 4), saved);
        var refresh = MakeRow(ticksOnly, File("a.txt", 4), saved, localHash: "h");
        Check.Require(Decide.For(unchanged, Newest, ComparedAt, localContentDirty: false).IsInSync(),
            "cost row: an unchanged row settles in sync");
        Check.Require(Decide.For(refresh, Newest, ComparedAt, localContentDirty: false).Kind == ActionKind.Record,
            "cost row: a ticks-only metadata change refreshes through UnchangedBoth");

        var unchangedOwner = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(Decide.For(unchanged, Newest, ComparedAt, localContentDirty: false));
        });
        var unchangedInline = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(new SyncAction(ActionKind.None, unchanged, NoneReason.InSync));
        });
        Check.Require(unchangedOwner == unchangedInline,
            $"Decide.For on an unchanged row allocates only its action ({unchangedOwner} B vs {unchangedInline} B).");

        var refreshOwner = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(Decide.For(refresh, Newest, ComparedAt, localContentDirty: false));
        });
        var refreshInline = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(new SyncAction(ActionKind.Record, refresh));
        });
        Check.Require(refreshOwner == refreshInline,
            $"Decide.For on a metadata refresh allocates only its action ({refreshOwner} B vs {refreshInline} B).");
    }

    private static bool? InlineChanged(Entry entry, SyncedEntry synced) =>
        entry.IsFolder || synced.IsFolder ? false
        : entry.Size != synced.LocalSize ? true
        : entry.Id != synced.LocalId || entry.Modified.UtcTicks != synced.LocalModifiedTicks ? null
        : false;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(bool value) => BoolSink = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(bool? value) => NullableBoolSink = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(SyncAction? value) => ActionSink = value;

    // The refresh compares the exact saved local id and modified ticks; a ticks-only change still refreshes.
    private static void SavedStamp()
    {
        var saved = SavedFile("a.txt", 4);
        Require(For(File("a.txt", 4, Before.AddHours(1)), File("a.txt", 4), saved, localHash: "h"),
            ActionKind.Record, "B U/U ticks-only local refresh");
    }

    // The free-incoming identity compares one observed side's id with a binding that belongs to another pair.
    private static void FreeIncoming()
    {
        var binding = SavedFile("a.txt", 4);
        Check.Require(Decide.IsFreeIncoming(File("a.txt", id: "other"), null, binding, null),
            "free incoming: another local id under a foreign binding is a free create");
        Check.Require(!Decide.IsFreeIncoming(File("a.txt"), null, binding, null),
            "free incoming: the binding's own local id is not free");
        Check.Require(Decide.IsFreeIncoming(null, File("a.txt", id: "other"), null, binding),
            "free incoming: another Dropbox id under a foreign binding is a free create");
        Check.Require(!Decide.IsFreeIncoming(null, File("a.txt"), null, binding),
            "free incoming: the binding's own Dropbox id is not free");
        Check.Require(!Decide.IsFreeIncoming(File("a.txt", id: "other"), File("a.txt", id: "other"), binding, binding),
            "free incoming: both sides observed is not a free create");
        Check.Require(!Decide.IsFreeIncoming(File("a.txt", id: "other"), null, null, null),
            "free incoming: no binding on either side is not a free create");
    }

    // Old-app pattern 1 (26 false copies): a same-id, same-size local file with a moved time is a metadata refresh;
    // once its content matches the record, the Dropbox edit is downloaded instead of keeping both.
    private static void OldAppPatternOne()
    {
        var saved = SavedFile("sheet.xlsx", 8);
        foreach (var delta in new[] { TimeSpan.FromMilliseconds(300), TimeSpan.FromHours(3) })
        {
            var local = File("sheet.xlsx", 8, Before.Add(delta));
            var dropbox = File("sheet.xlsx", 8, hash: "x");
            var first = For(local, dropbox, saved);
            Check.Require(first.Kind == ActionKind.NeedLocalHash,
                "Old-app pattern 1: a time-only local change asks for the content hash first");
            var decided = For(local, dropbox, saved, localHash: "h");
            Check.Require(decided.Kind == ActionKind.Download && decided.Target == "sheet.xlsx",
                "Old-app pattern 1: the record's content downloads the Dropbox edit, never KeepBoth");
        }
    }

    // Old-app pattern 2 (2 false copies): a local file whose size and content differ against an unchanged Dropbox
    // copy is an ordinary upload, never a keep-both.
    private static void OldAppPatternTwo()
    {
        var saved = SavedFile("ledger.json", 4);
        var upload = For(File("ledger.json", 9, hash: "y", id: "j"), File("ledger.json", 4, hash: "h"), saved,
            localHash: "y");
        Check.Require(upload.Kind == ActionKind.Upload && upload.Target == "ledger.json",
            "Old-app pattern 2: a changed local file against an unchanged Dropbox copy uploads, never KeepBoth");
    }

    private static void SharedClassifier()
    {
        var record = SavedFile("a.txt", 4, hash: "h");
        Check.Require(Decide.LocalChanged(File("a.txt", 4), record, null, localContentDirty: false) == false,
            "classifier: equal metadata at equal size is unchanged even without a hash");
        Check.Require(Decide.LocalChanged(File("a.txt", 4, id: "j"), record, null, localContentDirty: false) is null,
            "classifier: a metadata change at equal size with no hash is unknown");
        Check.Require(Decide.LocalChanged(File("a.txt", 4, id: "j"), record, "h", localContentDirty: false) == false,
            "classifier: a metadata change holding the saved content is unchanged");
        Check.Require(Decide.LocalChanged(File("a.txt", 4, id: "j"), record, "x", localContentDirty: false) == true,
            "classifier: a metadata change with other content is changed");
        Check.Require(Decide.LocalChanged(File("a.txt", 5), record, "h", localContentDirty: false) == true,
            "classifier: a size change is changed whatever the hash");
        Check.Require(Decide.LocalChanged(Folder("a.txt"), record, "h", localContentDirty: false) == false,
            "classifier: a local folder never changes");
        Check.Require(!Decide.DropboxChanged(File("a.txt", 4, hash: "h"), record),
            "classifier: an equal Dropbox content hash is unchanged");
        Check.Require(Decide.DropboxChanged(File("a.txt", 4, hash: "x"), record),
            "classifier: a different Dropbox content hash is changed");
        Check.Require(!Decide.DropboxChanged(Folder("a.txt"), SavedFolder("a.txt")),
            "classifier: a Dropbox folder never changes");
        Check.Require(Decide.Changed(Side.Local, File("a.txt", 4, id: "j"), record, null, localContentDirty: false),
            "shared changed: unknown local content stays protected");
        Check.Require(!Decide.Changed(Side.Local, File("a.txt", 4), record, null, localContentDirty: false)
            && !Decide.Changed(Side.Local, File("a.txt", 4, id: "j"), record, "h", localContentDirty: false),
            "shared changed: unchanged local metadata or verified content stays unchanged");
        Check.Require(Decide.Changed(Side.Local, File("a.txt", 5), record, "h", localContentDirty: false)
            && Decide.Changed(Side.Local, File("a.txt", 4, id: "j"), record, "x", localContentDirty: false),
            "shared changed: local size or content changes stay changed");
        Check.Require(!Decide.Changed(Side.Dropbox, File("a.txt", 4), record, "x", localContentDirty: false)
            && Decide.Changed(Side.Dropbox, File("a.txt", 4, hash: "x"), record, "h", localContentDirty: false),
            "shared changed: Dropbox uses its own hash, not the supplied local hash");
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            Check.Require(Decide.Changed(side, Folder("a.txt"), record, null, localContentDirty: false)
                && Decide.Changed(side, File("a.txt"), SavedFolder("a.txt"), null, localContentDirty: false),
                "shared changed: either kind flip stays protected on " + side);
            Check.Require(!Decide.Changed(side, Folder("a.txt"), SavedFolder("a.txt"), null, localContentDirty: false),
                "shared changed: unchanged folders stay unchanged on " + side);
        }
    }

    private static void HeldFolderIdentity()
    {
        foreach (var replaceLocal in new[] { false, true })
        foreach (var replaceDropbox in new[] { false, true })
        foreach (var hold in new IssueKind?[] { null, IssueKind.Tie, IssueKind.NameClash })
        {
            var local = Folder("Parent") with { Id = replaceLocal ? "recreated-local" : "i" };
            var dropbox = Folder("Parent") with { Id = replaceDropbox ? "recreated-dropbox" : "i" };
            var action = For(local, dropbox, SavedFolder("Parent"), Ctx(held: hold is not null, heldKind: hold));
            var expected = hold is not null && (replaceLocal || replaceDropbox) ? NoneReason.Held : NoneReason.InSync;
            Check.Require(action.Kind == ActionKind.None && action.None == expected,
                "HeldFolderIdentity/" + replaceLocal + "/" + replaceDropbox + "/" + hold +
                ": held folder identity must match; non-held delete/recreate keeps the existing InSync decision.");
        }
    }

    private static void Steps()
    {
        Require(For(File("a.txt"), null, context: Ctx(outOfScope: true)), ActionKind.None, "step 1 out of scope");
        Check.Require(For(File("a.txt"), null, context: Ctx(outOfScope: true)).None == NoneReason.OutOfScope,
            "step 1 out-of-scope reason");
        Check.Require(For(File("a.txt"), null, context: Ctx(skipped: true)).None == NoneReason.Skipped,
            "step 1 skipped reason");

        var ambiguous = For(File("A.txt"), File("a.txt"), context: Ctx(held: true),
            extra: new[] { File("a.txt") });
        Check.Require(ambiguous.Kind == ActionKind.Notice && ambiguous.NoticeKind == IssueKind.NameClash
            && ambiguous.Cause == TextKey.ClashMappedNames, "step 2 a shared-key ambiguity before held");

        var saved = SavedFile("a.txt", 4);
        var inSync = For(File("a.txt", 4), File("a.txt", 4), saved, Ctx(held: true));
        Check.Require(inSync.Kind == ActionKind.None && inSync.None == NoneReason.InSync && inSync.Row.Context.Held,
            "step 3 held re-evaluates to InSync and keeps the held row");
        var blocked = For(File("a.txt", 5), null, saved, Ctx(held: true));
        Check.Require(blocked.Kind == ActionKind.None && blocked.None == NoneReason.Held, "step 3 held blocks an effect");
        var refreshed = For(File("a", 1, Before), File("a", 2, Before.AddMilliseconds(500)), null,
            Ctx(held: true, heldKind: IssueKind.Tie));
        Check.Require(refreshed.Kind == ActionKind.Notice && refreshed.NoticeKind == IssueKind.Tie
            && refreshed.Cause == TextKey.TieEqualTimes && refreshed.Row.Context.Held,
            "step 3 held re-raises a notice of the same kind with the current row");
        var clashRefreshed = For(File("a"), Folder("a"), context: Ctx(held: true, heldKind: IssueKind.NameClash));
        Check.Require(clashRefreshed.Kind == ActionKind.Notice && clashRefreshed.NoticeKind == IssueKind.NameClash
            && clashRefreshed.Cause == TextKey.ClashFileFolder && clashRefreshed.Row.Context.Held,
            "step 3 held re-raises a same-kind NameClash");
        var escalated = For(File("a"), Folder("a"), context: Ctx(held: true, heldKind: IssueKind.Tie));
        Check.Require(escalated.Kind == ActionKind.None && escalated.None == NoneReason.Held,
            "step 3 a Tie re-evaluating to a NameClash stays held and raises nothing");
        var hashThroughHold = For(File("a.txt", 4, id: "j"), File("a.txt", 4), saved, Ctx(held: true));
        Check.Require(hashThroughHold.Kind == ActionKind.NeedLocalHash, "step 3 held passes NeedLocalHash through");
        var heldFirstRun = For(File("a.txt", 5, Before.AddHours(3)), File("a.txt", 5, Before), localHash: "h",
            context: Ctx(firstRun: true, held: true));
        Check.Require(heldFirstRun.Kind == ActionKind.Record && heldFirstRun.Row.Context.Held,
            "step 3 held first-run equal content is recorded and keeps the held row");

        var survive = For(File("f/a.txt"), File("f/a.txt", 4, hash: "x"), SavedFile("f/a.txt"),
            Ctx(underFolderDelete: Side.Dropbox));
        Check.Require(survive.Kind == ActionKind.Download, "step 4 survivor continues to table B");
        var removed = For(File("f/a.txt"), File("f/a.txt"), SavedFile("f/a.txt"),
            Ctx(underFolderDelete: Side.Dropbox));
        Check.Require(removed.Kind == ActionKind.None && removed.None == NoneReason.RemovedWithFolder,
            "step 4 removed child");
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var heldRemoved = For(File("f/a.txt"), File("f/a.txt"), SavedFile("f/a.txt"),
                Ctx(held: true, underFolderDelete: side, heldKind: IssueKind.Tie));
            Check.Require(heldRemoved.Kind == ActionKind.None && heldRemoved.None == NoneReason.Held
                && heldRemoved.Row.Context.Held && heldRemoved.Row.Context.UnderFolderDelete == side,
                "step 3 held blocks the folder-removed outcome on " + side);
            var skippedChild = For(File("f/a.txt"), File("f/a.txt"), SavedFile("f/a.txt"),
                Ctx(skipped: true, underFolderDelete: side));
            Check.Require(skippedChild.Kind == ActionKind.None && skippedChild.None == NoneReason.Skipped,
                "step 1 skipped children never reach folder-delete classification on " + side);
            var nested = For(Folder("f/nested"), Folder("f/nested"), SavedFolder("f/nested"),
                Ctx(holdsNestedRoot: true, underFolderDelete: side));
            Check.Require(nested.IsInSync(), "step 4 nested-root facts protect the child on " + side);
        }
    }

    // 6.5: a saved same-stamp child under a folder delete is Changed only when acquired dirty evidence says so,
    // so adding that evidence must change the outcome: hash first, then continue by the ordinary table-B rules.
    private static void FolderDeleteDirtyChild()
    {
        var saved = SavedFile("f/a.txt", 4);
        var row = MakeRow(File("f/a.txt", 4), null, saved, Ctx(underFolderDelete: Side.Local));
        var unknown = Decide.For(row, Newest, ComparedAt, localContentDirty: true);
        Check.Require(unknown.Kind == ActionKind.NeedLocalHash,
            "a dirty same-stamp folder-delete child requests the hash instead of removal");
        var equal = Decide.For(row with { LocalHash = "h" }, Newest, ComparedAt, localContentDirty: true);
        Check.Require(equal.Kind == ActionKind.None && equal.None == NoneReason.RemovedWithFolder,
            "equal content under dirty evidence still removes with the folder");
        var differing = Decide.For(row with { LocalHash = "x" }, Newest, ComparedAt, localContentDirty: true);
        Check.Require(differing.Kind == ActionKind.Upload && differing.Target == "f/a.txt",
            "differing content under dirty evidence survives the folder and uploads");
        var clean = Decide.For(row with { LocalHash = "x" }, Newest, ComparedAt, localContentDirty: false);
        Check.Require(clean.Kind == ActionKind.None && clean.None == NoneReason.RemovedWithFolder,
            "the same differing hash without dirty evidence removes with the clean-stamp folder");
    }

    private static void HeldFailures()
    {
        var saved = SavedFile("a.txt", 4);
        var rows = new[]
        {
            MakeRow(File("a.txt", 4, id: "j"), File("a.txt", 4), saved),
            MakeRow(File("a.txt", 4, Before.AddHours(1)), File("a.txt", 4), saved),
            MakeRow(File("a.txt", 4, id: "j"), File("a.txt", 4, hash: "x"), saved),
            MakeRow(File("a.txt", 5), File("a.txt", 5, hash: "x"), saved),
            MakeRow(File("a.txt", 4, id: "j"), null, saved),
            MakeRow(File("a.txt"), File("a.txt")),
            MakeRow(File("a_b.txt"), File("A_B.txt")),
            MakeRow(File("a.txt", modified: Before.AddHours(1)), File("a.txt"),
                context: Ctx(firstRun: true)),
            MakeRow(File("a.txt"), File("a.txt"), SavedFolder("a.txt")),
            MakeRow(File("a.txt", 4, id: "j"), File("a.txt", 4), saved,
                Ctx(underFolderDelete: Side.Local)),
        };
        foreach (var row in rows)
        {
            Require(Decide.For(row, Newest, ComparedAt, localContentDirty: false), ActionKind.NeedLocalHash,
                "Failure hold control: without a hold this row requires a hash");
            foreach (var kind in new[] { IssueKind.Failure, IssueKind.Tie, IssueKind.NameClash })
            {
                var held = row with { Context = row.Context with { Held = true, HeldKind = kind } };
                var action = Decide.For(held, Newest, ComparedAt, localContentDirty: false);
                Check.Require(ReferenceEquals(action.Row, held), "held decision retains its original row");
                if (kind == IssueKind.Failure)
                {
                    Check.Require(action.Kind == ActionKind.None && action.None == NoneReason.Held,
                        "definite Failure holds before requesting a local hash");
                }
                else
                {
                    Require(action, ActionKind.NeedLocalHash,
                        "Tie and NameClash still request the hash needed for re-evaluation");
                }
            }
        }

        var failure = Ctx(held: true, heldKind: IssueKind.Failure);
        var inSync = For(File("a.txt", 4), File("a.txt", 4), saved, failure);
        Check.Require(inSync.Kind == ActionKind.None && inSync.None == NoneReason.Held,
            "definite Failure holds the whole row even when its content appears in sync");
        foreach (var context in new[] { failure with { OutOfScope = true }, failure with { Skipped = true } })
        {
            var action = For(File("a.txt", 4, id: "j"), File("a.txt", 4), saved, context);
            Check.Require(action.Kind == ActionKind.None && action.None ==
                (context.OutOfScope ? NoneReason.OutOfScope : NoneReason.Skipped),
                "scope and skip decisions precede the Failure hold");
        }
        var ambiguous = For(File("A.txt"), File("a.txt"), context: failure, extra: new[] { File("A.txt") });
        Check.Require(ambiguous.Kind == ActionKind.Notice && ambiguous.NoticeKind == IssueKind.NameClash
            && ambiguous.Cause == TextKey.ClashMappedNames, "shared-key ambiguity still precedes the Failure hold");
    }

    private static void NoRecordTable()
    {
        var both = For(null, null);
        Check.Require(both.Kind == ActionKind.None && both.None == NoneReason.InSync, "A: both absent");

        var upload = For(File("sub/a.txt"), null, context: Ctx(destination: "sub/a.txt"));
        Check.Require(upload.Kind == ActionKind.Upload && upload.Target == "sub/a.txt", "A: local file upload");
        var ensureDropbox = For(Folder("sub"), null, context: Ctx(destination: "sub"));
        Check.Require(ensureDropbox.Kind == ActionKind.EnsureDropboxFolder && ensureDropbox.Target == "sub",
            "A: local folder ensure");
        var download = For(null, File("sub/a.txt"), context: Ctx(destination: "sub/a.txt"));
        Check.Require(download.Kind == ActionKind.Download && download.Target == "sub/a.txt", "A: dropbox file download");
        var ensureLocal = For(null, Folder("sub"), context: Ctx(destination: "sub"));
        Check.Require(ensureLocal.Kind == ActionKind.EnsureLocalFolder && ensureLocal.Target == "sub",
            "A: dropbox folder ensure");

        var clash = For(File("a"), Folder("a"));
        Check.Require(clash.Kind == ActionKind.Notice && clash.NoticeKind == IssueKind.NameClash
            && clash.Cause == TextKey.ClashFileFolder, "A: file vs folder");
        var clashReverse = For(Folder("a"), File("a"));
        Check.Require(clashReverse.Kind == ActionKind.Notice && clashReverse.Cause == TextKey.ClashFileFolder,
            "A: folder vs file");

        Require(For(Folder("docs"), Folder("docs")), ActionKind.Record, "A: folders same spelling");

        var dropboxSpelling = ForPolicy(Folder("docs"), Folder("Docs"), Ctx(firstRun: true), FirstRunPolicy.DropboxWins);
        Check.Require(dropboxSpelling.Kind == ActionKind.MoveLocal && dropboxSpelling.Target == "Docs"
            && dropboxSpelling.InfoAfter == TextKey.InfoFolderSpelling, "A: folder case DropboxWins");
        var localSpelling = ForPolicy(Folder("docs"), Folder("Docs"), Ctx(firstRun: true), FirstRunPolicy.LocalWins);
        Check.Require(localSpelling.Kind == ActionKind.MoveDropbox && localSpelling.Target == "docs"
            && localSpelling.InfoAfter == TextKey.InfoFolderSpelling, "A: folder case LocalWins");

        Require(For(File("a", 5, Before), File("a", 5, Before.AddMilliseconds(400), hash: "z"),
            context: Ctx(firstRun: true)), ActionKind.Record,
            "A: first run same size and reliable same second");
        Require(For(File("a", 5, Before), File("a", 5, Before.AddHours(3)), localHash: "h"),
            ActionKind.Record, "A: later run equal content");

        // The first run compares equal-size content before the first-run policy (D13 as amended); an equal size
        // and the same modified second are recorded without that comparison. Every first-run policy takes the
        // content comparison; only a content difference follows the policy.
        var firstRunPolicies = new[]
        {
            (Policy: FirstRunPolicy.NewestWins, Local: Before.AddHours(3), Kind: ActionKind.Upload),
            (Policy: FirstRunPolicy.NewestWins, Local: Before.AddHours(-3), Kind: ActionKind.Download),
            (Policy: FirstRunPolicy.LocalWins, Local: Before.AddHours(-3), Kind: ActionKind.Upload),
            (Policy: FirstRunPolicy.DropboxWins, Local: Before.AddHours(3), Kind: ActionKind.Download),
        };
        foreach (var (policy, localModified, kind) in firstRunPolicies)
        {
            var local = File("a.txt", 5, localModified);
            var dropbox = File("a.txt", 5, Before);
            var label = "A: first run " + policy;
            Check.Require(ForPolicy(local, dropbox, Ctx(firstRun: true), policy).Kind == ActionKind.NeedLocalHash,
                label + ": equal size needs the content hash");
            Require(ForPolicy(local, dropbox, Ctx(firstRun: true), policy, localHash: "h"), ActionKind.Record,
                label + ": equal content is recorded");
            var heldEqual = ForPolicy(local, dropbox, Ctx(firstRun: true, held: true), policy, localHash: "h");
            Check.Require(heldEqual.Kind == ActionKind.Record && heldEqual.Row.Context.Held,
                label + ": equal content passes through a hold");
            var differs = ForPolicy(local, dropbox, Ctx(firstRun: true), policy, localHash: "x");
            Check.Require(differs.Kind == kind && differs.Target == "a.txt" && differs.InfoAfter is null,
                label + ": different content follows the policy");
        }

        var policyLocal = ForPolicy(File("a.txt", 1, Before), File("A.txt", 2, Before.AddDays(1), hash: "z"),
            Ctx(firstRun: true), FirstRunPolicy.LocalWins);
        Check.Require(policyLocal.Kind == ActionKind.Upload && policyLocal.Target == "A.txt"
            && policyLocal.InfoAfter is null, "A: first run LocalWins");
        var policyDropbox = ForPolicy(File("a.txt", 1, Before), File("A.txt", 2, Before.AddDays(1), hash: "z"),
            Ctx(firstRun: true), FirstRunPolicy.DropboxWins);
        Check.Require(policyDropbox.Kind == ActionKind.Download && policyDropbox.Target == "a.txt",
            "A: first run DropboxWins");

        var unreliable = For(File("a", 1, ComparedAt.AddHours(1)), File("a", 2, Before));
        Check.Require(unreliable.Kind == ActionKind.KeepBoth && unreliable.InfoAfter == TextKey.InfoKeptBoth,
            "A: unequal sizes at an unreliable time keep both without a hash");
        var equalSeconds = For(File("a", 1, Before), File("a", 2, Before.AddMilliseconds(500)));
        Check.Require(equalSeconds.Kind == ActionKind.Notice && equalSeconds.Cause == TextKey.TieEqualTimes,
            "A: equal seconds");

        var newerLocal = For(File("a.txt", 1, Before.AddDays(1)), File("A.txt", 2, Before));
        Check.Require(newerLocal.Kind == ActionKind.Upload && newerLocal.Target == "A.txt"
            && newerLocal.InfoAfter == TextKey.InfoNewestWins, "A: local newer later run");
        var newerLocalFirst = For(File("a.txt", 1, Before.AddDays(1)), File("A.txt", 2, Before),
            context: Ctx(firstRun: true));
        Check.Require(newerLocalFirst.Kind == ActionKind.Upload && newerLocalFirst.InfoAfter is null,
            "A: local newer first run");
        var newerDropbox = For(File("a.txt", 2, Before), File("A.txt", 1, Before.AddDays(1)));
        Check.Require(newerDropbox.Kind == ActionKind.Download && newerDropbox.Target == "a.txt"
            && newerDropbox.InfoAfter == TextKey.InfoNewestWins, "A: dropbox newer");
    }

    private static void WithRecordTable()
    {
        var saved = SavedFile("a.txt", 4);
        var unchangedLocal = File("a.txt", 4);
        var unchangedDropbox = File("a.txt", 4);
        var changedDropbox = File("a.txt", 4, hash: "x");

        Require(For(null, null, saved), ActionKind.ForgetOnly, "B A/A");
        Require(For(null, unchangedDropbox, saved), ActionKind.DeleteDropbox, "B A/U file");
        Require(For(null, Folder("d"), SavedFolder("d")), ActionKind.DeferFolder, "B A/U folder");
        var editBeatsDelete = For(null, changedDropbox, saved);
        Check.Require(editBeatsDelete.Kind == ActionKind.Download && editBeatsDelete.Target == "a.txt"
            && editBeatsDelete.InfoAfter == TextKey.InfoEditBeatsDelete, "B A/C");
        var absentFlipped = For(null, Folder("a.txt"), saved);
        Check.Require(absentFlipped.Kind == ActionKind.ReplaceKind && absentFlipped.Side == Side.Local
            && absentFlipped.Target == "a.txt", "B A/F");

        Require(For(unchangedLocal, null, saved), ActionKind.DeleteLocal, "B U/A file");
        Require(For(Folder("d"), null, SavedFolder("d")), ActionKind.DeferFolder, "B U/A folder");

        var inSync = For(unchangedLocal, unchangedDropbox, saved);
        Check.Require(inSync.Kind == ActionKind.None && inSync.None == NoneReason.InSync, "B U/U in sync");
        Require(For(File("a.txt", 4, id: "j"), unchangedDropbox, saved, localHash: "h"), ActionKind.Record,
            "B U/U local metadata refresh");
        Require(For(unchangedLocal, File("a.txt", 4, id: "j"), saved), ActionKind.Record,
            "B U/U dropbox id refresh");
        Require(For(Folder("docs"), Folder("docs"), SavedFolder("docs")), ActionKind.None, "B U/U folder in sync");
        Require(For(Folder("docs"), Folder("Docs"), SavedFolder("Docs")), ActionKind.Record,
            "B U/U folder spelling refresh");

        // A recorded row settles from its saved fact; an unreliable observed time never triggers a content recheck.
        var future = ComparedAt.AddHours(1);
        var futureRecord = For(File("a.txt", 4, future), unchangedDropbox, SavedFile("a.txt", 4, modified: future));
        Check.Require(futureRecord.IsInSync(),
            "B U/U a recorded row with an unreliable time settles in sync without a hash");

        var replaced = For(unchangedLocal, changedDropbox, saved);
        Check.Require(replaced.Kind == ActionKind.Download && replaced.Target == "a.txt" && replaced.InfoAfter is null,
            "B U/C");
        var unchangedFlipped = For(unchangedLocal, Folder("a.txt"), saved);
        Check.Require(unchangedFlipped.Kind == ActionKind.ReplaceKind && unchangedFlipped.Side == Side.Local
            && unchangedFlipped.Target == "a.txt", "B U/F");

        var editedDeleted = For(File("a.txt", 5), null, saved);
        Check.Require(editedDeleted.Kind == ActionKind.Upload && editedDeleted.Target == "a.txt"
            && editedDeleted.InfoAfter == TextKey.InfoEditBeatsDelete, "B C/A");
        var edited = For(File("a.txt", 5), unchangedDropbox, saved);
        Check.Require(edited.Kind == ActionKind.Upload && edited.Target == "a.txt" && edited.InfoAfter is null,
            "B C/U");

        Require(For(File("a.txt", 4, id: "j"), File("a.txt", 4, hash: "x"), saved, localHash: "x"),
            ActionKind.Record, "B C/C equal content");
        var keptBoth = For(File("a.txt", 4, id: "j"), File("a.txt", 4, hash: "x"), saved, localHash: "y");
        Check.Require(keptBoth.Kind == ActionKind.KeepBoth && keptBoth.InfoAfter == TextKey.InfoKeptBoth,
            "B C/C different content");
        Require(For(File("a.txt", 5), changedDropbox, saved), ActionKind.KeepBoth, "B C/C different size");
        var editedFlipped = For(File("a.txt", 5), Folder("a.txt"), saved);
        Check.Require(editedFlipped.Kind == ActionKind.Notice && editedFlipped.NoticeKind == IssueKind.Tie
            && editedFlipped.Cause == TextKey.TieKindBoth, "B C/F");

        var flippedDeleted = For(Folder("a.txt"), null, saved);
        Check.Require(flippedDeleted.Kind == ActionKind.ReplaceKind && flippedDeleted.Side == Side.Dropbox
            && flippedDeleted.Target == "a.txt", "B F/A");
        var flippedUnchanged = For(Folder("a.txt"), unchangedDropbox, saved);
        Check.Require(flippedUnchanged.Kind == ActionKind.ReplaceKind && flippedUnchanged.Side == Side.Dropbox,
            "B F/U");
        var flippedChanged = For(Folder("a.txt"), changedDropbox, saved);
        Check.Require(flippedChanged.Kind == ActionKind.Notice && flippedChanged.Cause == TextKey.TieKindBoth,
            "B F/C");

        Require(For(Folder("a"), Folder("a"), SavedFile("a")), ActionKind.Record, "B F/F same new kind folders");
        Require(For(File("a", 2, hash: "x"), File("a", 2, hash: "x"), SavedFolder("a"), localHash: "x"),
            ActionKind.Record, "B F/F same new kind equal files");
        var flippedDiffer = For(File("a", 2, hash: "x"), File("a", 2, hash: "x"), SavedFolder("a"), localHash: "y");
        Check.Require(flippedDiffer.Kind == ActionKind.Notice && flippedDiffer.Cause == TextKey.TieKindBoth,
            "B F/F same new kind different files");
        var flippedUnequal = For(File("a", 2), File("a", 3), SavedFolder("a"));
        Check.Require(flippedUnequal.Kind == ActionKind.Notice && flippedUnequal.NoticeKind == IssueKind.Tie
            && flippedUnequal.Cause == TextKey.TieKindBoth,
            "B F/F unequal sizes tie without requesting a local hash");
        Require(For(File("a", 2), File("a", 2), SavedFolder("a")), ActionKind.NeedLocalHash,
            "B F/F equal sizes still need the local hash");
    }

    private static void NeedLocalHash()
    {
        var saved = SavedFile("a.txt", 4);
        var metadataOnly = For(File("a.txt", 4, id: "j"), File("a.txt", 4), saved);
        Check.Require(metadataOnly.Kind == ActionKind.NeedLocalHash, "NeedLocalHash: metadata-only change");

        // A metadata-only local change plus a Dropbox edit is a Download, not KeepBoth.
        var second = For(File("a.txt", 4, id: "j"), File("a.txt", 4, hash: "x"), saved, localHash: "h");
        Check.Require(second.Kind == ActionKind.Download, "NeedLocalHash: metadata-only plus Dropbox edit");
        // A metadata-only local change plus a Dropbox delete is a DeleteLocal, not a resurrection.
        var deleted = For(File("a.txt", 4, id: "j"), null, saved, localHash: "h");
        Check.Require(deleted.Kind == ActionKind.DeleteLocal, "NeedLocalHash: metadata-only plus Dropbox delete");

        var sameSizeBothChanged = For(File("a.txt", 4, id: "j"), File("a.txt", 4, hash: "x"), saved);
        Check.Require(sameSizeBothChanged.Kind == ActionKind.NeedLocalHash, "NeedLocalHash: both changed, same size");
        var caseOnly = For(File("a_b"), File("A_B"));
        Check.Require(caseOnly.Kind == ActionKind.NeedLocalHash, "NeedLocalHash: a case-only spelling pair");
        var caseOnlyUnequal = For(File("a_b", 1), File("A_B", 2));
        Check.Require(caseOnlyUnequal.Kind == ActionKind.Notice && caseOnlyUnequal.NoticeKind == IssueKind.Tie
            && caseOnlyUnequal.Cause == TextKey.TieEqualTimes,
            "NeedLocalHash: a case-only pair of unequal sizes ties without needing the local hash");
        Check.Require(For(File("a_b"), File("A_B"), localHash: "h").Kind == ActionKind.Record,
            "NeedLocalHash: a case-only pair re-decides on equal content");
    }

    // NAME-SKIP option C: the Engine substitutes no name, so underscores are literal and a skipped invalid entry
    // never reaches a mapped NameClash or an ordinary saved-record effect.
    private static void NamePolicy()
    {
        Require(For(File("a_b"), File("a_b"), localHash: "h"), ActionKind.Record,
            "names: a valid underscore name is literal and records against itself");
        Require(For(Folder("a_b"), Folder("a_b")), ActionKind.Record,
            "names: an underscore folder records against itself");
        var caseOnly = For(File("a_b"), File("A_B"), localHash: "x");
        Check.Require(caseOnly.Kind == ActionKind.Notice && caseOnly.NoticeKind == IssueKind.Tie
            && caseOnly.Cause == TextKey.TieEqualTimes,
            "names: differing content under a raw case-only spelling follows ordinary time rules");
        Require(For(File("a_b"), File("a_b"), localHash: "h", context: Ctx(firstRun: true)), ActionKind.Record,
            "names: first run literal underscore with equal content records");
        var skippedClash = For(File("a:b"), null, context: Ctx(skipped: true), extra: new[] { File("a:b") });
        Check.Require(skippedClash.Kind == ActionKind.None && skippedClash.None == NoneReason.Skipped,
            "names: a skipped invalid entry never becomes a NameClash");
        var skippedRecord = For(File("a:b", 4), null, SavedFile("a:b", 4), Ctx(skipped: true));
        Check.Require(skippedRecord.Kind == ActionKind.None && skippedRecord.None == NoneReason.Skipped,
            "names: a skipped invalid entry keeps no saved-record effect");
        var historical = For(File("a_b", 4), null, SavedFile("a_b", 4, dropboxPath: "a:b"), Ctx(skipped: true));
        Check.Require(historical.Kind == ActionKind.None && historical.None == NoneReason.Skipped,
            "names: a preserved historical binding with an invalid remote spelling stays skipped");
    }

    // P6 phase 1 option A: only a NO-RECORD, EQUAL-SIZE, UNRELIABLE-TIME row hashes to choose Record or KeepBoth.
    // A recorded row never rehashes for time alone; reliable ordering and explicit first-run policy keep their rules.
    private static void UnreliableTime()
    {
        var future = ComparedAt.AddHours(1);
        var unreliable = File("a.txt", 5, future);
        var reliable = File("a.txt", 5, Before);

        Require(For(unreliable, reliable), ActionKind.NeedLocalHash,
            "P6: an equal-size unreliable-time row compares content before deciding");
        var equal = For(unreliable, reliable, localHash: "h");
        Check.Require(equal.Kind == ActionKind.Record && equal.InfoAfter is null,
            "P6: identical content records quietly");
        var differs = For(unreliable, reliable, localHash: "x");
        Check.Require(differs.Kind == ActionKind.KeepBoth && differs.InfoAfter == TextKey.InfoKeptBoth,
            "P6: differing content keeps both automatically with the usual notice");
        var unequal = For(File("a.txt", 4, future), File("a.txt", 5, Before));
        Check.Require(unequal.Kind == ActionKind.KeepBoth && unequal.InfoAfter == TextKey.InfoKeptBoth,
            "P6: unequal sizes prove different content and keep both without a hash");
        Require(For(File("a.txt", 5, default(DateTimeOffset)), File("a.txt", 5, Before)), ActionKind.NeedLocalHash,
            "P6: an unset (epoch) local time is unreliable too");
        // Each side's time is sampled independently, so either side can make the pair unreliable.
        Require(For(File("a.txt", 5, Before), File("a.txt", 5, future)), ActionKind.NeedLocalHash,
            "P6: a future Dropbox time is unreliable on its own side");
        Require(For(File("a.txt", 5, future), File("a.txt", 5, future)), ActionKind.NeedLocalHash,
            "P6: both future times are unreliable");
        Require(For(File("a.txt", 5, Before), File("a.txt", 5, default(DateTimeOffset))), ActionKind.NeedLocalHash,
            "P6: an unset (epoch) Dropbox time is unreliable on its own side");

        // The old same-second metadata shortcut may not silently record different first-run bytes.
        var sameSecond = File("a.txt", 5, future);
        Require(For(sameSecond, sameSecond, context: Ctx(firstRun: true)), ActionKind.NeedLocalHash,
            "P6: a first-run same-size same-second unreliable row still compares content");
        var firstRunEqual = For(sameSecond, sameSecond, localHash: "h", context: Ctx(firstRun: true));
        Check.Require(firstRunEqual.Kind == ActionKind.Record && firstRunEqual.InfoAfter is null,
            "P6: first-run identical content records quietly");
        var firstRunDiffers = For(sameSecond, sameSecond, localHash: "x", context: Ctx(firstRun: true));
        Check.Require(firstRunDiffers.Kind == ActionKind.KeepBoth && firstRunDiffers.InfoAfter == TextKey.InfoKeptBoth,
            "P6: first-run different bytes at an unreliable same second keep both, never a silent record");

        // Explicit first-run policy and reliable ordering keep their own outcomes.
        var policyLocal = ForPolicy(File("a.txt", 5, future), File("a.txt", 5, Before), Ctx(firstRun: true),
            FirstRunPolicy.LocalWins, localHash: "x");
        Check.Require(policyLocal.Kind == ActionKind.Upload && policyLocal.InfoAfter is null,
            "P6: an explicit first-run LocalWins still chooses the local copy");
        var policyDropbox = ForPolicy(File("a.txt", 5, future), File("a.txt", 5, Before), Ctx(firstRun: true),
            FirstRunPolicy.DropboxWins, localHash: "x");
        Check.Require(policyDropbox.Kind == ActionKind.Download,
            "P6: an explicit first-run DropboxWins still chooses Dropbox");
        var reliableOrder = For(File("a.txt", 5, Before.AddDays(1)), File("a.txt", 5, Before), localHash: "x");
        Check.Require(reliableOrder.Kind == ActionKind.Upload && reliableOrder.InfoAfter == TextKey.InfoNewestWins,
            "P6: reliable unequal times still choose the newest copy");

        // Raw invalid-name skip dominates every time-policy case, including a saved row.
        var skipped = For(sameSecond, sameSecond, context: Ctx(skipped: true));
        Check.Require(skipped.Kind == ActionKind.None && skipped.None == NoneReason.Skipped,
            "P6: a skipped invalid name precedes the unreliable-time comparison");
        var skippedSaved = For(File("a.txt", 5, future), null, SavedFile("a.txt", 5, modified: future),
            Ctx(skipped: true));
        Check.Require(skippedSaved.Kind == ActionKind.None && skippedSaved.None == NoneReason.Skipped,
            "P6: a skipped invalid name with a record stays skipped");
    }

    // P4 first-run deletion rows (P4-T05a and P4-T06..T10). Evidence is requested only for a selected local-only
    // file after a complete listing; the action preserves unless equal proven content and a strictly newer
    // deletion reach T09. Local.Modified is Before, so a deletion before it is T07 and one after it is T09.
    private static void FirstRunDeletion()
    {
        const string Destination = "a.txt";
        var local = File(Destination, 4);
        var selected = Ctx(destination: Destination, completeInventory: true, deletionEligible: true);

        var request = For(local, null, context: selected);
        Check.Require(request.Kind == ActionKind.NeedDeletionEvidence && request.Target == Destination,
            "T05a: a selected local-only file asks for its one prepared evidence at the exact destination");
        Require(For(local, null, context: Ctx(destination: Destination, completeInventory: true)),
            ActionKind.Upload, "T05a: an unselected file follows ordinary first-run sync");
        var incomplete = For(local, null, context: Ctx(destination: Destination, deletionEligible: true));
        Check.Require(incomplete.Kind == ActionKind.None && incomplete.None == NoneReason.Held,
            nameof(RowContext.CompleteInventory));
        var noDestination = For(local, null, context: Ctx(completeInventory: true, deletionEligible: true));
        Check.Require(noDestination.Kind == ActionKind.Notice && noDestination.NoticeKind == IssueKind.Failure
            && noDestination.Cause == TextKey.FailChanged,
            "T05a: a selected file without a destination keeps a non-destructive failure");
        Require(For(local, null, context: selected with { FirstRun = true }), ActionKind.NeedDeletionEvidence,
            "T05a: a first-repair selection asks for its evidence");
        Require(For(local, null, context: selected), ActionKind.NeedDeletionEvidence,
            "T05a: a later retained obligation with its own eligibility still asks for evidence");
        Require(For(Folder(Destination), null, context: selected),
            ActionKind.DeferFolder, nameof(ActionKind.DeferFolder));

        // T01: a saved record keeps the normal table and never asks for first-run history.
        Require(For(local, null, SavedFile(Destination, 4), selected), ActionKind.DeleteLocal,
            "T01: a saved record uses the normal table even with deletion eligibility");

        // T06: the documented history negative preserves through an ordinary upload with no notice.
        var negative = For(local, null, context: selected,
            deletion: new DeletionEvidence.NoRetainedHistory(Destination));
        Check.Require(negative.Kind == ActionKind.Upload && negative.Target == Destination
            && negative.InfoAfter is null,
            "T06: a documented history negative uploads without a notice");

        // T07: a reliable deletion strictly before the local edit preserves without requesting a comparison hash.
        var earlier = Before.AddDays(-1);
        var missingHash = For(local, null, context: selected,
            deletion: Deleted(earlier, timeUnavailable: null, contentHash: null,
                hashUnavailable: TextKey.DeletionHashMissing));
        Check.Require(missingHash.Kind == ActionKind.Upload && missingHash.InfoAfter is null,
            "T07: a reliable deletion before the local edit uploads without a hash or notice");
        var malformedHash = For(local, null, context: selected,
            deletion: Deleted(earlier, timeUnavailable: null, contentHash: null,
                hashUnavailable: TextKey.DeletionHashMalformed));
        Check.Require(malformedHash.Kind == ActionKind.Upload && malformedHash.InfoAfter is null,
            "T07: a malformed newest hash does not defeat the reliable deletion-before-edit upload");
        var overlap = For(local, null, context: selected, localHash: ContentB,
            deletion: Deleted(earlier, null, ContentA, null));
        Check.Require(overlap.Kind == ActionKind.Upload && overlap.InfoAfter == TextKey.InfoEditBeatsDelete,
            "T07/T08 overlap: a reliable deletion with known differing hashes emits one edit-beats-delete notice");
        var equalAtT07 = For(local, null, context: selected, localHash: ContentA,
            deletion: Deleted(earlier, null, ContentA, null));
        Check.Require(equalAtT07.Kind == ActionKind.Upload && equalAtT07.InfoAfter is null,
            "T07: an equal known hash without a newer deletion invents no notice");

        // T08: proven differing content preserves even when the deletion time is missing, equal or unreliable.
        var noTime = For(local, null, context: selected, localHash: ContentB,
            deletion: Deleted(null, TextKey.DeletionTimeMissing, ContentA, null));
        Check.Require(noTime.Kind == ActionKind.Upload && noTime.InfoAfter == TextKey.InfoEditBeatsDelete,
            "T08: differing content uploads with edit-beats-delete when the deletion time is unavailable");
        var equalTime = For(local, null, context: selected, localHash: ContentB,
            deletion: Deleted(Before, null, ContentA, null));
        Check.Require(equalTime.Kind == ActionKind.Upload && equalTime.InfoAfter == TextKey.InfoEditBeatsDelete,
            "T08: an equal deletion time does not defeat differing content");
        var unreliableTime = For(local, null, context: selected, localHash: ContentB,
            deletion: Deleted(ComparedAt.AddHours(1), null, ContentA, null));
        Check.Require(unreliableTime.Kind == ActionKind.Upload && unreliableTime.InfoAfter == TextKey.InfoEditBeatsDelete,
            "T08: a future unreliable deletion time does not defeat differing content");

        // T09: equal proven content and a strictly newer deletion authorize the local removal.
        var newer = For(local, null, context: selected, localHash: ContentA,
            deletion: Deleted(ComparedAt.AddHours(-1), null, ContentA, null));
        Check.Require(newer.Kind == ActionKind.DeleteLocal && newer.Target == Destination,
            "T09: equal proven content and a strictly newer deletion authorizes the local removal");
        var newerEqualTime = For(local, null, context: selected, localHash: ContentA,
            deletion: Deleted(Before, null, ContentA, null));
        Check.Require(newerEqualTime.Kind == ActionKind.Notice && newerEqualTime.NoticeKind == IssueKind.Failure
            && newerEqualTime.Cause == TextKey.FailChanged,
            "T09: equal hashes with an equal time never delete");
        var newerNoTime = For(local, null, context: selected, localHash: ContentA,
            deletion: Deleted(null, TextKey.DeletionTimeUnavailable, ContentA, null));
        Check.Require(newerNoTime.Kind == ActionKind.Notice && newerNoTime.NoticeKind == IssueKind.Failure
            && newerNoTime.Cause == TextKey.DeletionTimeUnavailable,
            "T09: equal hashes with unavailable time never delete");
        Require(For(local, null, context: selected,
            deletion: Deleted(Before, null, ContentA, null)), ActionKind.NeedLocalHash,
            "T08/T09: a known newest hash without the local hash requests it");

        // T10-a: genuinely unavailable history syncs as new with the existing notice, never a deletion.
        var unavailable = For(local, null, context: selected,
            deletion: new DeletionEvidence.HistoryUnavailable(Destination, TextKey.RecoveryDropboxHistory));
        Check.Require(unavailable.Kind == ActionKind.Upload
            && unavailable.InfoAfter == TextKey.InfoDropboxHistoryUnchecked,
            "T10-a: unavailable history syncs as new with the existing unchecked-history notice");
        // T10-b: every remaining failure keeps its own specific non-destructive failure.
        var unknown = For(local, null, context: selected,
            deletion: new DeletionEvidence.Unknown(Destination, TextKey.ReadDropboxFailed));
        Check.Require(unknown.Kind == ActionKind.Notice && unknown.NoticeKind == IssueKind.Failure
            && unknown.Cause == TextKey.ReadDropboxFailed, "T10-b: an unknown current fact retains its failure");
        var unusableHash = For(local, null, context: selected, localHash: ContentB,
            deletion: Deleted(ComparedAt.AddHours(-1), null, null, TextKey.DeletionHashUnavailable));
        Check.Require(unusableHash.Kind == ActionKind.Notice && unusableHash.NoticeKind == IssueKind.Failure
            && unusableHash.Cause == TextKey.DeletionHashUnavailable,
            "T10-b: unusable newest content keeps a failure, never a deletion");
        var live = For(local, null, context: selected,
            deletion: new DeletionEvidence.LiveAtTarget(Destination, new IdentityLocation("local", Destination, local)));
        Check.Require(live.Kind == ActionKind.Notice && live.NoticeKind == IssueKind.Failure
            && live.Cause == TextKey.FailChanged,
            "a live target reobserved elsewhere remains a non-destructive failure, never a deletion");
        var mismatch = For(local, null, context: selected,
            deletion: new DeletionEvidence.NoRetainedHistory("other.txt"));
        Check.Require(mismatch.Kind == ActionKind.Notice && mismatch.Cause == TextKey.FailChanged,
            "evidence for another path is never applied to this row");
    }

    private const string RetainedPath = "Parent";

    private static void RetainedFolderDecisions()
    {
        foreach (var first in new[] { false, true })
        foreach (var complete in new[] { false, true })
        foreach (var kind in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        foreach (var folder in new[] { false, true })
        {
            var preserve = kind == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice;
            var row = MakeRow(folder ? Folder(RetainedPath) : File(RetainedPath), null,
                context: Ctx(firstRun: first, completeInventory: complete,
                    deletionEligible: !preserve, destination: RetainedPath) with { FirstRunDeletion = kind });
            var action = Decide.For(row, Newest, ComparedAt, localContentDirty: false);
            var expected = preserve && !folder ? ActionKind.Upload : !complete ? ActionKind.None
                : folder ? ActionKind.DeferFolder : ActionKind.NeedDeletionEvidence;
            Check.Require(action.Kind == expected &&
                (expected != ActionKind.None || action.None == NoneReason.Held) &&
                action.InfoAfter == (preserve && !folder ? TextKey.InfoDropboxHistoryUnchecked : (TextKey?)null),
                nameof(RetainedFolderDecisions));
            var resumed = Decide.ResumePreservedFolder(row, Newest, ComparedAt);
            Check.Require(preserve && folder && complete
                ? resumed.Kind == ActionKind.EnsureDropboxFolder && resumed.Target == RetainedPath &&
                    resumed.InfoAfter == TextKey.InfoDropboxHistoryUnchecked
                : resumed == action, nameof(RetainedFolderDecisions));
        }
        foreach (var kind in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        {
            var unselected = MakeRow(File(RetainedPath), null,
                context: Ctx(destination: RetainedPath, completeInventory: true) with { FirstRunDeletion = kind });
            var action = Decide.For(unselected, Newest, ComparedAt, localContentDirty: false);
            Check.Require(action.Kind == ActionKind.Upload && action.Target == RetainedPath &&
                action.InfoAfter == (kind == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice
                    ? TextKey.InfoDropboxHistoryUnchecked : (TextKey?)null), nameof(RetainedFolderDecisions));
        }
        var retained = MakeRow(Folder(RetainedPath), null,
            context: Ctx(completeInventory: true, destination: RetainedPath) with
            { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice });
        foreach (var guarded in new[]
        {
            retained with { Context = retained.Context with { OutOfScope = true } },
            retained with { Context = retained.Context with { Skipped = true } },
            retained with { Context = retained.Context with { Held = true, HeldKind = IssueKind.Failure } },
            retained with { Context = retained.Context with { Held = true, HeldKind = IssueKind.Tie } },
            retained with { Context = retained.Context with { Held = true, HeldKind = IssueKind.NameClash } },
            retained with { Extra = [Folder(RetainedPath)] },
            retained with { Dropbox = Folder(RetainedPath) },
            retained with { Dropbox = Folder(RetainedPath), Synced = SavedFolder(RetainedPath) }
        })
        {
            Check.Require(Decide.ResumePreservedFolder(guarded, Newest, ComparedAt) ==
                Decide.For(guarded, Newest, ComparedAt, localContentDirty: false), nameof(RetainedFolderDecisions));
        }
        foreach (var reason in new[] { TextKey.DeletionTimeMissing, TextKey.DeletionTimeMalformed,
            TextKey.DeletionTimeUnavailable })
        {
            var action = For(File(DeletionPath, DeletionSize), null,
                context: Ctx(completeInventory: true, deletionEligible: true, destination: DeletionPath),
                localHash: ContentA, deletion: Deleted(null, reason, ContentA, null));
            Check.Require(action.Kind == ActionKind.Notice && action.NoticeKind == IssueKind.Failure &&
                action.Cause == reason, nameof(RetainedFolderDecisions));
        }
        var future = For(File(DeletionPath, DeletionSize), null,
            context: Ctx(completeInventory: true, deletionEligible: true, destination: DeletionPath),
            localHash: ContentA, deletion: Deleted(ComparedAt.AddTicks(1), null, ContentA, null));
        Check.Require(future.Kind == ActionKind.Notice && future.Cause == TextKey.FailChanged,
            nameof(RetainedFolderDecisions));
    }

    // Same target and size as the existing T06-T10 deletion fixture.
    private const string DeletionPath = "a.txt";
    private const long DeletionSize = 4;

    private static DeletionEvidence.DeletedFile Deleted(DateTimeOffset? deletedAt, TextKey? timeUnavailable,
        string? contentHash, TextKey? hashUnavailable) =>
        new("a.txt", "dropbox", "remote-id", null, deletedAt, timeUnavailable, contentHash, hashUnavailable);

    private static SyncAction For(Entry? local, Entry? dropbox, SyncedEntry? synced = null,
        RowContext? context = null, string? localHash = null, IReadOnlyList<Entry>? extra = null,
        DeletionEvidence? deletion = null)
    {
        var row = MakeRow(local, dropbox, synced, context, localHash, extra, deletion);
        var action = Decide.For(row, Newest, ComparedAt, localContentDirty: false);
        Check.Require(ReferenceEquals(action.Row, row), "Decide retains the original row and its context");
        return action;
    }

    private static SyncAction ForPolicy(Entry? local, Entry? dropbox, RowContext context, FirstRunPolicy policy,
        string? localHash = null)
    {
        var row = MakeRow(local, dropbox, null, context, localHash);
        var action = Decide.For(row, policy, ComparedAt, localContentDirty: false);
        Check.Require(ReferenceEquals(action.Row, row), "Each policy retains the original row and its context");
        return action;
    }

    private static Row MakeRow(Entry? local, Entry? dropbox, SyncedEntry? synced = null,
        RowContext? context = null, string? localHash = null, IReadOnlyList<Entry>? extra = null,
        DeletionEvidence? deletion = null)
    {
        var path = synced?.LocalPath ?? dropbox?.Path ?? local?.Path ?? "";
        var key = synced?.LocalKey ?? (dropbox is null ? PathNames.Key(path) : PathNames.DropboxKey(path));
        return new Row(key, local, dropbox, synced, extra ?? Array.Empty<Entry>(), localHash, context ?? Ctx(),
            deletion);
    }

    private static RowContext Ctx(bool firstRun = false, bool held = false, string? destination = null,
        Side? underFolderDelete = null, bool holdsNestedRoot = false, bool outOfScope = false,
        bool skipped = false, IssueKind? heldKind = null, bool completeInventory = false,
        bool deletionEligible = false) =>
        new(outOfScope, skipped, held, underFolderDelete, holdsNestedRoot, firstRun, completeInventory,
            deletionEligible, destination, heldKind);

    private static Entry File(string path, long size = 1, DateTimeOffset? modified = null,
        string hash = "h", string id = "i") =>
        new(path, false, size, modified ?? Before, id, "", hash, null);

    private static Entry Folder(string path) => new(path, true, 0, default, "i", "", "", null);

    private static SyncedEntry SavedFile(string path, long size = 1, string hash = "h", string? dropboxPath = null,
        DateTimeOffset? modified = null)
    {
        var dropbox = dropboxPath ?? path;
        return new(PathNames.Key(path), path, PathNames.DropboxKey(dropbox), dropbox, false, "i", size,
            (modified ?? Before).UtcTicks, "i", hash);
    }

    private static SyncedEntry SavedFolder(string path, string? dropboxPath = null)
    {
        var dropbox = dropboxPath ?? path;
        return new(PathNames.Key(path), path, PathNames.DropboxKey(dropbox), dropbox, true, "i", 0, default, "i", "");
    }

    private static void Require(SyncAction action, ActionKind kind, string what) =>
        Check.Require(action.Kind == kind, what + " (got " + action.Kind + ")");
}
