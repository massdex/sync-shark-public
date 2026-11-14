namespace Massdex.SyncShark.Engine.Planner;

/// <summary>The actions of a notice resolution (section 6.6). Dismiss and keep-folder-always return no actions and need
/// no identity check here; every other choice checks
/// the subject still matches, then picks the incoming item and the occupant by the subject items and issue.Path.</summary>
public static class Resolutions
{
    /// <summary>The one rule for which choice an issue accepts; the other cause and subject checks stay in Tie and
    /// Clash. Keep-folder-always takes the ignored-folder notice only; its subject, rules and coverage belong to the
    /// Run transaction. For's dismiss path and EngineOperations both refuse a choice this rejects.</summary>
    internal static bool Allows(IssueKind kind, TextKey cause, ResolutionChoice choice) => (kind, choice) switch
    {
        (IssueKind.RootIdentity, ResolutionChoice.BindRoots) => true,
        (_, ResolutionChoice.Ignore or ResolutionChoice.Unignore) => Enum.IsDefined(kind),
        (IssueKind.Failure, ResolutionChoice.Retry) => true,
        (IssueKind.DriveChange, ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally) => true,
        (IssueKind.NameClash, ResolutionChoice.RenameIncoming or ResolutionChoice.ReplaceExisting
            or ResolutionChoice.KeepLocal or ResolutionChoice.KeepDropbox or ResolutionChoice.RenameLocal
            or ResolutionChoice.RenameDropbox) => true,
        (IssueKind.Tie, ResolutionChoice.UseLocal or ResolutionChoice.UseDropbox or ResolutionChoice.KeepBoth) => true,
        (IssueKind.Info or IssueKind.Skipped or IssueKind.Tie or IssueKind.NameClash, ResolutionChoice.Dismiss) => true,
        (IssueKind.Info, ResolutionChoice.KeepFolderAlways) => cause == TextKey.InfoIgnoredFolderKept,
        _ => false
    };

    /// <summary>The one option-shape rule of a resolve request, before any state: one exact issue or --all with Retry;
    /// first-run with FirstSync; rules, and no name, with keep-folder-always, its one choice with rules.</summary>
    public static bool RequestValid(bool hasIssue, bool all, ResolutionChoice choice, bool hasName, bool hasFirstRun,
        string? rules) =>
        (choice is not (ResolutionChoice.Ignore or ResolutionChoice.Unignore) || !hasName && !hasFirstRun && rules is null && !all) &&
        hasIssue != all && (!all || choice == ResolutionChoice.Retry) &&
        (!hasFirstRun || choice == ResolutionChoice.FirstSync) &&
        (rules is null || choice == ResolutionChoice.KeepFolderAlways) &&
        (choice != ResolutionChoice.KeepFolderAlways || !string.IsNullOrEmpty(rules) && !hasName);

    /// <summary>Validates a choice that acts on no observation rows and returns its empty action list. A stale subject
    /// still dismisses; keep-folder-always has no sync action, and its Run transaction validates the exact notice and
    /// saves the rules.</summary>
    public static IReadOnlyList<SyncAction> For(Issue issue, ResolutionChoice choice)
    {
        if (choice is not (ResolutionChoice.Dismiss or ResolutionChoice.KeepFolderAlways or ResolutionChoice.Ignore or ResolutionChoice.Unignore)) throw Bad();
        if (!Allows(issue.Kind, issue.Cause, choice)) throw Bad();
        return [];
    }

    /// <summary>Validates a choice that needs the subject's observation rows and returns its actions without
    /// performing effects. The row-free choices are refused here; they have their own overload. incomingBound: the
    /// incoming identity has a saved folder binding, so replacing keeps move semantics (B-216).</summary>
    public static IReadOnlyList<SyncAction> For(Issue issue, ResolutionChoice choice, string? newName, Row localRow,
        Row dropboxRow, bool incomingBound = false)
    {
        if (choice is ResolutionChoice.Dismiss or ResolutionChoice.KeepFolderAlways or ResolutionChoice.Ignore or ResolutionChoice.Unignore) throw Bad();
        if (issue.Kind == IssueKind.NameClash) return Clash(issue, choice, newName, localRow, dropboxRow, incomingBound);
        if (!StillMatches(issue.Subject, localRow, dropboxRow)) throw Stale();
        if (issue.Kind == IssueKind.Tie) return Tie(issue.Cause, choice, localRow, dropboxRow);
        throw Bad();
    }

