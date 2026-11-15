using Draft = System.Collections.Generic.List<(Massdex.SyncShark.V2.PlanRow Row, Massdex.SyncShark.V2.SyncAction Action)>;

namespace Massdex.SyncShark.V2;

/// <summary>Planner post-passes: folder deletes and folder moves (planner.md sections 4.5 and 4.6).</summary>
public static partial class Planner
{
    // ---- post-pass B: folder deletes (rules 23 and 24); runs on the rows' own paths, before any rebase ----

    /// <summary>
    /// Deepest folder first: a folder Delete whose contents are all gone collapses (rule 23), else the folder is kept
    /// (rule 24). Linear: the items and Deletes under each deleted folder, and the folders that something is placed
    /// under, are indexed once; collapsed Deletes are marked and removed in one pass at the end.
    /// </summary>
    private static Draft PostFolderDeletes(Context ctx, IReadOnlyList<PlanRow> rows)
    {
        var draft = ctx.Draft;
        var deleted = new PlannedDeletes(draft.Select(d => d.Action).Where(a => a.Kind == ActionKind.Delete));
        var folderDeletes = Enumerable.Range(0, draft.Count)
            .Where(i => draft[i].Action.Kind == ActionKind.Delete && draft[i].Action.ExpectedTarget?.Kind == ItemKind.Folder)
            .OrderByDescending(i => PairPath.Depth(draft[i].Action.Path)).ToList();
        if (folderDeletes.Count == 0)
            return draft;
        var folders = new HashSet<(Side, string)>(folderDeletes.Select(i => (draft[i].Action.Target, draft[i].Action.Path)),
            PathKeyComparer.Instance);
        var itemsUnder = UnderFolders(folders, rows.Select((row, r) => (Side.A, row.A?.Path, r))
            .Concat(rows.Select((row, r) => (Side.B, row.B?.Path, r))));
        var deletesUnder = UnderFolders(folders, draft.Select((d, j) =>
            (d.Action.Target, d.Action.Kind == ActionKind.Delete ? d.Action.Path : null, j)));
        var placedUnder = new HashSet<(Side, string)>(PathKeyComparer.Instance);
        foreach (var (_, action) in draft.Where(d => Places(d.Action.Kind)))
            AddAncestors(placedUnder, action.Target, action.Path);
        var removed = new bool[draft.Count];
        foreach (var index in folderDeletes)
        {
            if (removed[index])
                continue;
            var (folderRow, delete) = draft[index];
            var key = (delete.Target, delete.Path);
            if ((itemsUnder.GetValueOrDefault(key) ?? []).Any(r =>
                    Cur(rows[r], delete.Target) is { } child && ProviderRefusedPaths.IsRetained(child)))
            {
                deleted.Withdraw(delete);
                draft[index] = (folderRow, delete with { Kind = ActionKind.Hold, RecoveryFirst = false });
                AddNotice(ctx, delete.Rule, NoticeKind.Info, delete.Path,
                    new NoticeReason(ProviderRefusedPaths.RetainedNoticeKey, ProviderRefusedPaths.RetainedNoticeDetail));
                continue;
            }
            if (ContentsGone(rows, deleted, folderRow, delete, placedUnder, itemsUnder.GetValueOrDefault(key)))
            {
                // Only deletes of one provenance collapse, so the collapsed delete's ChoiceRemove holds for every
                // victim; mixed ones stay separate (deepest first) and the folder's own delete removes only the
                // emptied folder, so every victim is deleted exactly once. A folder with a held path under it is never
                // collapsed over either: its ordinary child deletes stay, and BlockHeldFolderDeletes holds the folder.
                // Prepared children carry their own historical source IDs without baseline rows. Preserve those
                // actions: executor expansion cannot reconstruct their IDs from the parent folder's identity.
                var under = deletesUnder.GetValueOrDefault(key);
                if (MixedProvenance(draft, delete, under) || HeldUnder(ctx, delete) ||
                    (under ?? []).Any(j => draft[j].Action.DeletionSourceObjectId is not null))
                    draft[index] = (folderRow, delete with { DescendantsPlanned = true, RecoveryFirst = false });
                else
                    CollapseFolderDelete(draft, index, under, removed);
                continue;
            }
            deleted.Withdraw(delete);
            KeepFolder(ctx, draft, index);
            AddAncestors(placedUnder, Opposite(delete.Target), delete.Path);
        }
        return draft.Where((_, i) => !removed[i]).ToList();
    }

