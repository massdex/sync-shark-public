namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Allowed notice choices and the exact conditional actions each choice selects.</summary>
public static class Resolutions
{
    /// <summary>Run exposes this list to Edge; presentation does not interpret the notice again.</summary>
    public static IReadOnlyList<ResolutionChoice> Choices(Notice notice) =>
        Enum.GetValues<ResolutionChoice>().Where(choice => Allows(notice, choice)).ToArray();

    private static bool Allows(Notice n, ResolutionChoice choice) => (n.Kind, choice) switch
    {
        (_, ResolutionChoice.Ignore or ResolutionChoice.Unignore) => Enum.IsDefined(n.Kind),
        (NoticeKind.PutBack or NoticeKind.Failure, ResolutionChoice.Retry) => true,
        (NoticeKind.RootIdentity, ResolutionChoice.BindRoots) => true,
        (NoticeKind.DriveChange, ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally) => true,
        (NoticeKind.Tie or NoticeKind.NameClash or NoticeKind.Info or NoticeKind.Skipped, ResolutionChoice.Dismiss) => true,
        (NoticeKind.NameClash, ResolutionChoice.RenameIncoming or ResolutionChoice.ReplaceExisting) => n.Subject.Incoming.HasValue,
        (NoticeKind.NameClash, ResolutionChoice.KeepLocal or ResolutionChoice.KeepDropbox or ResolutionChoice.RenameLocal or ResolutionChoice.RenameDropbox) => !n.Subject.Incoming.HasValue,
        (NoticeKind.Tie, ResolutionChoice.UseLocal or ResolutionChoice.UseDropbox) => true,
        (NoticeKind.Tie, ResolutionChoice.KeepBoth) => n.Cause is TextKey.TieEqualTimes or TextKey.TieUnreliableTime,
        (NoticeKind.Info, ResolutionChoice.UseLocal or ResolutionChoice.UseDropbox) => n.Cause == TextKey.InfoKeptBoth,
        (NoticeKind.Info, ResolutionChoice.RenameLocal or ResolutionChoice.RenameDropbox) => n.Cause == TextKey.InfoKeptBoth &&
            n.Subject.Items.Skip(2).Any(item => item.Side == (choice == ResolutionChoice.RenameLocal ? Side.Local : Side.Dropbox)),
        (NoticeKind.Info, ResolutionChoice.KeepLocal or ResolutionChoice.KeepDropbox) => n.Cause == TextKey.InfoMovedTwice,
        (NoticeKind.Info, ResolutionChoice.KeepBoth) => n.Cause is TextKey.InfoMovedTwice or TextKey.InfoKeptBoth,
        _ => false
    };

    public static IReadOnlyList<SyncAction> For(Notice notice, ResolutionChoice choice) =>
        choice is (ResolutionChoice.Dismiss or ResolutionChoice.Ignore or ResolutionChoice.Unignore) && Allows(notice, choice) ? [] : throw Bad();