    /// <summary>The folder whose descendants a resolution must sync after an action: a recognized action with a
    /// nonfolder source resets it to null; any other action kind keeps the previous value.</summary>
    public static (Side Side, Entry Entry)? FolderToSync(SyncAction action, (Side Side, Entry Entry)? previous) =>
        action.Kind switch
        {
            ActionKind.EnsureDropboxFolder => FolderSource(Side.Local, action.Row.Local),
            ActionKind.EnsureLocalFolder => FolderSource(Side.Dropbox, action.Row.Dropbox),
            ActionKind.ReplaceKind when action.Side == Side.Dropbox => FolderSource(Side.Local, action.Row.Local),
            ActionKind.ReplaceKind => FolderSource(Side.Dropbox, action.Row.Dropbox),
            _ => previous
        };

    /// <summary>The destination and freed original name to sync after a successful unlinked move, captured before
    /// effects and kept in native spelling. The freed name uses the opposite side, where its occupant remains.</summary>
    public static IReadOnlyList<(Side Side, string Path)> PathsToSync(SyncAction action)
    {
        if (action.Kind != ActionKind.MoveUnlinked) return [];
        var side = action.WrittenSide();
        var target = action.Target!;
        var original = action.Row.EntryOn(side)!.Path;
        return [(side, target), (PathNames.Other(side), original)];
    }

    /// <summary>The root actions left once the removal has taken the occupant: they see its side as empty.</summary>
    public static IReadOnlyList<SyncAction> AfterRemoval(IReadOnlyList<SyncAction> actions, SyncAction removal)
    {
        var side = removal.WrittenSide();
        return actions.Where(action => !ReferenceEquals(action, removal))
            .Select(action => action with { Row = action.Row with
            {
                Local = side == Side.Local ? null : action.Row.Local,
                Dropbox = side == Side.Dropbox ? null : action.Row.Dropbox
            } }).ToArray();
    }

    private static (Side Side, Entry Entry)? FolderSource(Side side, Entry? entry) =>
        entry is { IsFolder: true } ? (side, entry) : null;

    private static IReadOnlyList<SyncAction> Tie(TextKey cause, ResolutionChoice choice, Row localRow, Row dropboxRow)
    {
        var pair = Pair(localRow, dropboxRow);
        var local = pair.Local ?? throw Stale();
        var dropbox = pair.Dropbox ?? throw Stale();
        return [(cause, choice) switch
        {
            (TextKey.TieEqualTimes or TextKey.TieUnreliableTime, ResolutionChoice.UseLocal)
                => new SyncAction(ActionKind.Upload, pair, Target: dropbox.Path),
            (TextKey.TieEqualTimes or TextKey.TieUnreliableTime, ResolutionChoice.UseDropbox)
                => new SyncAction(ActionKind.Download, pair, Target: local.Path),
            (TextKey.TieEqualTimes or TextKey.TieUnreliableTime, ResolutionChoice.KeepBoth)
                => new SyncAction(ActionKind.KeepBoth, pair),
            (TextKey.TieCaseBoth, ResolutionChoice.UseLocal)
                => new SyncAction(ActionKind.MoveDropbox, pair, Target: local.Path),
            (TextKey.TieCaseBoth, ResolutionChoice.UseDropbox)
                => new SyncAction(ActionKind.MoveLocal, pair, Target: PathNames.LocalSpelling(dropbox.Path)),
            (TextKey.TieKindBoth, ResolutionChoice.UseLocal)
                => new SyncAction(ActionKind.ReplaceKind, pair, Target: dropbox.Path, Side: Side.Dropbox),
            (TextKey.TieKindBoth, ResolutionChoice.UseDropbox)
                => new SyncAction(ActionKind.ReplaceKind, pair, Target: local.Path, Side: Side.Local),
            _ => throw Bad()
        }];
    }

