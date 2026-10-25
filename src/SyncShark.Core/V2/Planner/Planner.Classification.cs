namespace Massdex.SyncShark.V2;

/// <summary>Planner side classification and hash requests (planner.md sections 3 and 4).</summary>
public static partial class Planner
{
    /// <summary>Classifies one side of a row against its baseline (or as present when there is none).</summary>
    private static RowState ClassifySide(ItemState? cur, ItemState? baseSameSide, bool deleted, bool known)
    {
        if (cur is null)
            return deleted || known ? RowState.Deleted : RowState.Unknown;
        if (cur.Flags.HasFlag(ItemFlags.Ignored))
            return RowState.Ignored;
        if ((cur.Flags & (ItemFlags.Unreadable | ItemFlags.Locked | ItemFlags.Placeholder | ItemFlags.NeedsRehash)) != 0)
            return RowState.Unreadable;
        if (baseSameSide is null)
            return RowState.Present;
        var same = Content.SameVersion(cur, baseSameSide);
        if (same is null)
            return RowState.Unreadable;
        if (same == true)
            return PairPath.SameLeafLocation(cur.Path, baseSameSide.Path) ? RowState.Same : RowState.Moved;
        if (cur.Kind != baseSameSide.Kind)
            return RowState.Replaced;
        return PathEqual(cur.Path, baseSameSide.Path) ? RowState.Edited : RowState.MovedEdited;
    }

    /// <summary>Whether this side's content and location are unchanged under the planner's classification rules.</summary>
    internal static bool Unchanged(ItemState current, ItemState baseline) =>
        ClassifySide(current, baseline, false, true) == RowState.Same;

    private static bool PathEqual(string x, string y) => string.Equals(x, y, StringComparison.Ordinal);

    private static bool BaselineSpellingChanged(PlanRow row) => row.Base is not null &&
        row.A is not null && row.B is not null &&
        (!PathEqual(row.A.Path, row.Base.A.Path) || !PathEqual(row.B.Path, row.Base.B.Path));

    private static bool LeafCaseMismatch(PlanRow row) => row.A is not null && row.B is not null &&
        DropboxNameMapping.SamePlace(row.A, row.B) &&
        !PairPath.SameLeafLocation(DropboxNameMapping.MatchPath(row.A), DropboxNameMapping.MatchPath(row.B));

    private static bool Changed(RowState s) => s is RowState.Edited or RowState.Moved or RowState.MovedEdited;

    private static bool Comparable(ItemState? item) =>
        item is not null && item.Kind == ItemKind.File &&
        (item.Flags & (ItemFlags.Ignored | ItemFlags.Unreadable | ItemFlags.Locked | ItemFlags.Placeholder | ItemFlags.NeedsRehash | ItemFlags.OwnedPointer)) == 0;

    private static void NeedSameVersion(SortedSet<string> set, ItemState? cur, ItemState baseSameSide)
    {
        if (Comparable(cur) && cur!.Hash is null && Content.SameVersion(cur, baseSameSide) is null)
            set.Add(cur.Path);
    }

    /// <summary>The paths per side whose hashes must be computed before deciding (planner.md section 3).</summary>
    public static HashRequest HashRequests(IReadOnlyList<PlanRow> rows)
    {
        var a = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var b = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            StubHashNeed(row, a, b);
            if (row.Base is not null)
            {
                NeedSameVersion(a, row.A, row.Base.A);
                NeedSameVersion(b, row.B, row.Base.B);
            }
            var sA = ClassifySide(row.A, row.Base?.A, row.Ev.DeletedA, row.Ev.KnownA);
            var sB = ClassifySide(row.B, row.Base?.B, row.Ev.DeletedB, row.Ev.KnownB);
            // A spelling-only move needs cross-side proof, even when metadata matches its baseline.
            var compareContent = row.Base is null || (Changed(sA) && Changed(sB)) ||
                sA == RowState.Moved || sB == RowState.Moved ||
                sA == RowState.Same && sB == RowState.Same && (BaselineSpellingChanged(row) || LeafCaseMismatch(row));
            if (compareContent && Comparable(row.A) && Comparable(row.B) && DropboxNameMapping.SamePlace(row.A!, row.B!) &&
                Content.SameContent(row.A!, row.B!) is null)
            {
                if (row.A!.Hash is null)
                    a.Add(row.A.Path);
                if (row.B!.Hash is null)
                    b.Add(row.B.Path);
            }
        }
        foreach (var (side, path) in CollisionHashNeeds(rows))
            (side == Side.A ? a : b).Add(path);
        // Collision discovery also proposes hashes; owned pointer metadata is the only
        // permitted content identity, even when that identity is incomplete.
        foreach (var row in rows)
        {
            if (IsOwnedPointer(row.A)) a.Remove(row.A!.Path);
            if (IsOwnedPointer(row.B)) b.Remove(row.B!.Path);
        }
        return new HashRequest(a.ToArray(), b.ToArray());
    }

    // An unlinked inventory at unique, unmapped paths has no cross-row create destination or
    // folder move. Within-row hash needs were already collected above; one-sided creates target
    // an absent path, and conflict labels are allocated against occupancy. Avoid two action drafts.
    // Linked/mapped scopes retain the general projection: occupancy alone cannot model folder moves.
    private static bool OnlyUnlinkedPaths(IReadOnlyList<PlanRow> rows)
    {
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Base is not null || row.A is null && row.B is null ||
                row.A is { } a && DropboxNameMapping.MatchPath(a) != a.Path ||
                row.B is { } b && DropboxNameMapping.MatchPath(b) != b.Path ||
                row.A is { } left && row.B is { } right &&
                    (!PathEqual(left.Path, right.Path) || left.Kind != right.Kind) ||
                !occupied.Add((row.A ?? row.B)!.Path)) return false;
        }
        return true;
    }

    /// <summary>A neutral label rule for collision discovery; labels only pick free paths and never reach a plan.</summary>
    private static readonly LabelRule DiscoveryLabel = new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B");

    /// <summary>Discovery needs no bound: only finitely many paths are taken, so a free label is always found.</summary>
    private const int DiscoveryLabelAttempts = int.MaxValue;

    /// <summary>
    /// Unhashed files whose cross-row collision (different rows, one path once this plan's moves run) cannot be
    /// confirmed equal or different. Found by drafting the plan in both sync modes, since the modes reach different
    /// creates; the tie window does not matter because content is compared before modification times. Every such need
    /// names a present file without a hash, so with none there is nothing to draft.
    /// </summary>
    private static IEnumerable<(Side Side, string Path)> CollisionHashNeeds(IReadOnlyList<PlanRow> rows) =>
        !rows.Any(r => r.A is { Kind: ItemKind.File, Hash: null } || r.B is { Kind: ItemKind.File, Hash: null }) ||
        OnlyUnlinkedPaths(rows)
            ? Enumerable.Empty<(Side, string)>()
            : new[] { false, true }.SelectMany(firstSync =>
                BuildDraft(string.Empty, rows, new PlanOptions(TimeSpan.Zero, firstSync, DiscoveryLabel, DiscoveryLabelAttempts)).HashNeeds);
}
