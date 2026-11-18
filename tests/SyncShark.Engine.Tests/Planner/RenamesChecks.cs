using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks for Renames (`--engine-checks renames`), written by Renames's writer.</summary>
public static class RenamesChecks
{
    private const string Suffix = ".syncshark-case";
    private static readonly DateTimeOffset ComparedAt = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Middle = Old.AddHours(1);
    private static readonly DateTimeOffset Newer = Old.AddHours(2);

    internal static RenameCandidate? CandidateSink;
    internal static bool BoolSink;

    public static void Run()
    {
        Decisions();
        LocalAliases();
        DestinationDispositions();
        SavedSpellingDrift();
        CandidateSelection();
        LocalHashRequirement();
        AllocationFree();
    }

    private static void Decisions()
    {
        // Row 1: finish an interrupted Dropbox case-only rename at the current local spelling.
        var saved = Saved("a.txt", "a.txt");
        var intermediate = new[] { new RenameCandidate(Side.Dropbox, At("A.txt" + Suffix, id: "did"), saved) };
        var finish = Decide(intermediate, At("A.txt"));
        Check.Require(finish.Kind == RenameKind.FinishCaseRename && finish.Target == "A.txt",
            "FinishCaseRename uses the current local spelling");
        Check.Require(Decide(intermediate, observations: Facts(carries: true, skipped: true)).Kind
            == RenameKind.FinishCaseRename, "FinishCaseRename is decided before the later rows");
        Check.Require(Decide(intermediate).Target == "a.txt",
            "FinishCaseRename without a local entry returns to the saved path");
        Check.Require(Decide(intermediate, At("b.txt")).Target == "a.txt",
            "FinishCaseRename with a local entry no longer case-only returns to the saved path");

        Check.Require(Decide(intermediate, At("a.txt")).Target == "a.txt",
            "FinishCaseRename with a local entry at the saved spelling keeps that spelling");

        // Rows 2-4: carries another pair, skipped/held, out of scope.
        var moved = new[] { new RenameCandidate(Side.Local, At("b.txt"), saved) };
        var carries = Decide(moved, observations: Facts(dropbox: At("a.txt"), carries: true));
        Check.Require(carries.Kind == RenameKind.Notice && carries.NoticeKind == IssueKind.Failure
            && carries.Cause == TextKey.FailCarriesOtherPair, "CarriesOtherPair notices and holds");
        foreach (var (skipped, held) in new[] { (true, false), (false, true) })
        {
            var hold = Decide(moved, observations: Facts(dropbox: At("a.txt"), skipped: skipped, held: held));
            Check.Require(hold.Kind == RenameKind.Hold, "a skipped or held old/new path holds");
        }

        var left = Decide(moved, observations: Facts(dropbox: At("a.txt"), outOfScope: true));
        Check.Require(left.Kind == RenameKind.Leave, "a move out of scope leaves the mapping");

        // Row 5: competing case-only changes.
        var caseGroup = new[]
        {
            new RenameCandidate(Side.Local, At("A.txt", Newer), saved),
            new RenameCandidate(Side.Dropbox, At("a.TXT", Middle, id: "did"), saved)
        };
        var newest = Decide(caseGroup, observations: Facts(localDestination: "a.TXT", dropboxDestination: "A.txt"));
        Check.Require(newest.Kind == RenameKind.Follow && newest.MovedSide == Side.Local
            && newest.Target == "A.txt", "competing case changes follow the newest");
        var dropboxNewest = Decide(new[]
        {
            new RenameCandidate(Side.Local, At("A.txt", Middle), saved),
            new RenameCandidate(Side.Dropbox, At("a.TXT", Newer, id: "did"), saved)
        }, observations: Facts(localDestination: "a.TXT", dropboxDestination: "A.txt"));
        Check.Require(dropboxNewest.Kind == RenameKind.Follow && dropboxNewest.MovedSide == Side.Dropbox
            && dropboxNewest.Target == "a.TXT", "the newer Dropbox case change wins");
        var equal = Decide(new[]
        {
            new RenameCandidate(Side.Local, At("A.txt", Newer), saved),
            new RenameCandidate(Side.Dropbox, At("a.TXT", Newer.AddMilliseconds(500), id: "did"), saved)
        });
        Check.Require(equal.Kind == RenameKind.Notice && equal.NoticeKind == IssueKind.Tie
            && equal.Cause == TextKey.TieCaseBoth, "equal case-change times tie");
        var unreliable = Decide(new[]
        {
            new RenameCandidate(Side.Local, At("A.txt", ComparedAt.AddMinutes(1)), saved),
            new RenameCandidate(Side.Dropbox, At("a.TXT", Middle, id: "did"), saved)
        });
        Check.Require(unreliable.Kind == RenameKind.Notice && unreliable.Cause == TextKey.TieCaseBoth,
            "an unreliable case-change time ties");
        var folderSaved = Saved("dir", "dir", folder: true, localSize: 0);
        var folders = Decide(new[]
        {
            new RenameCandidate(Side.Local, At("Dir", Newer, size: 0, folder: true), folderSaved),
            new RenameCandidate(Side.Dropbox, At("DIR", Middle, size: 0, folder: true, id: "did"), folderSaved)
        });
        Check.Require(folders.Kind == RenameKind.Notice && folders.Cause == TextKey.TieCaseBoth,
            "competing folder case changes tie");

        foreach (var winner in new[] { Side.Local, Side.Dropbox })
        foreach (var competing in new[] { false, true })
        {
            var mapped = Saved("A_B/a.txt", "a:b/a.txt");
            var candidates = new[]
            {
                new RenameCandidate(Side.Local, At("A_B/A.txt", winner == Side.Local ? Newer : Middle), mapped),
                new RenameCandidate(Side.Dropbox, At("a:b/a.TXT", winner == Side.Dropbox ? Newer : Middle, id: "did"), mapped)
            };
            var decision = Decide(candidates.Where(candidate => competing || candidate.Side == winner).ToArray(),
                observations: Facts(local: competing || winner == Side.Local ? candidates[0].Moved : At(mapped.LocalPath),
                    dropbox: At(mapped.DropboxPath, id: "did"),
                    localDestination: "A_B/a.TXT", dropboxDestination: "a:b/A.txt"));
            Check.Require(decision.Kind == RenameKind.Follow && decision.MovedSide == winner &&
                decision.Target == (winner == Side.Local ? "a:b/A.txt" : "A_B/a.TXT"),
                "One-sided and competing Follow use the supplied destination on the losing side, including mapped parent spelling.");
        }

        // Rows 6-7: both sides moved.
        var sameKey = Decide(new[]
        {
            new RenameCandidate(Side.Local, At("x/b.txt"), saved),
            new RenameCandidate(Side.Dropbox, At("X/B.TXT", id: "did"), saved)
        });
        Check.Require(sameKey.Kind == RenameKind.Adopt, "both moved to the same key adopt");
        var mappedKeySaved = Saved("A_B/a.txt", "a_b/a.txt");
        var mappedAdopt = Decide(new[]
        {
            new RenameCandidate(Side.Local, At("A_B/a.txt"), mappedKeySaved),
            new RenameCandidate(Side.Dropbox, At("a_b/A.TXT", id: "did"), mappedKeySaved)
        });
        Check.Require(mappedAdopt.Kind == RenameKind.Adopt,
            "both moved to the same key adopt under the frozen spelling");
        var unicodeSaved = Saved("café.txt", "café.txt");
        var unicodeAdopt = Decide(new[]
        {
            new RenameCandidate(Side.Local, At("café.txt"), unicodeSaved),
            new RenameCandidate(Side.Dropbox, At("CAFÉ.TXT", id: "did"), unicodeSaved)
        });
        Check.Require(unicodeAdopt.Kind == RenameKind.Adopt,
            "both moved to the same path under OrdinalIgnoreCase adopt");
        var splitTwice = Decide(new[]
        {
            new RenameCandidate(Side.Local, At("x.txt"), saved),
            new RenameCandidate(Side.Dropbox, At("y.txt", id: "did"), saved)
        });
        Check.Require(splitTwice.Kind == RenameKind.Split && splitTwice.NoticeKind == IssueKind.Info
            && splitTwice.Cause == TextKey.InfoMovedTwice && splitTwice.MovedSide is null,
            "both moved to different places split with an Info");

        // Rows 8-10: one side moved.
        var follow = Decide(moved, observations: Facts(dropbox: At("a.txt"), dropboxDestination: "b.txt"));
        Check.Require(follow.Kind == RenameKind.Follow && follow.MovedSide == Side.Local
            && follow.Target == "b.txt", "an unchanged counterpart follows the move");
        var mappedSaved = Saved("dir/a_b.txt", "dir/a:b.txt");
        var followMapped = Decide(
            new[] { new RenameCandidate(Side.Dropbox, At("dir/A:b.txt", id: "did"), mappedSaved) },
            observations: Facts(local: At("dir/a_b.txt"), localDestination: "dir/A_b.txt"));
        Check.Require(followMapped.Kind == RenameKind.Follow && followMapped.MovedSide == Side.Dropbox
            && followMapped.Target == "dir/A_b.txt", "a Dropbox move uses the Windows spelling locally");

        // V16 upgrade: a schema-4 saved mapped binding (Dropbox A:B, Local A_B) whose Dropbox item
        // is now observed at valid AB with the same file ID follows to AB from the current observation.
        var upgradeSaved = Saved("A_B", "A:B");
        var upgrade = Decide(
            new[] { new RenameCandidate(Side.Dropbox, At("AB", id: "did"), upgradeSaved) },
            observations: Facts(local: At("A_B"), localDestination: "AB"));
        Check.Require(upgrade is { Kind: RenameKind.Follow, MovedSide: Side.Dropbox, Target: "AB" }
            && upgrade.WrittenSide() == Side.Local && upgrade.NoticeKind is null && upgrade.Cause is null,
            "V16 upgrade follows the Dropbox rename and moves the PC copy to AB without a notice or hold");
        var editedDropbox = Decide(moved, observations: Facts(dropbox: At("a.txt", contentHash: "other")));
        Check.Require(editedDropbox.Kind == RenameKind.Split && editedDropbox.Cause == TextKey.InfoMovedEdited,
            "a changed counterpart splits with an Info");
        var editedLocal = Decide(
            new[] { new RenameCandidate(Side.Dropbox, At("b.txt", id: "did"), saved) },
            observations: Facts(local: At("a.txt", size: 11)));
        Check.Require(editedLocal.Kind == RenameKind.Split && editedLocal.Cause == TextKey.InfoMovedEdited,
            "a metadata-changed local counterpart splits with an Info");
        var touched = At("a.txt", modified: ComparedAt.AddDays(-1));
        var touchedSame = Decide(new[] { new RenameCandidate(Side.Dropbox, At("b.txt", id: "did"), saved) },
            observations: Facts(local: touched, localHash: "hash", localDestination: "b.txt"));
        Check.Require(touchedSame.Kind == RenameKind.Follow,
            "a local counterpart touched but holding the saved content still follows");
        var touchedEdited = Decide(new[] { new RenameCandidate(Side.Dropbox, At("b.txt", id: "did"), saved) },
            observations: Facts(local: touched, localHash: "other"));
        Check.Require(touchedEdited.Kind == RenameKind.Split && touchedEdited.Cause == TextKey.InfoMovedEdited,
            "a same-size local edit splits");
        var touchedUnknown = Decide(new[] { new RenameCandidate(Side.Dropbox, At("b.txt", id: "did"), saved) },
            observations: Facts(local: touched));
        Check.Require(touchedUnknown.Kind == RenameKind.Split, "an unknown local change keeps the edit");
        var deleted = Decide(moved);
        Check.Require(deleted.Kind == RenameKind.Split && deleted.Cause == TextKey.InfoMovedDeleted,
            "an absent counterpart splits with an Info");

        // A folder rename with one independently moved child: each level is its own decision.
        var folderDecision = Decide(
            new[] { new RenameCandidate(Side.Local, At("new", size: 0, folder: true), folderSaved) },
            observations: Facts(dropbox: At("old", size: 0, folder: true), dropboxDestination: "new"));
        Check.Require(folderDecision.Kind == RenameKind.Follow && folderDecision.Target == "new",
            "a folder rename follows with an unchanged counterpart folder");
        var childSaved = Saved("old/c.txt", "old/c.txt");
        var childDecision = Decide(
            new[] { new RenameCandidate(Side.Local, At("moved/c.txt"), childSaved) },
            observations: Facts(dropbox: At("old/c.txt"), dropboxDestination: "moved/c.txt"));
        Check.Require(childDecision.Kind == RenameKind.Follow && childDecision.Target == "moved/c.txt",
            "an independently moved child is decided as its own move");
    }