    private static IReadOnlyList<SyncAction> Clash(Issue issue, ResolutionChoice choice, string? newName,
        Row localRow, Row dropboxRow, bool incomingBound)
    {
        // Already replaced (2.6.5): nothing is left at the occupant's path and every other subject item (the bound incoming
        // folder and, for an occupied rename, its own saved counterpart) is still in place, so a crash came between the delete
        // and the close; the runner finishes that removal, never a second one.
        if (choice == ResolutionChoice.ReplaceExisting && incomingBound && issue.Subject.Incoming is { } bound &&
            issue.Subject.Items.All(item => OccupantRole(item.Side, item.Path, bound, issue.Path)
                ? FindObserved(item, localRow, dropboxRow, anyId: true) is null
                : true))
            return [];
        var matches = Matches(issue.Subject, localRow, dropboxRow);
        var side = (choice, issue.Subject.Incoming) switch
        {
            (ResolutionChoice.RenameIncoming or ResolutionChoice.ReplaceExisting, { } incoming) => incoming,
            (ResolutionChoice.KeepLocal or ResolutionChoice.RenameLocal, null) => Side.Local,
            (ResolutionChoice.KeepDropbox or ResolutionChoice.RenameDropbox, null) => Side.Dropbox,
            _ => throw Bad()
        };
        var entry = Incoming(matches, side, issue);
        return (choice is ResolutionChoice.RenameIncoming or ResolutionChoice.RenameLocal or ResolutionChoice.RenameDropbox)
            ? [Unlinked(side, entry, newName, localRow, dropboxRow)]
            : Replace(side, entry, matches, issue.Path, localRow, dropboxRow, incomingBound);
    }

    private static SyncAction Unlinked(Side side, Entry entry, string? newName, Row localRow, Row dropboxRow)
    {
        if (!PathNames.IsSingleSegment(newName)) throw Bad();
        return new SyncAction(ActionKind.MoveUnlinked, Single(side, entry, localRow, dropboxRow),
            Target: PathNames.Child(PathNames.Parent(entry.Path), newName!), Side: side);
    }

    // Only same-kind files can replace in one transfer; a kind change uses the existing delete-then-create action.
    // A bound incoming folder only frees the path: its own rename then moves it in, so nothing is copied (B-216).
    private static IReadOnlyList<SyncAction> Replace(Side incomingSide, Entry incoming, Matched[] matches,
        string issuePath, Row localRow, Row dropboxRow, bool incomingBound)
    {
        var (occupantSide, occupant) = Occupant(matches, incomingSide, incoming, issuePath, localRow, dropboxRow);
        if (occupantSide == incomingSide)
            throw new EngineRequestException(TextKey.ResolveSameSideUnsupported);
        var row = localRow with
        {
            Local = incomingSide == Side.Local ? incoming : occupant,
            Dropbox = incomingSide == Side.Dropbox ? incoming : occupant,
            Synced = null,
            Extra = [],
            LocalHash = null
        };
        if (incomingBound && incoming.IsFolder) return [new SyncAction(SyncAction.DeleteOn(occupantSide), row)];
        if (incoming.IsFolder != occupant.IsFolder)
            return [new SyncAction(ActionKind.ReplaceKind, row, Target: occupant.Path, Side: occupantSide)];
        var copy = new SyncAction(SyncAction.CopyOn(incoming.IsFolder, occupantSide), row, Target: occupant.Path);
        if (!incoming.IsFolder) return [copy];
        var delete = new SyncAction(SyncAction.DeleteOn(occupantSide),
            Single(occupantSide, occupant, localRow, dropboxRow));
        return [delete, copy];
    }

    // A skipped entry is present but never synced (D27), so it never satisfies a resolution subject.
    private static Entry? Resolvable(IssueItem item, Row localRow, Row dropboxRow) =>
        FindObserved(item, localRow, dropboxRow) is { Skip: null } entry ? entry : null;

    // Validate every subject identity before selecting a resolution action.
    private static Matched[] Matches(IssueSubject subject, Row localRow, Row dropboxRow)
    {
        var matches = new Matched[subject.Items.Count];
        for (var index = 0; index < matches.Length; index++)
        {
            var item = subject.Items[index];
            var entry = Resolvable(item, localRow, dropboxRow) ?? throw Stale();
            matches[index] = new Matched(item.Side, entry);
        }
        return matches;
    }

