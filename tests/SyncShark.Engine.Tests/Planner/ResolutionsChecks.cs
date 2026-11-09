using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks for Resolutions (`--engine-checks resolutions`), written by Resolutions's writer.</summary>
public static class ResolutionsChecks
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly RowContext NoContext = new(false, false, false, null, false, false, false, false);

    public static void Run()
    {
        foreach (var choice in Enum.GetValues<ResolutionChoice>())
            Check.Require(Resolutions.Allows(IssueKind.RootIdentity, TextKey.RootUnavailable, choice) ==
                (choice is ResolutionChoice.BindRoots or ResolutionChoice.Ignore or ResolutionChoice.Unignore), "root identity offers explicit binding and visibility choices");
        foreach (var kind in Enum.GetValues<IssueKind>())
            Check.Require(Resolutions.Allows(kind, TextKey.RootUnavailable, ResolutionChoice.BindRoots) ==
                (kind == IssueKind.RootIdentity), "binding belongs only to root identity notices");
        foreach (var kind in Enum.GetValues<IssueKind>())
        foreach (var choice in new[] { ResolutionChoice.Ignore, ResolutionChoice.Unignore })
        {
            var issue = new Issue(1, kind, TextKey.FailChanged, "a", new IssueSubject([], null), T0, false);
            Check.Require(Resolutions.For(issue, choice).Count == 0, "every kind accepts row-free visibility choices");
            Check.Require(Resolutions.RequestValid(true, false, choice, false, false, null) &&
                !Resolutions.RequestValid(true, false, choice, true, false, null) &&
                !Resolutions.RequestValid(true, false, choice, false, true, null) &&
                !Resolutions.RequestValid(true, false, choice, false, false, "rule") &&
                !Resolutions.RequestValid(false, true, choice, false, false, null), "visibility request shape is exact");
        }
        foreach (var kind in new[] { IssueKind.DriveChange, IssueKind.RootIdentity })
        {
            var issue = new Issue(1, kind, TextKey.FailChanged, "", new IssueSubject([], null), T0, false);
            Check.Require(Massdex.SyncShark.Engine.Edge.IssuePresentation.ResolutionConfirmation(issue, ResolutionChoice.Ignore)
                .Contains(Text.Get(TextKey.ResolutionIgnorePairConsequence), StringComparison.Ordinal),
                "pair-blocking Ignore confirmation describes the retained stop");
        }
        foreach (var cause in new[] { TextKey.FailFolderDependencyCycle, TextKey.FailChanged })
        {
            var issue = new Issue(1, IssueKind.Failure, cause, "Parent/Child", new IssueSubject([], null), T0, false);
            var confirmation = Massdex.SyncShark.Engine.Edge.IssuePresentation.ResolutionConfirmation(issue, ResolutionChoice.Retry);
            Check.Require(confirmation.Contains(Text.Get(TextKey.ResolutionRetryRepairConsequence), StringComparison.Ordinal) ==
                (cause == TextKey.FailFolderDependencyCycle), "only cycle Retry confirms a whole-pair Repair through the shared CLI/UI presentation owner");
        }
        ContentTies();
        CaseTie();
        KindTie();
        ClashWithIncoming();
        ClashWithoutIncoming();
        ClashDifferentKinds();
        RefusedRename();
        SameSideCandidates();
        SameSideReplacements();
        MoveFollowUp();
        Dismissals();
        KeepFolderAlways();
        Rejections();
        FolderFollowUp();
        FirstObservedCandidate();
        SingleRowFacts();
        CostProbe();
        Stale();
    }

    private static void FolderFollowUp()
    {
        var localFolder = Folder("a", "lf");
        var localFile = File("a.txt", "lf");
        var dropboxFolder = Folder("a", "df");
        var dropboxFile = File("a.txt", "df");
        var previousEntry = File("prev", "dp");
        var previous = ((Side Side, Entry Entry)?)(Side.Dropbox, previousEntry);

        RequireSource(Resolutions.FolderToSync(
            new SyncAction(ActionKind.EnsureDropboxFolder, RowOf(localFolder, null)), null),
            Side.Local, localFolder, "an ensured Dropbox folder follows its local folder source");
        RequireSource(Resolutions.FolderToSync(
            new SyncAction(ActionKind.EnsureLocalFolder, RowOf(null, dropboxFolder)), null),
            Side.Dropbox, dropboxFolder, "an ensured local folder follows its Dropbox folder source");
        RequireSource(Resolutions.FolderToSync(
            new SyncAction(ActionKind.ReplaceKind, RowOf(localFolder, null), Side: Side.Dropbox), null),
            Side.Local, localFolder, "a Dropbox-side replace follows the local folder source");
        RequireSource(Resolutions.FolderToSync(
            new SyncAction(ActionKind.ReplaceKind, RowOf(null, dropboxFolder), Side: Side.Local), previous),
            Side.Dropbox, dropboxFolder, "a local-side replace follows the Dropbox folder source");

        RequireNoSource(Resolutions.FolderToSync(
            new SyncAction(ActionKind.EnsureDropboxFolder, RowOf(localFile, null)), previous),
            "a recognized nonfolder source resets the follow-up even when a previous value exists");
        RequireNoSource(Resolutions.FolderToSync(
            new SyncAction(ActionKind.EnsureDropboxFolder, RowOf(null, dropboxFolder)), null),
            "an ensured Dropbox folder reads the local source, not the Dropbox entry");
        RequireNoSource(Resolutions.FolderToSync(
            new SyncAction(ActionKind.EnsureLocalFolder, RowOf(null, dropboxFile)), null),
            "a nonfolder local follow-up source is reset to null");
        RequireNoSource(Resolutions.FolderToSync(
            new SyncAction(ActionKind.ReplaceKind, RowOf(localFile, null), Side: Side.Dropbox), null),
            "a nonfolder replace source is reset to null");

        RequireSource(Resolutions.FolderToSync(
            new SyncAction(ActionKind.ReplaceKind, RowOf(null, dropboxFolder)), null),
            Side.Dropbox, dropboxFolder, "a replace with no explicit side keeps the Dropbox-source fallback");
        RequireSource(Resolutions.FolderToSync(new SyncAction(ActionKind.Upload, RowOf(null, null)), previous),
            Side.Dropbox, previousEntry, "an unrecognized action preserves the previous follow-up");
    }

    private static void RequireSource((Side Side, Entry Entry)? source, Side side, Entry entry, string what) =>
        Check.Require(source is { } value && value.Side == side && ReferenceEquals(value.Entry, entry), what);

    private static void RequireNoSource((Side Side, Entry Entry)? source, string what) =>
        Check.Require(source is null, what);

    // The identity match is exact (path and id) and the first current observation on that side wins. The second local
    // observation the resolution runner keeps in the Dropbox row is used only when the local row's own entry misses,
    // and the Dropbox row's extras win over identical extras in the local row.
    private static void FirstObservedCandidate()
    {
        var local = Folder("x", "l1");
        var shadow = File("x", "l1");
        var occupant = File("x", "d1");
        var localRow = RowOf(local, null);
        var dropboxRow = RowOf(shadow, occupant);
        var issue = IssueAt(IssueKind.NameClash, TextKey.ClashNameTaken, Side.Local, "x",
            new(Side.Local, local.Path, local.Id), new(Side.Dropbox, occupant.Path, occupant.Id));
        var action = Single(Resolutions.For(issue, ResolutionChoice.RenameIncoming, "y", localRow, dropboxRow));
        Check.Require(ReferenceEquals(action.Row.Local, local),
            "the local row's observation is selected before the Dropbox row's second local observation");

        var primary = File("x", "d1");
        var second = Folder("x", "d1");
        var localOther = File("x", "l1");
        var localRow2 = RowOf(localOther, second);
        var dropboxRow2 = RowOf(null, primary);
        var issue2 = IssueAt(IssueKind.NameClash, TextKey.ClashNameTaken, Side.Dropbox, "x",
            new(Side.Dropbox, primary.Path, primary.Id), new(Side.Local, localOther.Path, localOther.Id));
        var action2 = Single(Resolutions.For(issue2, ResolutionChoice.RenameIncoming, "y", localRow2, dropboxRow2));
        Check.Require(ReferenceEquals(action2.Row.Dropbox, primary),
            "the Dropbox row's observation is selected before the local row's Dropbox entry");

        // The local row's entry does not match the subject, so only the Dropbox row's second local observation can.
        var unrelated = File("other", "lo");
        var onlySecond = File("x", "l2");
        var occupant3 = File("x", "d1");
        var localRow3 = RowOf(unrelated, null);
        var dropboxRow3 = RowOf(onlySecond, occupant3);
        var issue3 = IssueAt(IssueKind.NameClash, TextKey.ClashNameTaken, Side.Local, "x",
            new(Side.Local, onlySecond.Path, onlySecond.Id), new(Side.Dropbox, occupant3.Path, occupant3.Id));
        var action3 = Single(Resolutions.For(issue3, ResolutionChoice.RenameIncoming, "y", localRow3, dropboxRow3));
        Check.Require(ReferenceEquals(action3.Row.Local, onlySecond),
            "the Dropbox row's second local observation matches when the local row's entry does not");

        // Identical Path+Id extras in both lists: the Dropbox row's extra is enumerated first.
        var localPrimary = File("q", "l1");
        var dropboxPrimary = File("z", "d1");
        var extraFromDropboxRow = File("e", "x1");
        var extraFromLocalRow = Folder("e", "x1");
        var localRow4 = new Row("K", localPrimary, null, null, new[] { extraFromLocalRow }, null, NoContext);
        var dropboxRow4 = new Row("K", null, dropboxPrimary, null, new[] { extraFromDropboxRow }, null, NoContext);
        var issue4 = IssueAt(IssueKind.NameClash, TextKey.ClashNameTaken, null, "e",
            new(Side.Local, localPrimary.Path, localPrimary.Id),
            new(Side.Dropbox, extraFromDropboxRow.Path, extraFromDropboxRow.Id));
        var action4 = Single(Resolutions.For(issue4, ResolutionChoice.RenameDropbox, "renamed", localRow4,
            dropboxRow4));
        Check.Require(ReferenceEquals(action4.Row.Dropbox, extraFromDropboxRow),
            "the Dropbox row's extra is selected before the local row's identical extra");
    }

    // A one-sided row keeps the source key, local hash and context and clears Synced/Extra without an intermediate copy.
    private static void SingleRowFacts()
    {
        var local = File("a.txt", "l1");
        var localExtra = File("x1", "le");
        var dropbox = File("d.txt", "d1");
        var dropboxExtra = File("x2", "de");
        var localSynced = new SyncedEntry(PathNames.Key("a.txt"), "a.txt", PathNames.DropboxKey("a.txt"), "a.txt",
            false, "l1", 1, T0.UtcTicks, "d1", "h1");
        var dropboxSynced = new SyncedEntry(PathNames.Key("d.txt"), "d.txt", PathNames.DropboxKey("d.txt"), "d.txt",
            false, "d1", 2, T0.UtcTicks, "d2", "h2");
        var localContext = new RowContext(false, false, false, null, false, false, false, false, "local-dest");
        var dropboxContext = new RowContext(false, false, false, null, false, false, false, false, "dropbox-dest");
        var localRow = new Row("LK", local, null, localSynced, new[] { localExtra }, "lh", localContext);
        var dropboxRow = new Row("DK", null, dropbox, dropboxSynced, new[] { dropboxExtra }, "dh", dropboxContext);

        var localIssue = IssueAt(IssueKind.NameClash, TextKey.ClashNameTaken, Side.Local, "a.txt",
            new(Side.Local, local.Path, local.Id), new(Side.Dropbox, dropbox.Path, dropbox.Id));
        var localAction = Single(Resolutions.For(localIssue, ResolutionChoice.RenameIncoming, "new.txt", localRow,
            dropboxRow));
        Check.Require(localAction.Row.Key == "LK" && localAction.Row.LocalHash == "lh"
            && ReferenceEquals(localAction.Row.Context, localContext) && ReferenceEquals(localAction.Row.Local, local)
            && localAction.Row.Synced is null && localAction.Row.Extra.Count == 0,
            "a Local one-sided row keeps that row's key, local hash and context and clears Synced/Extra");

        var dropboxIssue = IssueAt(IssueKind.NameClash, TextKey.ClashNameTaken, Side.Dropbox, "d.txt",
            new(Side.Dropbox, dropbox.Path, dropbox.Id), new(Side.Local, local.Path, local.Id));
        var dropboxAction = Single(Resolutions.For(dropboxIssue, ResolutionChoice.RenameIncoming, "new.txt",
            localRow, dropboxRow));
        Check.Require(dropboxAction.Row.Key == "DK" && dropboxAction.Row.LocalHash == "dh"
            && ReferenceEquals(dropboxAction.Row.Context, dropboxContext)
            && ReferenceEquals(dropboxAction.Row.Dropbox, dropbox)
            && dropboxAction.Row.Synced is null && dropboxAction.Row.Extra.Count == 0,
            "a Dropbox one-sided row keeps that row's key, local hash and context and clears Synced/Extra");
    }

    // Measured evidence: the migrated one-sided construction is still exactly one Row allocation, the same as the
    // direct construction it replaced. NoInlining sinks keep both calls alive.
    private static void CostProbe()
    {
        var entry = File("a.txt", "l1");
        var source = new Row("LK", entry, null, null, Array.Empty<Entry>(), "lh", NoContext);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var baseline = AllocProbe.Measure(_ => BaselineOneSide(source, side, entry), 10_000);
            var migrated = AllocProbe.Measure(_ => MigratedOneSide(source, side, entry), 10_000);
            Console.WriteLine("  one-sided Row construction (" + side + "): baseline=" + baseline +
                " B, migrated=" + migrated + " B per 10000 calls");
            Check.Require(migrated == baseline,
                "one-sided construction keeps its single Row allocation on " + side + ": " + migrated + " B");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Row MigratedOneSide(Row source, Side side, Entry entry) =>
        Row.ForOneSide(side, source.Key, entry, null, [], source.LocalHash, source.Context);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Row BaselineOneSide(Row source, Side side, Entry entry) =>
        new(source.Key, side == Side.Local ? entry : null, side == Side.Dropbox ? entry : null,
            null, Array.Empty<Entry>(), source.LocalHash, source.Context);

    private static void ContentTies()
    {
        var (issue, row, local, dropbox) = Tie(TextKey.TieEqualTimes);

        var upload = Single(Resolutions.For(issue, ResolutionChoice.UseLocal, null, row, row));
        Check.Require(upload.Kind == ActionKind.Upload, "use-local on a content tie uploads");
        Check.Require(upload.Target == dropbox.Path && upload.Row.Local?.Id == local.Id
            && upload.Row.Dropbox?.Id == dropbox.Id, "use-local replaces the Dropbox version at its path");

        var download = Single(Resolutions.For(issue, ResolutionChoice.UseDropbox, null, row, row));
        Check.Require(download.Kind == ActionKind.Download && download.Target == local.Path
            && download.Row.Dropbox?.Id == dropbox.Id && download.Row.Local?.Id == local.Id,
            "use-dropbox downloads over the local version at its path");

        var both = Single(Resolutions.For(issue, ResolutionChoice.KeepBoth, null, row, row));
        Check.Require(both.Kind == ActionKind.KeepBoth && both.Target is null && both.Row.Local is not null
            && both.Row.Dropbox is not null, "keep-both keeps both versions");

        var unreliable = Tie(TextKey.TieUnreliableTime);
        Check.Require(Single(Resolutions.For(unreliable.Issue, ResolutionChoice.UseLocal, null, unreliable.Row,
            unreliable.Row)).Kind == ActionKind.Upload, "an unreliable-time tie also uploads on use-local");
        Check.Require(Resolutions.For(issue, ResolutionChoice.Dismiss).Count == 0,
            "dismiss on a content tie returns no actions");
    }

    private static void CaseTie()
    {
        var local = File("a.txt", "l1");
        var dropbox = File("A.txt", "d1");
        var row = RowOf(local, dropbox);
        var issue = IssueOf(IssueKind.Tie, TextKey.TieCaseBoth, null,
            new(Side.Local, local.Path, local.Id), new(Side.Dropbox, dropbox.Path, dropbox.Id));

        var dropboxMove = Single(Resolutions.For(issue, ResolutionChoice.UseLocal, null, row, row));
        Check.Require(dropboxMove.Kind == ActionKind.MoveDropbox && dropboxMove.Target == "a.txt",
            "use-local on a case tie moves the Dropbox item to the local spelling");

        var localMove = Single(Resolutions.For(issue, ResolutionChoice.UseDropbox, null, row, row));
        Check.Require(localMove.Kind == ActionKind.MoveLocal && localMove.Target == "A.txt",
            "use-dropbox on a case tie moves the local item to the Dropbox spelling");

        Check.Require(Resolutions.For(issue, ResolutionChoice.Dismiss).Count == 0,
            "dismiss on a case tie returns no actions");
    }

    private static void KindTie()
    {
        var local = Folder("a", "l1");
        var dropbox = File("a", "d1");
        var row = RowOf(local, dropbox);
        var issue = IssueOf(IssueKind.Tie, TextKey.TieKindBoth, null,
            new(Side.Local, local.Path, local.Id), new(Side.Dropbox, dropbox.Path, dropbox.Id));

        var towardLocal = Single(Resolutions.For(issue, ResolutionChoice.UseLocal, null, row, row));
        Check.Require(towardLocal.Kind == ActionKind.ReplaceKind && towardLocal.Side == Side.Dropbox
            && towardLocal.Target == dropbox.Path && towardLocal.Row.Local?.Id == local.Id,
            "use-local on a kind tie replaces the Dropbox side with the local kind");

        var towardDropbox = Single(Resolutions.For(issue, ResolutionChoice.UseDropbox, null, row, row));
        Check.Require(towardDropbox.Kind == ActionKind.ReplaceKind && towardDropbox.Side == Side.Local
            && towardDropbox.Target == local.Path && towardDropbox.Row.Dropbox?.Id == dropbox.Id,
            "use-dropbox on a kind tie replaces the local side with the Dropbox kind");

        Check.Require(Resolutions.For(issue, ResolutionChoice.Dismiss).Count == 0,
            "dismiss on a kind tie returns no actions");
    }

    private static void ClashWithIncoming()
    {
        var incoming = File("dir/a.txt", "l2");
        var occupant = File("dir/a.txt", "d2");
        var row = RowOf(incoming, occupant);
        var issue = IssueOf(IssueKind.NameClash, TextKey.ClashNameTaken, Side.Local,
            new(Side.Local, incoming.Path, incoming.Id), new(Side.Dropbox, occupant.Path, occupant.Id));

        var rename = Single(Resolutions.For(issue, ResolutionChoice.RenameIncoming, "new.txt", row, row));
        Check.Require(rename.Kind == ActionKind.MoveUnlinked && rename.Side == Side.Local
            && rename.Target == "dir/new.txt", "rename-incoming moves the incoming item and records no link");
        Check.Require(rename.Row.Synced is null, "the renamed incoming item is not linked to the old occupant");

        var replace = Resolutions.For(issue, ResolutionChoice.ReplaceExisting, null, row, row);
        Check.Require(replace.Count == 1, "replace-existing is one replacing transfer for a file");
        Check.Require(replace[0].Kind == ActionKind.Upload && replace[0].Target == occupant.Path
            && replace[0].Row.Local?.Id == incoming.Id && replace[0].Row.Dropbox?.Id == occupant.Id,
            "replace-existing uploads the incoming file over the occupant it replaces");

        var folderIncoming = Folder("dir/sub", "l3");
        var folderOccupant = Folder("dir/sub", "d3");
        var folderRow = RowOf(folderIncoming, folderOccupant);
        var folderIssue = IssueOf(IssueKind.NameClash, TextKey.ClashNameTaken, Side.Local,
            new(Side.Local, folderIncoming.Path, folderIncoming.Id),
            new(Side.Dropbox, folderOccupant.Path, folderOccupant.Id));
        var folderReplace = Resolutions.For(folderIssue, ResolutionChoice.ReplaceExisting, null, folderRow, folderRow);
        Check.Require(folderReplace.Count == 2 && folderReplace[1].Kind == ActionKind.EnsureDropboxFolder
            && folderReplace[1].Target == folderOccupant.Path,
            "a folder replace-existing ensures the folder on Dropbox; the runner then copies the descendants");

        Check.Require(Resolutions.For(issue, ResolutionChoice.Dismiss).Count == 0,
            "dismiss on a clash with an incoming side returns no actions");
    }

    private static void ClashWithoutIncoming()
    {
        var local = File("a.txt", "l1");
        var dropbox = File("a.txt", "d1");
        var row = RowOf(local, dropbox);
        var issue = IssueOf(IssueKind.NameClash, TextKey.ClashFileFolder, null,
            new(Side.Local, local.Path, local.Id), new(Side.Dropbox, dropbox.Path, dropbox.Id));

        var keepLocal = Resolutions.For(issue, ResolutionChoice.KeepLocal, null, row, row);
        Check.Require(keepLocal.Count == 1 && keepLocal[0].Kind == ActionKind.Upload
            && keepLocal[0].Target == dropbox.Path && keepLocal[0].Row.Local?.Id == local.Id
            && keepLocal[0].Row.Dropbox?.Id == dropbox.Id,
            "keep-local replaces the Dropbox candidate in one transfer");

        var keepDropbox = Resolutions.For(issue, ResolutionChoice.KeepDropbox, null, row, row);
        Check.Require(keepDropbox.Count == 1 && keepDropbox[0].Kind == ActionKind.Download
            && keepDropbox[0].Target == local.Path && keepDropbox[0].Row.Local?.Id == local.Id
            && keepDropbox[0].Row.Dropbox?.Id == dropbox.Id,
            "keep-dropbox replaces the local candidate in one transfer");

        var renameLocal = Single(Resolutions.For(issue, ResolutionChoice.RenameLocal, "b.txt", row, row));
        Check.Require(renameLocal.Kind == ActionKind.MoveUnlinked && renameLocal.Side == Side.Local
            && renameLocal.Target == "b.txt", "rename-local moves the local side");

        var renameDropbox = Single(Resolutions.For(issue, ResolutionChoice.RenameDropbox, "d.txt", row, row));
        Check.Require(renameDropbox.Kind == ActionKind.MoveUnlinked && renameDropbox.Side == Side.Dropbox
            && renameDropbox.Target == "d.txt", "rename-dropbox moves the Dropbox side");

        Check.Require(Resolutions.For(issue, ResolutionChoice.Dismiss).Count == 0,
            "dismiss on a clash without an incoming side returns no actions");
    }

    private static void ClashDifferentKinds()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var incomingFolder in new[] { false, true })
        foreach (var hasIncoming in new[] { false, true })
        {
            var incoming = incomingFolder ? Folder("item", "incoming") : File("item", "incoming");
            var occupant = incomingFolder ? File("item", "occupant") : Folder("item", "occupant");
            var row = RowOf(side == Side.Local ? incoming : occupant, side == Side.Dropbox ? incoming : occupant);
            var issue = IssueAt(IssueKind.NameClash, TextKey.ClashFileFolder, hasIncoming ? side : null, occupant.Path,
                new(side, incoming.Path, incoming.Id), new(PathNames.Other(side), occupant.Path, occupant.Id));
            var choice = hasIncoming ? ResolutionChoice.ReplaceExisting :
                side == Side.Local ? ResolutionChoice.KeepLocal : ResolutionChoice.KeepDropbox;
            var action = Single(Resolutions.For(issue, choice, null, row, row));
            Check.Require(action.Kind == ActionKind.ReplaceKind && action.Side == PathNames.Other(side) &&
                action.Target == occupant.Path && action.Row.Local == row.Local && action.Row.Dropbox == row.Dropbox &&
                action.Row.Synced is null,
                $"{choice} with incoming {side} folder={incomingFolder} replaces the exact opposite-kind occupant.");

            var renameChoice = hasIncoming ? ResolutionChoice.RenameIncoming :
                side == Side.Local ? ResolutionChoice.RenameLocal : ResolutionChoice.RenameDropbox;
            var rename = Single(Resolutions.For(issue, renameChoice, "renamed", row, row));
            Check.Require(Resolutions.PathsToSync(rename).SequenceEqual(new[]
                { (side, "renamed"), (PathNames.Other(side), occupant.Path) }),
                $"{renameChoice} follows the moved item, then the freed name on the occupant's side " +
                $"(folder={occupant.IsFolder}) so a folder's descendants can be synced.");
        }
    }

    // A refused rename (a case tie's Dropbox move onto a taken spelling): the subject is the local item, the Dropbox
    // item and the occupant at issue.Path, which the observation puts in Extra.
    private static void RefusedRename()
    {
        var local = File("a.txt", "l1");
        var moved = File("A.txt", "d1");
        var occupant = File("a.txt", "d2");
        var localRow = new Row("A.TXT", local, null, null, Array.Empty<Entry>(), null, NoContext);
        var dropboxRow = new Row("A.TXT", null, moved, null, new[] { occupant }, null, NoContext);
        var issue = IssueAt(IssueKind.NameClash, TextKey.ClashNameTaken, Side.Local, "a.txt",
            new(Side.Local, local.Path, local.Id), new(Side.Dropbox, moved.Path, moved.Id),
            new(Side.Dropbox, occupant.Path, occupant.Id));

        var replace = Single(Resolutions.For(issue, ResolutionChoice.ReplaceExisting, null, localRow, dropboxRow));
        Check.Require(replace.Kind == ActionKind.Upload && replace.Target == "a.txt"
            && replace.Row.Local?.Id == local.Id && replace.Row.Dropbox?.Id == occupant.Id,
            "a refused rename replaces the occupant at issue.Path, not the primary Dropbox entry");
    }

    private static void SameSideCandidates()
    {
        var atIssue = File("a:b", "d1");
        var sibling = File("a?b", "d2");
        foreach (var incoming in new[] { false, true })
        foreach (var reverse in new[] { false, true })
        {
            var entries = reverse ? new[] { sibling, atIssue } : new[] { atIssue, sibling };
            var row = RowOf(null, entries[0]) with { Extra = entries[1..] };
            var issue = IssueAt(IssueKind.NameClash,
                incoming ? TextKey.ClashNameTaken : TextKey.ClashMappedNames,
                incoming ? Side.Dropbox : null, atIssue.Path,
                entries.Select(entry => new IssueItem(Side.Dropbox, entry.Path, entry.Id)).ToArray());
            var action = Single(Resolutions.For(issue,
                incoming ? ResolutionChoice.RenameIncoming : ResolutionChoice.RenameDropbox, "renamed", row, row));
            Check.Require(action.Kind == ActionKind.MoveUnlinked && action.Side == Side.Dropbox &&
                action.Target == "renamed" && action.Row.Dropbox == (incoming ? sibling : atIssue),
                "Only an incoming rename selects away from issue.Path; no-incoming selects at it, independent of ordering.");
            Check.Require(Resolutions.PathsToSync(action).SequenceEqual(new[]
                { (Side.Dropbox, "renamed"), (Side.Local, incoming ? sibling.Path : atIssue.Path) }),
                "mapped-name follow-ups preserve the selected exact Dropbox identity regardless of subject order");
            if (incoming)
                Check.Require(Fails(() => Resolutions.For(issue, ResolutionChoice.ReplaceExisting, null, row, row))
                    == TextKey.ResolveSameSideUnsupported,
                    "mapped siblings are a same-side replacement refusal, not a stale subject");
        }
    }

    private static void SameSideReplacements()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var folder in new[] { false, true })
        foreach (var occupantFolder in new[] { false, true })
        foreach (var reverse in new[] { false, true })
        {
            var incoming = folder ? Folder("Parent/incoming", "incoming") : File("Parent/incoming", "incoming");
            var occupant = occupantFolder ? Folder("Parent/taken", "occupant") : File("Parent/taken", "occupant");
            var entries = reverse ? new[] { occupant, incoming } : new[] { incoming, occupant };
            var localRow = side == Side.Local ? RowOf(entries[0], null) : RowOf(null, entries[0]);
            var dropboxRow = side == Side.Local ? RowOf(entries[1], null) :
                RowOf(null, entries[0]) with { Extra = [entries[1]] };
            var issue = IssueAt(IssueKind.NameClash, TextKey.ClashNameTaken, side, occupant.Path,
                entries.Select(entry => new IssueItem(side, entry.Path, entry.Id)).ToArray());

            Check.Require(Fails(() => Resolutions.For(issue, ResolutionChoice.ReplaceExisting, null,
                localRow, dropboxRow)) == TextKey.ResolveSameSideUnsupported,
                "an exact same-side replacement is unsupported, not stale, regardless of kind or subject order");
            var rename = Single(Resolutions.For(issue, ResolutionChoice.RenameIncoming, "renamed",
                localRow, dropboxRow));
            Check.Require(rename.Kind == ActionKind.MoveUnlinked && rename.Side == side &&
                ReferenceEquals(rename.Row.EntryOn(side), incoming),
                "a refused same-side replacement still permits renaming the exact incoming item");
            Check.Require(Resolutions.PathsToSync(rename).SequenceEqual(new[]
                { (side, "Parent/renamed"), (PathNames.Other(side), incoming.Path) }),
                "same-side follow-ups use the incoming original path, not the occupied issue path");

            foreach (var staleItem in issue.Subject.Items)
            foreach (var changePath in new[] { false, true })
            {
                var staleItemIdentity = changePath ? staleItem with { Path = staleItem.Path.ToUpperInvariant() } :
                    staleItem with { Id = "changed" };
                var staleIssue = issue with
                {
                    Subject = issue.Subject with
                    {
                        Items = issue.Subject.Items.Select(item => item == staleItem ? staleItemIdentity : item).ToArray()
                    }
                };
                Check.Require(Fails(() => Resolutions.For(staleIssue, ResolutionChoice.ReplaceExisting, null,
                    localRow, dropboxRow)) == TextKey.ResolveSubjectStale,
                    "every exact same-side identity is checked before reporting unsupported replacement");
            }
        }
    }

    private static void MoveFollowUp()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var folder in new[] { false, true })
        {
            var original = side == Side.Local ? "Parent/a_b" : "Parent/a:b";
            var entry = folder ? Folder(original, "source") : File(original, "source");
            var other = File("Other/unrelated", "other");
            var row = RowOf(side == Side.Local ? entry : other, side == Side.Dropbox ? entry : other);
            foreach (var target in new[] { "Parent/renamed", original,
                side == Side.Local ? "Parent/A_B" : "Parent/A:B",
                side == Side.Local ? "Parent/other" : "Parent/a?b" })
            {
                var action = new SyncAction(ActionKind.MoveUnlinked, row, Target: target, Side: side);
                var paths = Resolutions.PathsToSync(action);
                Check.Require(paths.Count == 2 && paths[0] == (side, target),
                    "file and folder moves select the destination first and keep identical paths on distinct sides");
                Check.Require(paths[1] == (PathNames.Other(side), original),
                    "the freed name uses the opposite side and native spelling, including case-only and mapped-name moves");
            }
        }

        foreach (var kind in Enum.GetValues<ActionKind>())
        {
            if (kind == ActionKind.MoveUnlinked) continue;
            Check.Require(Resolutions.PathsToSync(new SyncAction(kind, RowOf(null, null))).Count == 0,
                "only MoveUnlinked selects path follow-ups: " + kind);
        }
    }

    private static void Dismissals()
    {
        var local = File("a.txt", "l1");
        foreach (var kind in new[] { IssueKind.Info, IssueKind.Skipped })
        {
            var issue = IssueOf(kind, TextKey.InfoKeptBoth, null, new IssueItem(Side.Local, local.Path, local.Id));
            Check.Require(Resolutions.For(issue, ResolutionChoice.Dismiss).Count == 0,
                "dismiss on an info or skipped issue returns no actions");
        }
    }

    private static void KeepFolderAlways()
    {
        Check.Require(Resolutions.RequestValid(true, false, ResolutionChoice.Dismiss, false, false, null) &&
            Resolutions.RequestValid(false, true, ResolutionChoice.Retry, false, false, null) &&
            Resolutions.RequestValid(true, false, ResolutionChoice.FirstSync, false, true, null) &&
            Resolutions.RequestValid(true, false, ResolutionChoice.KeepFolderAlways, false, false, "rule") &&
            !Resolutions.RequestValid(false, false, ResolutionChoice.Dismiss, false, false, null) &&
            !Resolutions.RequestValid(true, true, ResolutionChoice.Retry, false, false, null) &&
            !Resolutions.RequestValid(false, true, ResolutionChoice.Dismiss, false, false, null) &&
            !Resolutions.RequestValid(true, false, ResolutionChoice.Dismiss, false, true, null) &&
            !Resolutions.RequestValid(true, false, ResolutionChoice.Dismiss, false, false, "rule") &&
            !Resolutions.RequestValid(true, false, ResolutionChoice.KeepFolderAlways, false, false, null) &&
            !Resolutions.RequestValid(true, false, ResolutionChoice.KeepFolderAlways, false, false, "") &&
            !Resolutions.RequestValid(true, false, ResolutionChoice.KeepFolderAlways, true, false, "rule"),
            "the resolve request shape has one rule");
        Check.Require(Resolutions.Allows(IssueKind.Info, TextKey.InfoIgnoredFolderKept, ResolutionChoice.KeepFolderAlways),
            "keep-folder-always is the ignored-folder notice's choice");
        Check.Require(!Resolutions.Allows(IssueKind.Info, TextKey.InfoKeptBoth, ResolutionChoice.KeepFolderAlways),
            "keep-folder-always refuses any other info cause");
        foreach (var kind in new[] { IssueKind.Skipped, IssueKind.Tie, IssueKind.NameClash, IssueKind.Failure })
            Check.Require(!Resolutions.Allows(kind, TextKey.InfoIgnoredFolderKept, ResolutionChoice.KeepFolderAlways),
                "keep-folder-always is not a choice for " + kind);

        var folder = Folder("dir", "d1");
        var issue = IssueOf(IssueKind.Info, TextKey.InfoIgnoredFolderKept, null,
            new IssueItem(Side.Dropbox, folder.Path, folder.Id));
        Check.Require(Resolutions.For(issue, ResolutionChoice.KeepFolderAlways).Count == 0,
            "keep-folder-always returns no sync actions; the Run transaction owns the saved rules");
    }

    private static void Rejections()
    {
        var (tie, row, _, _) = Tie(TextKey.TieEqualTimes);
        Check.Require(Fails(() => Resolutions.For(tie, ResolutionChoice.KeepLocal, null, row, row))
            == TextKey.ResolveBadChoice, "a choice outside the content-tie set is a bad choice");

        var (caseTie, caseRow, _, _) = Tie(TextKey.TieCaseBoth);
        Check.Require(Fails(() => Resolutions.For(caseTie, ResolutionChoice.KeepBoth, null, caseRow, caseRow))
            == TextKey.ResolveBadChoice, "keep-both is not a case-tie choice");

        var local = Folder("a", "l1");
        var dropbox = File("a", "d1");
        var kindRow = RowOf(local, dropbox);
        var kindIssue = IssueOf(IssueKind.Tie, TextKey.TieKindBoth, null,
            new(Side.Local, local.Path, local.Id), new(Side.Dropbox, dropbox.Path, dropbox.Id));
        Check.Require(Fails(() => Resolutions.For(kindIssue, ResolutionChoice.KeepBoth, null, kindRow, kindRow))
            == TextKey.ResolveBadChoice, "keep-both is not a kind-tie choice");

        var incoming = File("a.txt", "l2");
        var occupant = File("a.txt", "d2");
        var clashRow = RowOf(incoming, occupant);
        var incomingIssue = IssueOf(IssueKind.NameClash, TextKey.ClashNameTaken, Side.Local,
            new(Side.Local, incoming.Path, incoming.Id), new(Side.Dropbox, occupant.Path, occupant.Id));
        Check.Require(Fails(() => Resolutions.For(incomingIssue, ResolutionChoice.KeepLocal, null, clashRow,
            clashRow)) == TextKey.ResolveBadChoice, "keep-local is not a choice when an incoming side exists");
        Check.Require(Fails(() => Resolutions.For(incomingIssue, ResolutionChoice.RenameIncoming, null, clashRow,
            clashRow)) == TextKey.ResolveBadChoice, "rename-incoming needs a new name");
        foreach (var badName in new[] { "dir/new.txt", "..", ".", "a\\b" })
            Check.Require(Fails(() => Resolutions.For(incomingIssue, ResolutionChoice.RenameIncoming, badName,
                clashRow, clashRow)) == TextKey.ResolveBadChoice,
                "a rename name must be one segment (no separators or traversal): " + badName);

        var noIncomingIssue = IssueOf(IssueKind.NameClash, TextKey.ClashFileFolder, null,
            new(Side.Local, incoming.Path, incoming.Id), new(Side.Dropbox, occupant.Path, occupant.Id));
        Check.Require(Fails(() => Resolutions.For(noIncomingIssue, ResolutionChoice.ReplaceExisting, null, clashRow,
            clashRow)) == TextKey.ResolveBadChoice, "replace-existing is not a choice with no incoming side");
        Check.Require(Fails(() => Resolutions.For(noIncomingIssue, ResolutionChoice.RenameLocal, null, clashRow,
            clashRow)) == TextKey.ResolveBadChoice, "rename-local needs a new name");

        var info = IssueOf(IssueKind.Info, TextKey.InfoKeptBoth, null, new IssueItem(Side.Local, incoming.Path, incoming.Id));
        Check.Require(Fails(() => Resolutions.For(info, ResolutionChoice.UseLocal, null, clashRow, clashRow))
            == TextKey.ResolveBadChoice, "an info issue accepts only dismiss");

        var failure = IssueOf(IssueKind.Failure, TextKey.FailChanged, null, new IssueItem(Side.Local, incoming.Path, incoming.Id));
        Check.Require(Fails(() => Resolutions.For(failure, ResolutionChoice.Dismiss)) == TextKey.ResolveBadChoice,
            "a failure issue is not resolvable");

        var dismissal = IssueOf(IssueKind.Info, TextKey.InfoKeptBoth, null, new IssueItem(Side.Local, incoming.Path, incoming.Id));
        Check.Require(Fails(() => Resolutions.For(dismissal, ResolutionChoice.Dismiss, null, clashRow, clashRow))
            == TextKey.ResolveBadChoice, "a row-free choice never reaches the row-taking overload");
        Check.Require(Fails(() => Resolutions.For(dismissal, ResolutionChoice.UseLocal)) == TextKey.ResolveBadChoice,
            "the row-free overload refuses a choice that needs observations");
    }

    private static void Stale()
    {
        var local = File("a.txt", "l1");
        var dropbox = File("a.txt", "d1");
        var issue = IssueOf(IssueKind.Tie, TextKey.TieEqualTimes, null,
            new(Side.Local, local.Path, local.Id), new(Side.Dropbox, dropbox.Path, dropbox.Id));

        var changedLocal = File("a.txt", "l9");
        var changedRow = RowOf(changedLocal, dropbox);
        Check.Require(Fails(() => Resolutions.For(issue, ResolutionChoice.UseLocal, null, changedRow, changedRow))
            == TextKey.ResolveSubjectStale, "a changed local id makes the subject stale");

        var movedDropbox = File("b.txt", "d1");
        var movedRow = RowOf(local, movedDropbox);
        Check.Require(Fails(() => Resolutions.For(issue, ResolutionChoice.UseDropbox, null, movedRow, movedRow))
            == TextKey.ResolveSubjectStale, "a changed Dropbox path makes the subject stale");

        var caseChanged = File("A.txt", "l1");
        var caseRow = RowOf(caseChanged, dropbox);
        Check.Require(Fails(() => Resolutions.For(issue, ResolutionChoice.UseLocal, null, caseRow, caseRow))
            == TextKey.ResolveSubjectStale, "the subject identity is exact, not case-insensitive");

        Check.Require(Fails(() => Resolutions.For(issue, ResolutionChoice.KeepLocal, null, changedRow, changedRow))
            == TextKey.ResolveSubjectStale, "the stale check comes before the choice check");

        var incoming = File("a.txt", "l2");
        var occupant = File("a.txt", "d2");
        var clashIssue = IssueOf(IssueKind.NameClash, TextKey.ClashNameTaken, Side.Local,
            new(Side.Local, incoming.Path, incoming.Id), new(Side.Dropbox, occupant.Path, occupant.Id));
        var changedClashRow = RowOf(File("a.txt", "l9"), occupant);
        Check.Require(Fails(() => Resolutions.For(clashIssue, ResolutionChoice.KeepLocal, null, changedClashRow,
            changedClashRow)) == TextKey.ResolveSubjectStale, "a stale name clash reports stale before an invalid choice");

        Check.Require(Resolutions.For(issue, ResolutionChoice.Dismiss).Count == 0,
            "dismiss is allowed with no identity check (a stale subject still dismisses)");

        var skippedRow = RowOf(local with { Skip = nameof(TextKey.SkipUnreadable) }, dropbox);
        Check.Require(Fails(() => Resolutions.For(issue, ResolutionChoice.UseLocal, null, skippedRow, skippedRow))
            == TextKey.ResolveSubjectStale, "a skipped exact observation does not satisfy a tie subject");
        var skippedClashRow = RowOf(incoming with { Skip = nameof(TextKey.SkipUnreadable) }, occupant);
        Check.Require(Fails(() => Resolutions.For(clashIssue, ResolutionChoice.RenameIncoming, "new.txt",
            skippedClashRow, skippedClashRow)) == TextKey.ResolveSubjectStale,
            "a skipped exact observation does not satisfy a name clash");
    }

    private static (Issue Issue, Row Row, Entry Local, Entry Dropbox) Tie(TextKey cause)
    {
        var local = File("a.txt", "l1");
        var dropbox = File("a.txt", "d1");
        var row = RowOf(local, dropbox);
        var issue = IssueOf(IssueKind.Tie, cause, null,
            new(Side.Local, local.Path, local.Id), new(Side.Dropbox, dropbox.Path, dropbox.Id));
        return (issue, row, local, dropbox);
    }

    private static SyncAction Single(IReadOnlyList<SyncAction> actions)
    {
        Check.Require(actions.Count == 1, "one action expected");
        return actions[0];
    }

    private static TextKey? Fails(Func<IReadOnlyList<SyncAction>> run)
    {
        try
        {
            run();
            return null;
        }
        catch (EngineRequestException error)
        {
            if (error.Key is TextKey.ResolveSubjectStale or TextKey.ResolveSameSideUnsupported)
                Check.Require(!error.Message.Contains('{'),
                    "subject-stale and same-side refusals render their owned diagnostic without unresolved placeholders");
            return error.Key;
        }
    }

    private static Issue IssueOf(IssueKind kind, TextKey cause, Side? incoming, params IssueItem[] items) =>
        IssueAt(kind, cause, incoming, "", items);

    private static Issue IssueAt(IssueKind kind, TextKey cause, Side? incoming, string path, params IssueItem[] items) =>
        new(1, kind, cause, path, new IssueSubject(items, incoming), T0, false);

    private static Row RowOf(Entry? local, Entry? dropbox) =>
        new("a", local, dropbox, null, Array.Empty<Entry>(), null, NoContext);

    private static Entry File(string path, string id) => new(path, false, 1, T0, id, "", "", null);

    private static Entry Folder(string path, string id) => new(path, true, 0, T0, id, "", "", null);
}