    /// <summary>
    /// True when every item under the folder on the target side is deleted by this plan (at its path or by a deleted
    /// folder inside this one) and nothing is placed under it. An item with no Delete (edited, unreadable, ignored,
    /// unhashed, no evidence) keeps the folder: the Executor expands a folder Delete over everything live under it.
    /// </summary>
    private static bool ContentsGone(IReadOnlyList<PlanRow> rows, PlannedDeletes deleted, PlanRow folderRow,
        SyncAction delete, HashSet<(Side, string)> placedUnder, List<int>? itemsUnder)
    {
        var target = delete.Target;
        if (placedUnder.Contains((target, delete.Path)))
            return false;
        foreach (var r in itemsUnder ?? new List<int>())
            if (!ReferenceEquals(rows[r], folderRow) &&
                (Cur(rows[r], target)!.Flags & ItemFlags.CarriedIgnore) == 0 &&
                !deleted.Covers(target, Cur(rows[r], target)!, delete.Path))
                return false;
        return true;
    }

    private static bool Places(ActionKind kind) => ActionPhases.IsCreate(kind) || kind == ActionKind.Update;

    /// <summary>Adds every folder strictly above the path on the side.</summary>
    private static void AddAncestors(HashSet<(Side, string)> set, Side side, string path)
    {
        foreach (var folder in PairPath.Ancestors(path))
            set.Add((side, folder));
    }

    /// <summary>For each of the folders, the positions of the entries whose path on that side lies strictly under it.</summary>
    private static Dictionary<(Side, string), List<int>> UnderFolders(HashSet<(Side, string)> folders,
        IEnumerable<(Side Side, string? Path, int Position)> entries)
    {
        var under = new Dictionary<(Side, string), List<int>>(PathKeyComparer.Instance);
        foreach (var (side, path, position) in entries)
            foreach (var folder in path is null ? Enumerable.Empty<string>() : PairPath.Ancestors(path))
                if (folders.Contains((side, folder)))
                {
                    if (!under.TryGetValue((side, folder), out var list))
                        under[(side, folder)] = list = new List<int>();
                    list.Add(position);
                }
        return under;
    }

    /// <summary>
    /// This plan's Deletes per side, by object and by path. An item counts as deleted only by its own Delete (matched by
    /// object, so a kept case-variant at a deleted path never does) or by a deleted folder between it and the folder
    /// being collapsed (matched by path ignoring case: a case-variant folder is itself an item that must be deleted).
    /// </summary>
    private sealed class PlannedDeletes
    {
        private readonly HashSet<(Side, string)> objects = new();
        private readonly HashSet<(Side, string)> paths = new();

        public PlannedDeletes(IEnumerable<SyncAction> deletes)
        {
            foreach (var delete in deletes)
            {
                if (delete.ExpectedTarget is { } victim)
                    objects.Add((delete.Target, victim.ObjectId));
                paths.Add((delete.Target, delete.Path.ToLowerInvariant()));
            }
        }

        /// <summary>Forgets a Delete that post-pass B turned into a kept folder (rule 24).</summary>
        public void Withdraw(SyncAction delete)
        {
            if (delete.ExpectedTarget is { } victim)
                objects.Remove((delete.Target, victim.ObjectId));
            paths.Remove((delete.Target, delete.Path.ToLowerInvariant()));
        }

