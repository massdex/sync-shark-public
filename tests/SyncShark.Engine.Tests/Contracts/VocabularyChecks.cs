using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Checks for the root vocabulary owners (`--engine-checks vocabulary`): ResourceNames ids, exact enum
/// parsing, read-failure and effect vocabulary, effect events, and RowContext evaluation.</summary>
public static class VocabularyChecks
{
    public static void Run()
    {
        CreatesIds();
        ParsesExactNames();
        DeclaresIssueAndChoiceVocabulary();
        SelectsSideVocabulary();
        BuildsEffectEvents();
        EvaluatesRowContext();
    }

    // Ids are Guid N-format, unique, and the shared length is that format's length.
    private static void CreatesIds()
    {
        var first = ResourceNames.NewId();
        var second = ResourceNames.NewId();
        Check.Require(first.Length == ResourceNames.IdLength && first.Length == 32, "NewId returns 32 characters");
        Check.Require(first != second, "NewId returns a fresh id");
        Check.Require(Guid.TryParseExact(first, ResourceNames.IdFormat, out _),
            "NewId returns a Guid in IdFormat");
        Check.Require(ResourceNames.IdFormat == "N" && ResourceNames.IdLength == 32,
            "IdLength belongs with the id format");
    }

    // Canonical declared names only, and both overloads agree for the settings enums.
    private static void ParsesExactNames()
    {
        Check.Require(EnumNames.TryExact<FirstRunPolicy>(nameof(FirstRunPolicy.NewestWins), out var policy)
            && policy == FirstRunPolicy.NewestWins, "a declared member name parses");
        Check.Require(!EnumNames.TryExact<FirstRunPolicy>("newestWins", out _), "case variants fail");
        Check.Require(!EnumNames.TryExact<FirstRunPolicy>("NEWESTWINS", out _), "upper-case names fail");
        Check.Require(!EnumNames.TryExact<FirstRunPolicy>("1", out _), "a numeric spelling fails");
        Check.Require(!EnumNames.TryExact<FirstRunPolicy>(" NewestWins", out _), "leading whitespace fails");
        Check.Require(!EnumNames.TryExact<FirstRunPolicy>("NewestWins ", out _), "trailing whitespace fails");
        Check.Require(!EnumNames.TryExact<FirstRunPolicy>("Sideways", out _), "an unknown name fails");
        Check.Require(!EnumNames.TryExact<FirstRunPolicy>(null, out var missing) && missing == default,
            "null fails and yields the default");

        Check.Require(EnumNames.TryExact(typeof(RecycleFallback), nameof(RecycleFallback.Fail), out var reflected)
            && reflected is RecycleFallback.Fail, "the reflective form parses a declared member");
        Check.Require(!EnumNames.TryExact(typeof(RecycleFallback), "fail", out var badReflected)
            && badReflected is null, "the reflective form fails a case variant and yields null");
        Check.Require(!EnumNames.TryExact(typeof(RecycleFallback), "9", out _), "the reflective form fails a number");
        Check.Require(!EnumNames.TryExact(typeof(string), "Fail", out var nonEnum) && nonEnum is null,
            "a non-enum type fails before parsing");

        // Both overloads agree for every settings enum member and for the TextKey vocabulary.
        foreach (var name in Enum.GetNames<FirstRunPolicy>())
            Check.Require(EnumNames.TryExact<FirstRunPolicy>(name, out var typed)
                && EnumNames.TryExact(typeof(FirstRunPolicy), name, out var boxed) && Equals(typed, boxed),
                "both overloads accept " + name);
        foreach (var name in Enum.GetNames<RecycleFallback>())
            Check.Require(EnumNames.TryExact<RecycleFallback>(name, out var typed)
                && EnumNames.TryExact(typeof(RecycleFallback), name, out var boxed) && Equals(typed, boxed),
                "both overloads accept " + name);
        var keys = Enum.GetValues<TextKey>();
        foreach (var key in keys)
            Check.Require(EnumNames.TryExact<TextKey>(key.ToString(), out var parsed) && parsed == key,
                "every TextKey name parses exactly");
        Check.Require(keys.Length == Text.All.Count, "every TextKey has its template");

        // A repeated or unknown template key keeps Text's own error, not a parse fallback.
        Check.Require(!EnumNames.TryExact<TextKey>("NoSuchTemplate", out _), "an unknown template key fails");
    }