    private static void DestinationDispositions()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var (identity, expected) in new[] { (DestinationIdentity.Gone, RenameKind.Vacate),
            (DestinationIdentity.Moving, RenameKind.Hold), (DestinationIdentity.Here, RenameKind.Follow) })
        {
            var saved = Saved("old.txt", "old.txt");
            var moved = new RenameCandidate(side, At("new.txt", id: saved.IdOn(side)), saved);
            var facts = Facts(local: side == Side.Local ? null : At("old.txt"),
                dropbox: side == Side.Dropbox ? null : At("old.txt", id: "did"),
                localDestination: "new.txt", dropboxDestination: "new.txt") with
                { Destination = new DestinationFacts(Saved("new.txt", "new.txt"), side, identity) };
            Check.Require(Decide([moved], observations: facts).Kind == expected,
                "Destination disposition selects Vacate, prerequisite Hold or ordinary Follow on both origins.");
            if (side == Side.Local)
                Check.Require(Decide([moved], observations: facts with { LocalAtSynced = At("old.txt") }).Kind == RenameKind.Leave,
                    "Local alias rejection precedes destination vacancy decisions.");
        }
    }

    private static void LocalAliases()
    {
        var saved = Saved("original.txt", "original.txt");
        var local = new RenameCandidate(Side.Local, At("alias.txt"), saved);
        var original = At(saved.LocalPath);
        foreach (var remotePath in new string?[] { null, "alias.txt", "elsewhere.txt" })
        {
            RenameCandidate[] group = remotePath is null ? [local] :
                [local, new RenameCandidate(Side.Dropbox, At(remotePath, id: "did"), saved)];
            var facts = Facts(local: original, dropbox: At(saved.DropboxPath, id: "did"),
                dropboxDestination: local.Moved.Path);
            Check.Require(Decide(group, observations: facts).Kind == RenameKind.Leave,
                "A surviving Local alias leaves ordinary path work before Follow, Adopt or Split.");
            Check.Require(Decide(group, observations: facts with { OldOrNewHeld = true }).Kind == RenameKind.Hold,
                "Alias recognition preserves earlier holds.");
        }
        Check.Require(!Renames.IsLocalAlias(null, original) && !Renames.IsLocalAlias(local, null) &&
            !Renames.IsLocalAlias(local, original with { Id = "other" }) &&
            !Renames.IsLocalAlias(local, original with { IsFolder = true }) &&
            !Renames.IsLocalAlias(local with { Moved = local.Moved with { IsFolder = true } }, original),
            "Alias recognition excludes absent candidates, different identities and directories.");
        var caseMove = local with { Moved = original with { Path = "ORIGINAL.txt" } };
        Check.Require(!Renames.IsLocalAlias(caseMove, caseMove.Moved),
            "A saved-path read resolving to the new spelling is a case rename, not an alias.");
    }

    private static void SavedSpellingDrift()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var folder in new[] { false, true })
        {
            var saved = Saved(side == Side.Local ? "parent/a.txt" : "Parent/a.txt",
                side == Side.Dropbox ? "parent/a.txt" : "Parent/a.txt", folder);
            var moved = At("Parent/a.txt", folder: folder, id: saved.IdOn(side));
            var other = PathNames.Other(side);
            var counterpart = At("Parent/a.txt", folder: folder, id: saved.IdOn(other));
            Observations Observe(Entry entry) => Facts(local: other == Side.Local ? entry : moved,
                dropbox: other == Side.Dropbox ? entry : moved,
                localDestination: moved.Path, dropboxDestination: moved.Path);
            var candidate = Renames.Candidate(side, moved, saved)!;
            Check.Require(Decide([candidate], observations: Observe(counterpart)).Kind == RenameKind.Adopt,
                "Same-identity saved case drift adopts the already matching paths without a move.");
            Check.Require(Decide([candidate], observations: Observe(counterpart with { Size = 11,
                ContentHash = "edited" })).Kind == RenameKind.Adopt,
                "Spelling adoption leaves content reconciliation to the ordinary row, even for edited content.");
            Check.Require(Decide([candidate], observations: Observe(counterpart with { Id = "replacement" })).Kind != RenameKind.Adopt,
                "A replacement counterpart cannot be adopted as saved spelling drift.");
            Check.Require(Decide([candidate with { Moved = moved with { Id = "replacement" } }],
                observations: Observe(counterpart)).Kind != RenameKind.Adopt,
                "A replacement incoming identity cannot be adopted as saved spelling drift.");
            Check.Require(Decide([candidate], observations: Observe(counterpart with { Path = "parent/a.txt" })).Kind == RenameKind.Follow,
                "A genuine one-sided case rename still follows to the other spelling.");
        }
    }

    // Selection scans in order and keeps the nullable and required callers distinct.
    private static void CandidateSelection()
    {
        var saved = Saved("a.txt", "a.txt");
        var group = new[]
        {
            new RenameCandidate(Side.Dropbox, At("b.txt", id: "did"), saved),
            new RenameCandidate(Side.Local, At("c.txt"), saved),
            new RenameCandidate(Side.Local, At("d.txt"), saved)
        };
        Check.Require(ReferenceEquals(Renames.FirstOn(group, Side.Local), group[1]),
            "FirstOn returns the first candidate on the side");
        Check.Require(ReferenceEquals(Renames.FirstOn(group, Side.Dropbox), group[0]),
            "FirstOn returns the only candidate on the other side");
        Check.Require(Renames.FirstOn(new[] { group[1] }, Side.Dropbox) is null,
            "FirstOn returns null when the side is absent");
        Check.Require(ReferenceEquals(Renames.RequireOn(group, Side.Local), group[1]),
            "RequireOn returns the first candidate on the side");
        RequireSameFailure(
            Capture(() => Array.Empty<RenameCandidate>().First(candidate => candidate.Side == Side.Dropbox)),
            Capture(() => Renames.RequireOn(new[] { group[1] }, Side.Dropbox)),
            "RequireOn's missing side fails exactly as Enumerable.First");
        var absentSource = (IEnumerable<RenameCandidate>)null!;
        RequireSameFailure(
            Capture(() => absentSource.FirstOrDefault(candidate => candidate.Side == Side.Local)),
            Capture(() => Renames.FirstOn(null!, Side.Local)),
            "FirstOn's null source fails exactly as Enumerable.FirstOrDefault");
        RequireSameFailure(
            Capture(() => absentSource.First(candidate => candidate.Side == Side.Local)),
            Capture(() => Renames.RequireOn(null!, Side.Local)),
            "RequireOn's null source fails exactly as Enumerable.First");
        var absentGroup = (IReadOnlyList<RenameCandidate>)null!;
        RequireSameFailure(
            Capture(() => absentGroup.FirstOrDefault(candidate => candidate.Side == Side.Local)),
            Capture(() => Renames.For(null!, null, Facts(), Suffix, ComparedAt, localContentDirty: false)),
            "Renames.For's null group fails exactly as its former FirstOrDefault");
    }

    // Only a Dropbox-moved split with a same-size, metadata-only local file asks for the hash.
    private static void LocalHashRequirement()
    {
        var saved = Saved("a.txt", "a.txt");
        var splitEdited = new RenameDecision(RenameKind.Split, Side.Dropbox, NoticeKind: IssueKind.Info,
            Cause: TextKey.InfoMovedEdited);
        var metadataChanged = At("a.txt", modified: Old.AddDays(1), id: "other");
        Check.Require(Renames.NeedsLocalHash(splitEdited, saved, metadataChanged, localContentDirty: false),
            "hash requirement: a metadata-only local file asks for the hash");
        Check.Require(!Renames.NeedsLocalHash(splitEdited, saved, At("a.txt"), localContentDirty: false),
            "hash requirement: unchanged local metadata needs no hash");
        Check.Require(!Renames.NeedsLocalHash(splitEdited, saved, At("a.txt", size: 11, id: "other"), localContentDirty: false),
            "hash requirement: a size change needs no hash");
        Check.Require(!Renames.NeedsLocalHash(splitEdited, saved, At("a.txt", folder: true, id: "other"), localContentDirty: false),
            "hash requirement: a folder observation needs no hash");
        Check.Require(!Renames.NeedsLocalHash(splitEdited, Saved("a.txt", "a.txt", folder: true), metadataChanged,
                localContentDirty: false),
            "hash requirement: a saved folder needs no hash");
        Check.Require(!Renames.NeedsLocalHash(splitEdited, saved, null, localContentDirty: false),
            "hash requirement: an absent local observation needs no hash");
        Check.Require(!Renames.NeedsLocalHash(
            new RenameDecision(RenameKind.Split, Side.Local, Cause: TextKey.InfoMovedEdited), saved, metadataChanged,
            localContentDirty: false),
            "hash requirement: a Local-moved split needs no local hash");
        Check.Require(!Renames.NeedsLocalHash(
            new RenameDecision(RenameKind.Split, Side.Dropbox, Cause: TextKey.InfoMovedDeleted), saved, metadataChanged,
            localContentDirty: false),
            "hash requirement: another cause needs no hash");
        Check.Require(!Renames.NeedsLocalHash(new RenameDecision(RenameKind.Adopt), saved, metadataChanged,
                localContentDirty: false),
            "hash requirement: a non-split needs no hash");
    }

    // Measured per-site evidence: selection and the hash requirement allocate no more than their inline forms.
    private static void AllocationFree()
    {
        var saved = Saved("a.txt", "a.txt");
        var group = new[]
        {
            new RenameCandidate(Side.Dropbox, At("b.txt", id: "did"), saved),
            new RenameCandidate(Side.Local, At("c.txt"), saved)
        };
        var decision = new RenameDecision(RenameKind.Split, Side.Dropbox, Cause: TextKey.InfoMovedEdited);
        // The saved size matches so LocalChanged reaches the stamp comparison; the probe requires its unknown answer.
        var local = At("a.txt", modified: Old.AddDays(1), id: "other");
        Check.Require(Renames.NeedsLocalHash(decision, saved, local, localContentDirty: false),
            "cost probe: the metadata-only local entry reaches the hash requirement");

        var firstOwner = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                Sink(Renames.FirstOn(group, Side.Local));
                Sink(Renames.FirstOn(group, Side.Dropbox));
            }
        });
        var firstInline = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                RenameCandidate? localFirst = null;
                RenameCandidate? dropboxFirst = null;
                for (var j = 0; j < group.Length; j++)
                {
                    var candidate = group[j];
                    if (candidate.Side == Side.Local) localFirst ??= candidate;
                    if (candidate.Side == Side.Dropbox) dropboxFirst ??= candidate;
                }
                Sink(localFirst);
                Sink(dropboxFirst);
            }
        });
        Check.Require(firstOwner == firstInline,
            $"Renames.FirstOn allocates no more than the inline scan ({firstOwner} B vs {firstInline} B).");

        var needsOwner = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(Renames.NeedsLocalHash(decision, saved, local, localContentDirty: false));
        });
        var needsInline = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
                Sink(decision.Kind == RenameKind.Split && decision.Cause == TextKey.InfoMovedEdited
                    && decision.MovedSide == Side.Dropbox && local is { IsFolder: false }
                    && Massdex.SyncShark.Engine.Planner.Decide.LocalChanged(local, saved, null, localContentDirty: false) is null);
        });
        Check.Require(needsOwner == needsInline,
            $"Renames.NeedsLocalHash allocates no more than its inline expression ({needsOwner} B vs {needsInline} B).");
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            return error;
        }
        throw new InvalidOperationException("the action was expected to fail");
    }

    private static void RequireSameFailure(Exception expected, Exception actual, string what) =>
        Check.Require(expected.GetType() == actual.GetType() && expected.Message == actual.Message
            && (expected as ArgumentException)?.ParamName == (actual as ArgumentException)?.ParamName,
            what + " (" + actual.GetType().Name + ": " + actual.Message + ")");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(RenameCandidate? value) => CandidateSink = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(bool value) => BoolSink = value;

    private static RenameDecision Decide(IReadOnlyList<RenameCandidate> group, Entry? localById = null,
        Observations? observations = null) =>
        Renames.For(group, localById, observations ?? Facts(), Suffix, ComparedAt, localContentDirty: false);

    private static SyncedEntry Saved(string localPath, string dropboxPath, bool folder = false, long localSize = 10,
        DateTimeOffset? localModified = null, string dropboxHash = "hash", string localId = "lid",
        string dropboxId = "did") =>
        new(PathNames.Key(localPath), localPath, PathNames.DropboxKey(dropboxPath), dropboxPath, folder,
            localId, localSize, (localModified ?? Old).UtcTicks, dropboxId, dropboxHash);

    private static Entry At(string path, DateTimeOffset? modified = null, long size = 10, bool folder = false,
        string id = "lid", string contentHash = "hash") =>
        new(path, folder, size, modified ?? Old, id, "rev", contentHash, null);

    private static Observations Facts(Entry? local = null, Entry? dropbox = null, bool skipped = false,
        bool held = false, bool outOfScope = false, bool carries = false, string? localHash = null,
        string? localDestination = null, string? dropboxDestination = null) =>
        new(local, dropbox, skipped, held, outOfScope, carries, localHash, localDestination, dropboxDestination);
}