        /// <summary>True when the item, or a folder between it and <paramref name="folder"/>, is deleted on the side.</summary>
        public bool Covers(Side side, ItemState item, string folder)
        {
            if (objects.Contains((side, item.ObjectId)))
                return true;
            for (var p = item.Path[..item.Path.LastIndexOf('/')]; PairPath.IsUnder(p, folder); p = p[..p.LastIndexOf('/')])
                if (paths.Contains((side, p.ToLowerInvariant())))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// True when a Delete under the folder differs from the folder's own Delete in provenance: ChoiceRemove (the
    /// one-sided choice effects count it), 's durable FirstSyncChoice flag, or NeedsReview (an
    /// unreviewed rule-19 child that still needs its own review). A mixed folder is not collapsed, so it never hides
    /// a delete the first-sync review must still guard, and an ordinary rule-6 folder delete never absorbs an
    /// unreviewed rule-19 child.
    /// </summary>
    private static bool MixedProvenance(Draft draft, SyncAction folderDelete, List<int>? deletesUnder) =>
        (deletesUnder ?? new List<int>()).Any(j =>
            draft[j].Action.Kind == ActionKind.Delete &&
            (draft[j].Action.ChoiceRemove != folderDelete.ChoiceRemove ||
             draft[j].Action.FirstSyncChoice != folderDelete.FirstSyncChoice ||
             draft[j].Action.NeedsReview != folderDelete.NeedsReview));

    /// <summary>
    /// Rule 23: one recovery-first Delete for the folder; the descendant Deletes it covers are marked removed. The
    /// collapsed folder keeps its own FirstSyncChoice provenance (decision 71), so the guard sees the same reason.
    /// </summary>
    private static void CollapseFolderDelete(Draft draft, int index, List<int>? deletesUnder, bool[] removed)
    {
        var (row, action) = draft[index];
        draft[index] = (row, action with { Rule = 23, RecoveryFirst = true });
        foreach (var j in deletesUnder ?? new List<int>())
            removed[j] |= draft[j].Action.Kind == ActionKind.Delete;
    }

    /// <summary>Rule 24: recreate the folder on the deleting side for the kept contents (edit beats delete).</summary>
    private static void KeepFolder(Context ctx, Draft draft, int index)
    {
        var (row, action) = draft[index];
        var mkdir = NewAction(24, ActionKind.MkDir, Opposite(action.Target), action.Path, null, action.ExpectedTarget,
            null, false, action.ObjectKey);
        draft[index] = (row, mkdir with { ActionId = action.ActionId });
        AddNotice(ctx, 24, NoticeKind.EditBeatsDelete, action.Path, PlanNotices.EditBeatsDelete);
    }

    // ---- post-pass A: folder moves (rule 25), per side ------------------------

    /// <summary>
    /// Drops the child moves a folder move implies on the same side, then moves every path that names an item of a
    /// row whose item on the folder move's side sits under the old folder. Paths on the other side never move.
    /// </summary>
    private static Draft PostMoves(Context ctx)
    {
        var byFrom = ctx.Draft.Where(d => IsFolderMove(d.Action)).Select(d => d.Action)
            .ToLookup(m => (m.Target, m.FromPath ?? m.Path), PathKeyComparer.Instance);
        bool Implied(SyncAction child) => PairPath.Ancestors(child.FromPath ?? child.Path).Any(folder =>
            byFrom[(child.Target, folder)].Any(m => m.ActionId != child.ActionId && ImpliedBy(child, m)));
        var kept = ctx.Draft.Where(d => !(d.Action.Kind == ActionKind.Move && Implied(d.Action))).ToList();
        if (byFrom.Count == 0)
            return kept;                                                                  // no folder move: nothing rebases
        var steps = ActionPhases.Assign(kept.Select(d => d.Action).ToList());
        var order = Enumerable.Range(0, kept.Count).Where(i => steps.ContainsKey(kept[i].Action.ActionId))
            .OrderBy(i => steps[kept[i].Action.ActionId].Phase).ThenBy(i => steps[kept[i].Action.ActionId].Level)
            .ThenBy(i => i).ToList();
        var applied = new List<(PlanRow Row, SyncAction Move)>();
        foreach (var i in order)
        {
            var (row, action) = kept[i];
            var step = steps[action.ActionId];
            var tracked = new Dictionary<Side, string?> { [Side.A] = row.A?.Path, [Side.B] = row.B?.Path };
            foreach (var (moveRow, move) in applied)
                if (!ReferenceEquals(moveRow, row) && RunsAfter(step, steps[move.ActionId]))
                    action = RebaseForMove(ctx, tracked, action, move);
            kept[i] = (row, action);
            if (IsFolderMove(action))
                applied.Add((row, action));
        }
        return kept;
    }

    private static bool IsFolderMove(SyncAction action) => action.Kind == ActionKind.Move &&
        (action.ExpectedTarget?.Kind == ItemKind.Folder || action.Source?.Kind == ItemKind.Folder);

    /// <summary>A child move on the same side whose source and destination both follow the folder move.</summary>
    private static bool ImpliedBy(SyncAction child, SyncAction folderMove)
    {
        var from = folderMove.FromPath ?? folderMove.Path;
        var childFrom = child.FromPath ?? child.Path;
        return child.Target == folderMove.Target && PairPath.IsUnder(childFrom, from) &&
            PathEqual(child.Path, PairPath.Rebase(childFrom, from, folderMove.Path));
    }

    private static bool RunsAfter(Step x, Step m) => x.Phase > m.Phase || (x.Phase == m.Phase && x.Level > m.Level);

    /// <summary>Moves the action's paths that name this row's item on the move's side, if that item was under the folder.</summary>
    private static SyncAction RebaseForMove(Context ctx, Dictionary<Side, string?> tracked, SyncAction action,
        SyncAction move)
    {
        var side = move.Target;
        var item = tracked[side];
        var from = move.FromPath ?? move.Path;
        if (item is null || !PairPath.IsSelfOrUnder(item, from))
            return action;
        var moved = PairPath.Rebase(item, from, move.Path);
        tracked[side] = moved;
        if (!ctx.RebasedBy.TryGetValue(action.ActionId, out var by))
            ctx.RebasedBy[action.ActionId] = by = new HashSet<string>(StringComparer.Ordinal);
        by.Add(move.ActionId);
        // Rebase the ancestor, not the entire child name: a child's independent leaf-case
        // rename must retain its requested spelling after a case-only parent move.
        return PairPath.Same(from, move.Path)
            ? MovePaths(action, side, from, move.Path, mirror: true)
            : MovePaths(action, side, item, moved, mirror: true);
    }

    /// <summary>
    /// Rewrites the action's paths on <paramref name="side"/> from <paramref name="from"/> to <paramref name="to"/>:
    /// target, origin and expected state when the action targets that side, else only its source. With
    /// <paramref name="mirror"/>, a create on the other side at the source's own path follows the source.
    /// </summary>
    private static SyncAction MovePaths(SyncAction action, Side side, string from, string to, bool mirror)
    {
        string Map(string path) => PairPath.IsSelfOrUnder(path, from) ? PairPath.Rebase(path, from, to) : path;
        if (action.Target == side)
            return action with
            {
                Path = Map(action.Path),
                FromPath = action.FromPath is null ? null : Map(action.FromPath),
                ExpectedTarget = action.ExpectedTarget is { } e ? e with { Path = Map(e.Path) } : null,
            };
        if (action.Source is not { } source)
            return action;
        var path = mirror && PairPath.Same(action.Path, source.Path) ? Map(action.Path) : action.Path;
        return action with { Path = path, Source = source with { Path = Map(source.Path) } };
    }
}