    // Producers put the incoming item first on its side (as ScopedCloseAllowed reads it); its path is not a role, so
    // only a subject without an incoming side selects by the issue path (B-124).
    private static Entry Incoming(Matched[] matches, Side side, Issue issue)
    {
        Entry? fallback = null;
        foreach (var match in matches)
        {
            if (match.Side != side) continue;
            if (issue.Subject.Incoming is not null || match.Entry.Path == issue.Path) return match.Entry;
            fallback ??= match.Entry;
        }
        return fallback ?? throw Stale();
    }

    // The item the clash is at (issue.Path) on the other side; a different spelling falls back to that side's item.
    private static (Side Side, Entry Entry) Occupant(Matched[] matches, Side incomingSide, Entry incoming,
        string issuePath, Row localRow, Row dropboxRow)
    {
        foreach (var match in matches)
            if (OccupantRole(match.Side, match.Entry.Path, incomingSide, issuePath) && !Same(match.Entry, incoming))
                return (match.Side, match.Entry);
        foreach (var match in matches)
            if (match.Entry.Path == issuePath && !Same(match.Entry, incoming))
                return (match.Side, match.Entry);
        foreach (var match in matches)
            if (match.Side != incomingSide && !Same(match.Entry, incoming))
                return (match.Side, match.Entry);
        if (On(PathNames.Other(incomingSide), localRow, dropboxRow) is { } fallback)
            return (PathNames.Other(incomingSide), fallback);
        throw Stale();
    }

    // The occupant is the other side's item at the clash path in its exact spelling; a case variant is another item.
    private static bool OccupantRole(Side side, string path, Side incoming, string issuePath) =>
        side != incoming && path == issuePath;

    private static bool Same(Entry left, Entry right) => Same(left, right.Path, right.Id);

    private static bool Same(Entry entry, string path, string id) => entry.Path == path && entry.Id == id;

    private static Row Single(Side side, Entry entry, Row localRow, Row dropboxRow)
    {
        var source = side == Side.Local ? localRow : dropboxRow;
        return Row.ForOneSide(side, source.Key, entry, null, [], source.LocalHash, source.Context);
    }

    private static Row Pair(Row localRow, Row dropboxRow) => localRow with
    {
        Local = On(Side.Local, localRow, dropboxRow),
        Dropbox = On(Side.Dropbox, localRow, dropboxRow),
        Synced = localRow.Synced ?? dropboxRow.Synced,
        LocalHash = localRow.LocalHash ?? dropboxRow.LocalHash
    };

    // The row observed for the subject item on that side, falling back to the other row's entry.
    private static Entry? On(Side side, Row localRow, Row dropboxRow) => side == Side.Local
        ? localRow.Local ?? dropboxRow.Local
        : dropboxRow.Dropbox ?? localRow.Dropbox;

    // A stale check: every subject item must still be observed with the same path and id and not skipped.
    private static bool StillMatches(IssueSubject subject, Row localRow, Row dropboxRow)
    {
        foreach (var item in subject.Items)
            if (Resolvable(item, localRow, dropboxRow) is null) return false;
        return true;
    }

    // The first current observation on the subject item's side with the exact path and id, in the existing order:
    // Local = local row then Dropbox row; Dropbox = Dropbox row, local row, then the two Extra lists. anyId asks for
    // any item at that path in any spelling, since a case variant there still occupies it.
    private static Entry? FindObserved(IssueItem item, Row localRow, Row dropboxRow, bool anyId = false)
    {
        bool At(Entry entry) => anyId ? PathNames.Same(entry.Path, item.Path) : Same(entry, item.Path, item.Id);
        if (item.Side == Side.Local)
        {
            if (localRow.Local is { } first && At(first)) return first;
            if (dropboxRow.Local is { } second && At(second)) return second;
            return null;
        }
        if (dropboxRow.Dropbox is { } primary && At(primary)) return primary;
        if (localRow.Dropbox is { } secondary && At(secondary)) return secondary;
        foreach (var extra in dropboxRow.Extra)
            if (At(extra)) return extra;
        foreach (var extra in localRow.Extra)
            if (At(extra)) return extra;
        return null;
    }

    private readonly record struct Matched(Side Side, Entry Entry);

    private static EngineRequestException Stale() => new(TextKey.ResolveSubjectStale);

    private static EngineRequestException Bad() => new(TextKey.ResolveBadChoice);
}