    /// <summary>Validate all subjects before returning any actions, including both endpoints of a losing binding.</summary>
    public static IReadOnlyList<SyncAction> For(Notice notice, ResolutionChoice choice, string? name, Row localRow,
        Row dropboxRow, bool incomingBound = false)
    {
        if (!Allows(notice, choice)) throw Bad();
        if (choice is ResolutionChoice.Dismiss or ResolutionChoice.Ignore or ResolutionChoice.Unignore) return For(notice, choice);
        if (notice.Subject.Items.Count == 0) throw Stale();
        if (notice.Kind == NoticeKind.NameClash) return Clash(notice, choice, name, localRow, dropboxRow, incomingBound);
        if ((notice.Kind != NoticeKind.Info || notice.Cause != TextKey.InfoKeptBoth || choice == ResolutionChoice.KeepBoth) &&
            !StillMatches(notice.Subject, localRow, dropboxRow)) throw Stale();
        if (notice.Kind == NoticeKind.Tie)
        {
            Require(notice, Side.Local, On(Side.Local, localRow, dropboxRow));
            Require(notice, Side.Dropbox, On(Side.Dropbox, localRow, dropboxRow));
            return Tie(notice.Cause, choice, localRow, dropboxRow);
        }
        if (notice.Kind != NoticeKind.Info) throw Bad();
        if (choice == ResolutionChoice.KeepBoth) return [];
        if (notice.Cause == TextKey.InfoMovedTwice)
        {
            var side = choice == ResolutionChoice.KeepLocal ? Side.Dropbox : Side.Local;
            var losing = side == Side.Local ? localRow : dropboxRow;
            return [Delete(notice, side, losing), Delete(notice, PathNames.Other(side), losing)];
        }
        // The producer records both originals first, then any labels. A reviewed original is not a label.
        var subjects = notice.Subject.Items;
        if (subjects.Count is < 3 or > 4 || !MatchesEndpoint(subjects[0], Side.Local, localRow.Local) ||
            !MatchesEndpoint(subjects[1], Side.Dropbox, dropboxRow.Dropbox)) throw Stale();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var label = side == Side.Local ? dropboxRow.Local : localRow.Dropbox;
            var expected = subjects.Skip(2).Where(item => item.Side == side).ToArray();
            if (label is null ? expected.Length != 0 : expected.Length != 1 || !MatchesEndpoint(expected[0], side, label) ||
                label.Id == (side == Side.Local ? localRow.Local : dropboxRow.Dropbox)!.Id) throw Stale();
        }
        if (choice is ResolutionChoice.RenameLocal or ResolutionChoice.RenameDropbox)
        {
            var side = choice == ResolutionChoice.RenameLocal ? Side.Local : Side.Dropbox;
            var label = side == Side.Local ? dropboxRow.Local : localRow.Dropbox;
            return [Unlinked(side, label!, name, localRow, dropboxRow)];
        }
        var written = choice == ResolutionChoice.UseLocal ? Side.Dropbox : Side.Local;
        var pc = localRow.Local!;
        var remote = dropboxRow.Dropbox!;
        var originals = localRow with { Local = pc, Dropbox = remote, Synced = null, Extra = [], LocalHash = null };
        var actions = new List<SyncAction>(3);
        foreach (var side in new[]{written, PathNames.Other(written)})
        {
            var label = side == Side.Dropbox ? localRow : dropboxRow;
            if (label.EntryOn(side) is { } entry) actions.Add(new(SyncAction.DeleteOn(side), label, Target: entry.Path, Side: side));
        }
        var target = originals.EntryOn(written)!.Path;
        actions.Add(new(pc.IsFolder != remote.IsFolder ? ActionKind.ReplaceKind : SyncAction.CopyOn(pc.IsFolder, written), originals, Target: target, Side: written));
        return actions;
    }

    private static bool MatchesEndpoint(NoticeItem item, Side side, Entry? entry) =>
        entry is { Skip: null } && item.Side == side && item.Id == entry.Id && item.Path == entry.Path;

    private static Entry Require(Notice notice, Side side, Entry? entry) => entry is { Skip: null } &&
        notice.Subject.Items.Contains(new NoticeItem(side, entry.Path, entry.Id)) ? entry : throw Stale();

    private static SyncAction Delete(Notice notice, Side side, Row row) =>
        new(SyncAction.DeleteOn(side), row, Side: side, Target: Require(notice, side, row.EntryOn(side)).Path);
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

    private static IReadOnlyList<SyncAction> Clash(Notice issue, ResolutionChoice choice, string? newName,
        Row localRow, Row dropboxRow, bool incomingBound)
    {
        // Already replaced (2.6.5): nothing is left at the occupant's path and every other subject item (the bound incoming
        // folder and, for an occupied rename, its own saved counterpart) is still in place, so a crash came between the delete
        // and the close; the runner finishes that removal, never a second one.
        if (choice == ResolutionChoice.ReplaceExisting && incomingBound && issue.Subject.Incoming is { } bound &&
            issue.Subject.Items.All(item => OccupantRole(item.Side, item.Path, bound, issue.Path)
                ? FindObserved(item, localRow, dropboxRow, anyId: true) is null
                : Resolvable(item, localRow, dropboxRow) is not null))
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
        var (occupantSide, occupant) = Occupant(matches, incomingSide, incoming, issuePath);
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
    private static Entry? Resolvable(NoticeItem item, Row localRow, Row dropboxRow) =>
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
    private static Entry Incoming(Matched[] matches, Side side, Notice issue)
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
        string issuePath)
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
    private static Entry? FindObserved(NoticeItem item, Row localRow, Row dropboxRow, bool anyId = false)
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