    // The drive decision and Retry add vocabulary; the appended members must not renumber the kinds
    // and choices that existing saved state and CLI values already use.
    private static void DeclaresIssueAndChoiceVocabulary()
    {
        var kinds = Enum.GetValues<IssueKind>();
        Check.Require(kinds is
                [IssueKind.Failure, IssueKind.Tie, IssueKind.NameClash, IssueKind.Info, IssueKind.Skipped,
                    IssueKind.DriveChange, IssueKind.RootIdentity],
            "the issue kinds append DriveChange and RootIdentity after Skipped and nothing else");
        Check.Require(kinds.Select((kind, ordinal) => (int)kind == ordinal).All(kept => kept),
            "no issue kind is renumbered");

        Check.Require(Enum.GetNames<ResolutionChoice>() is
                [nameof(ResolutionChoice.UseLocal), nameof(ResolutionChoice.UseDropbox),
                    nameof(ResolutionChoice.KeepBoth), nameof(ResolutionChoice.Dismiss),
                    nameof(ResolutionChoice.RenameIncoming), nameof(ResolutionChoice.ReplaceExisting),
                    nameof(ResolutionChoice.KeepLocal), nameof(ResolutionChoice.KeepDropbox),
                    nameof(ResolutionChoice.RenameLocal), nameof(ResolutionChoice.RenameDropbox),
                    nameof(ResolutionChoice.Retry), nameof(ResolutionChoice.FirstSync),
                    nameof(ResolutionChoice.SyncNormally), nameof(ResolutionChoice.KeepFolderAlways)],
            "the choices append Retry, FirstSync, SyncNormally and KeepFolderAlways; no Stop or removal choice is added");
        Check.Require(Enum.GetValues<ResolutionChoice>().Select((choice, ordinal) => (int)choice == ordinal)
                .All(kept => kept),
            "no existing choice is renumbered and the new choices continue the ordinals");
    }

    // Side-selected vocabulary, with the side's own branch and no added validation.
    private static void SelectsSideVocabulary()
    {
        Check.Require(Text.ReadFailedOn(Side.Local) == TextKey.ReadLocalFailed, "the local read failure");
        Check.Require(Text.ReadFailedOn(Side.Dropbox) == TextKey.ReadDropboxFailed, "the Dropbox read failure");
        Check.Require(Text.ReadFailedOn((Side)7) == TextKey.ReadDropboxFailed, "an unrecognized side reads as Dropbox");

        Check.Require(EffectKinds.Deleted(Side.Local) == EffectKind.DeletedLocal, "the local delete effect");
        Check.Require(EffectKinds.Deleted(Side.Dropbox) == EffectKind.DeletedDropbox, "the Dropbox delete effect");
        Check.Require(EffectKinds.Deleted((Side)7) == EffectKind.DeletedDropbox, "an unrecognized side deletes as Dropbox");
        Check.Require(EffectKinds.Moved(Side.Local) == EffectKind.MovedLocal, "the local move effect");
        Check.Require(EffectKinds.Moved(Side.Dropbox) == EffectKind.MovedDropbox, "the Dropbox move effect");
        Check.Require(EffectKinds.Moved((Side)7) == EffectKind.MovedDropbox, "an unrecognized side moves as Dropbox");
    }

    // The effect-event fields are unchanged and the recurring names cost no extra allocation.
    private static void BuildsEffectEvents()
    {
        var keptBoth = KeepBothEvent("label", "original");
        Check.Require(keptBoth.Kind == RunEventKind.Effect && Massdex.SyncShark.Engine.Run.RunLog.Present(keptBoth).What == nameof(EffectKind.KeptBoth)
            && keptBoth.Path == "label" && keptBoth.Detail == "original" && Massdex.SyncShark.Engine.Run.RunLog.Present(keptBoth).Outcome == nameof(Side.Dropbox),
            "a keep-both effect event keeps its compile-time fields");
        var moved = RunEvent.Effect(EffectKind.MovedLocal, "to.txt", "from.txt", Side.Local);
        Check.Require(moved.Kind == RunEventKind.Effect && Massdex.SyncShark.Engine.Run.RunLog.Present(moved).What == nameof(EffectKind.MovedLocal)
            && Massdex.SyncShark.Engine.Run.RunLog.Present(moved).Outcome == nameof(Side.Local), "a moved effect event names its kind and side");
        var stateOnly = RunEvent.Effect(EffectKind.Recorded, "a.txt", "");
        Check.Require(stateOnly is { Kind: RunEventKind.Effect, Path: "a.txt", Outcome: "" },
            "a state-only effect has an empty outcome");
        var unknownSide = RunEvent.Effect(EffectKind.Downloaded, "a.txt", "", (Side)7);
        Check.Require(Massdex.SyncShark.Engine.Run.RunLog.Present(unknownSide).Outcome == ((Side)7).ToString(), "an unrecognized side keeps its ToString name");
        var otherKind = RunEvent.Effect((EffectKind)99, "a.txt", "", Side.Local);
        Check.Require(Massdex.SyncShark.Engine.Run.RunLog.Present(otherKind).What == ((EffectKind)99).ToString(), "an unrecognized kind keeps its ToString name");

        // Semantic value events must not introduce per-effect allocation.
        var factoryBytes = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 100; i++)
                last = RunEvent.Effect(EffectKind.KeptBoth, "label", "original", Side.Dropbox);
        }, 1, 0);
        var constructorBytes = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 100; i++)
                last = new RunEvent(RunEventKind.Effect, nameof(EffectKind.KeptBoth), "label", "original",
                    nameof(Side.Dropbox));
        }, 1, 0);
        Check.Require(last is not null && factoryBytes == constructorBytes,
            "an effect event allocates the same bytes as the direct constructor (" + factoryBytes + " vs "
            + constructorBytes + ")");
    }

    // The JIT probe's constant keep-both site: its disassembly must show no factory call and no name formatting.
    private static RunEvent KeepBothEvent(string label, string original) =>
        RunEvent.Effect(EffectKind.KeptBoth, label, original, Side.Dropbox);

    private static RunEvent? last;

    // Supplied facts only; corrected listed-or-saved scope; neutral reused by reference.
    private static void EvaluatesRowContext()
    {
        var pair = new PairConfig("pair", "pair",
            Path.Combine(Path.GetTempPath(), nameof(VocabularyChecks), "local"), "dropbox", "connection",
            ["FolderName:node_modules"],
            [new Exclusion(Side.Local, "Excluded", true), new Exclusion(Side.Dropbox, "Hidden", false)],
            FirstRunPolicy.NewestWins, true, null);
        var inner = pair with
        {
            PairId = "inner", Name = "inner",
            LocalRoot = Path.Combine(pair.LocalRoot, "inner"), DropboxRoot = "dropbox/inner"
        };
        var scope = new PathScope(pair, [pair, inner]);
        var neutral = new RowContext(false, false, false, null, false, true, true, false);

        var plain = Row("plain.txt", local: File("plain.txt"));
        Check.Require(ReferenceEquals(RowContext.ForRow(plain, scope, neutral, skipped: false,
            heldKind: null, underFolderDelete: null, deletionEligible: false, firstRunDeletion: IssueSubject.FirstRunDeletionKind.None), neutral), "an ordinary row reuses the neutral context by reference");
        Check.Require(RowContext.ForRow(Row("plain.txt", synced: Synced("plain.txt")), scope, neutral, false,
            null, null, deletionEligible: false, firstRunDeletion: IssueSubject.FirstRunDeletionKind.None).Destination is null, "an ordinary row keeps Destination null");
        var eligible = RowContext.ForRow(plain, scope, neutral, skipped: false, heldKind: null,
            underFolderDelete: null, deletionEligible: true, firstRunDeletion: IssueSubject.FirstRunDeletionKind.None);
        Check.Require(!ReferenceEquals(eligible, neutral)
            && eligible is { DeletionEligible: true, Skipped: false, Held: false, FirstRun: true,
                CompleteInventory: true } && eligible.Destination is null,
            "a deletion-eligible row carries its own eligible context");

        foreach (var kind in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        {
            var retained = RowContext.ForRow(plain, scope, neutral, skipped: false, heldKind: null,
                underFolderDelete: null, deletionEligible: false, firstRunDeletion: kind);
            Check.Require(ReferenceEquals(retained, neutral) == (kind == IssueSubject.FirstRunDeletionKind.None) &&
                retained.FirstRunDeletion == kind && !retained.DeletionEligible &&
                retained.FirstRun == neutral.FirstRun && retained.CompleteInventory == neutral.CompleteInventory,
                nameof(RowContext.FirstRunDeletion));
            var exceptional = RowContext.ForRow(plain, scope, neutral, skipped: true,
                heldKind: IssueKind.Failure, underFolderDelete: Side.Local, deletionEligible: false,
                firstRunDeletion: kind);
            Check.Require(exceptional.FirstRunDeletion == kind && exceptional.Skipped && exceptional.Held &&
                exceptional.HeldKind == IssueKind.Failure && exceptional.UnderFolderDelete == Side.Local,
                nameof(RowContext.FirstRunDeletion));
        }

        OutOfScope(scope, neutral, Row("Excluded/a.txt", local: File("Excluded/a.txt")), true,
            "a listed local exclusion");
        OutOfScope(scope, neutral, Row("Excluded/a.txt", synced: Synced("Excluded/a.txt")), true,
            "a local exclusion on a saved-only row");
        OutOfScope(scope, neutral, Row("Hidden/a.txt", dropbox: File("Hidden/a.txt")), true,
            "a listed Dropbox exclusion");
        OutOfScope(scope, neutral, Row("Hidden/a.txt", synced: Synced("Hidden/a.txt")), true,
            "a Dropbox exclusion on a saved-only row");
        OutOfScope(scope, neutral, Row("inner/file.txt", local: File("inner/file.txt")), true,
            "a nested pair's listed root");
        OutOfScope(scope, neutral, Row("inner/file.txt", synced: Synced("inner/file.txt")), true,
            "a nested pair's saved root");
        OutOfScope(scope, neutral, Row("plain.txt", local: File("plain.txt"),
            synced: new SyncedEntry(PathNames.Key("Excluded/a.txt"), "Excluded/a.txt",
                PathNames.DropboxKey("plain.txt"), "plain.txt", false, "", 0, 0, "", "")), false,
            "a listed path wins over a saved path in scope");
        OutOfScope(scope, neutral, Row("ExcludedSibling/a.txt", local: File("ExcludedSibling/a.txt")), false,
            "an exclusion ends at a segment boundary");
        // The folder bit falls back to the saved record, and a listed file's own kind wins over the saved kind.
        OutOfScope(scope, neutral, Row("node_modules", synced: Synced("node_modules", isFolder: true)), true,
            "a saved folder keeps the ignored folder name out of scope");
        OutOfScope(scope, neutral, Row("node_modules", local: File("node_modules"),
            synced: Synced("node_modules", isFolder: true)), false,
            "a listed file's kind wins over the saved folder kind");

        var skipped = RowContext.ForRow(plain, scope, neutral, skipped: true, heldKind: null, underFolderDelete: null, deletionEligible: false, firstRunDeletion: IssueSubject.FirstRunDeletionKind.None);
        Check.Require(skipped is { Skipped: true, OutOfScope: false, Held: false, UnderFolderDelete: null,
            HoldsNestedRoot: false, FirstRun: true, CompleteInventory: true } && skipped.Destination is null,
            "a skipped row keeps the run facts");
        var held = RowContext.ForRow(plain, scope, neutral, skipped: false, heldKind: IssueKind.NameClash,
            underFolderDelete: null, deletionEligible: false, firstRunDeletion: IssueSubject.FirstRunDeletionKind.None);
        Check.Require(held is { Held: true, HeldKind: IssueKind.NameClash, Skipped: false },
            "a held row carries the supplied kind");
        var deleting = RowContext.ForRow(plain, scope, neutral, skipped: false, heldKind: null,
            underFolderDelete: Side.Local, deletionEligible: false, firstRunDeletion: IssueSubject.FirstRunDeletionKind.None);
        Check.Require(deleting is { UnderFolderDelete: Side.Local, HoldsNestedRoot: false },
            "a deleting row without a nested root is not protected");
        var nested = RowContext.ForRow(Row("inner", local: Folder("inner")), scope, neutral, skipped: false,
            heldKind: null, underFolderDelete: Side.Local, deletionEligible: false, firstRunDeletion: IssueSubject.FirstRunDeletionKind.None);
        Check.Require(nested is { UnderFolderDelete: Side.Local, HoldsNestedRoot: true, OutOfScope: true },
            "a listed nested root under a folder delete is protected");
        var savedNested = RowContext.ForRow(Row("plain", synced: Synced("inner", isFolder: true)), scope, neutral,
            skipped: false, heldKind: null, underFolderDelete: Side.Dropbox, deletionEligible: false, firstRunDeletion: IssueSubject.FirstRunDeletionKind.None);
        Check.Require(savedNested is { UnderFolderDelete: Side.Dropbox, HoldsNestedRoot: true },
            "the saved path protects a Dropbox nested root when the row has no listed Dropbox entry");
    }

    private static void OutOfScope(PathScope scope, RowContext neutral, Row row, bool expected, string what) =>
        Check.Require(RowContext.ForRow(row, scope, neutral, skipped: false, heldKind: null,
            underFolderDelete: null, deletionEligible: false, firstRunDeletion: IssueSubject.FirstRunDeletionKind.None).OutOfScope == expected, "OutOfScope: " + what);

    private static Row Row(string key, Entry? local = null, Entry? dropbox = null, SyncedEntry? synced = null) =>
        new(key, local, dropbox, synced, [], null,
            new RowContext(false, false, false, null, false, false, false, false));

    private static SyncedEntry Synced(string path, bool isFolder = false) =>
        new(PathNames.Key(path), path, PathNames.DropboxKey(path), path, isFolder, "", 0, 0, "", "");

    private static Entry File(string path) => new(path, false, 0, default, "", "", "", null);

    private static Entry Folder(string path) => new(path, true, 0, default, "", "", "", null);
}
