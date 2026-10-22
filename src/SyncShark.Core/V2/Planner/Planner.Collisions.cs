using Draft = System.Collections.Generic.List<(Massdex.SyncShark.V2.PlanRow Row, Massdex.SyncShark.V2.SyncAction Action)>;

namespace Massdex.SyncShark.V2;

/// <summary>Planner post-pass C: creates whose destination is vacated or occupied (rules 4, 5, 12-14; planner.md 4.7).</summary>
public static partial class Planner
{
    /// <summary>Resolves every create against the target side's occupancy; moves go first so a first-sync move owns
    /// the occupant row's mirror create.</summary>
    private static Draft PostCollisions(Context ctx)
    {
        var draft = ctx.Draft;
        var dropped = new HashSet<int>();
        var all = Enumerable.Range(0, draft.Count);
        var creates = all.Where(i => draft[i].Action.Kind == ActionKind.Move)
            .Concat(all.Where(i => draft[i].Action.Kind != ActionKind.Move && ActionPhases.IsCreate(draft[i].Action.Kind)))
            .ToList();
        foreach (var i in creates)
            if (!dropped.Contains(i) && ActionPhases.IsCreate(draft[i].Action.Kind) && !Vacate(ctx, draft, dropped, i))
                ResolveOccupied(ctx, draft, dropped, i);
        RedirectDuplicateCreates(ctx, draft, dropped);
        return draft.Where((_, i) => !dropped.Contains(i)).ToList();
    }

    /// <summary>A delete on the same target and path clears the way for this create (rules 12 and 13).</summary>
    private static bool Vacate(Context ctx, Draft draft, HashSet<int> dropped, int i)
    {
        var (row, action) = draft[i];
        var vacating = ctx.Index.FirstDeleteAt(draft, action.Target, action.Path, row);
        if (vacating < 0 || dropped.Contains(vacating))
            return false;
        var delete = draft[vacating];
        var victim = delete.Action.ExpectedTarget;
        var incoming = Incoming(row, action);
        if (victim is { Kind: ItemKind.File } && incoming is { Kind: ItemKind.File } &&
            Content.SameContent(victim, incoming) is null)
        {
            // Unconfirmed: the incoming create does nothing (the victim's own evidence-based Delete still stands).
            NeedHashes(ctx, row, action, incoming, victim, Cur(delete.Row, action.Target)?.Path ?? victim.Path, action.Target);
            SkipUnconfirmed(ctx, draft, dropped, i, delete.Row);
            return true;
        }
        var identical = victim is not null && incoming is not null && Content.SameContent(victim, incoming) == true;
        draft[vacating] = (delete.Row, delete.Action with { Rule = identical ? 12 : 13, RecoveryFirst = true });
        if (!identical)
            AddNotice(ctx, 13, NoticeKind.AutoConflict, action.Path, PlanNotices.AutoConflict);
        return true;
    }

    /// <summary>
    /// A destination an item occupies once this plan's folder moves have run (another row's item, or this row's own
    /// item that the row does not move or delete away): identical, first-sync newer wins, or keep both.
    /// </summary>
    private static void ResolveOccupied(Context ctx, Draft draft, HashSet<int> dropped, int i)
    {
        var (row, action) = draft[i];
        if (Occupant(ctx, draft, dropped, action.Target, action.Path) is not { } found)
            return;
        var (occRow, occupant, occupantPath) = found;
        if (ReferenceEquals(occRow, row) && OwnVacates(ctx, draft, dropped, row, action, occupant))
            return;
        if (action.Kind == ActionKind.MkDir && occupant.Kind == ItemKind.Folder)
        {
            dropped.Add(i);                                                               // folder onto folder
            return;
        }
        var incoming = Incoming(row, action);
        if (incoming is { Kind: ItemKind.File } && occupant.Kind == ItemKind.File &&
            Content.SameContent(occupant, incoming) is null)
        {
            NeedHashes(ctx, row, action, incoming, occupant, occupantPath, action.Target); // never keep-both or newer-wins
            SkipUnconfirmed(ctx, draft, dropped, i, occRow);                              // on maybe-identical files
            return;
        }
        var plainFiles = incoming is { Kind: ItemKind.File } && occupant.Kind == ItemKind.File && !Flagged(occupant) &&
            !OccupantBusy(ctx, draft, dropped, i, occRow, action);
        var same = plainFiles && Content.SameContent(occupant, incoming!) == true;
        if (!same) PlanProvenance.Current.Value?.Rejected(action, "occupancy");
        var newerWins = ctx.Options.FirstSync && (action.Kind == ActionKind.Move || ReferenceEquals(occRow, row) ||
            ctx.RebasedBy.ContainsKey(action.ActionId));
        if (same)
            OntoIdentical(ctx, draft, dropped, i, occupant, occRow, incoming!);
        else if (plainFiles && newerWins)
            FirstSyncOccupied(ctx, draft, dropped, i, occupant, occRow, incoming!);
        else if (plainFiles && ReferenceEquals(occRow, row) && action.Rule is 10 or 11 &&
                 ctx.RebasedBy.ContainsKey(action.ActionId))
        {
            // RQ-035: a move/edit conflict is still keep-both when the parent move brings
            // both versions to one path. This is not an unrelated rule-14 occupant.
            dropped.UnionWith(ctx.Index.OfRow(row));
            WithdrawTwoPlaceNotice(ctx, row);
            var (a, b) = action.Target == Side.B ? (incoming!, occupant) : (occupant, incoming!);
            KeepBothPair(ctx, row, row.Base!.ObjectKey, a, b, split: true);
            return;
        }
        else
            HoldIncoming(ctx, draft, dropped, i, occupant);
        if (!same && !(plainFiles && newerWins))
            DropMirrors(ctx, draft, dropped, occRow, action);
    }

    /// <summary>
    /// The row and item at <paramref name="path"/> on <paramref name="side"/> after this plan's folder moves: an item a
    /// folder move carries there (with its new path), else the item already there unless a folder move carries it away.
    /// </summary>
    private static (PlanRow Row, ItemState Item, string CurrentPath)? Occupant(Context ctx, Draft draft,
        HashSet<int> dropped, Side side, string path)
    {
        var occupancy = ctx.Occupancy(side);
        var moves = ctx.Index.FolderMoves.Count == 0 ? new List<SyncAction>() : ctx.Index.FolderMoves
            .Where(j => !dropped.Contains(j) && draft[j].Action.Target == side && IsFolderMove(draft[j].Action))
            .Select(j => draft[j].Action).ToList();
        foreach (var move in moves)
            if (PairPath.IsUnder(path, move.Path) &&
                occupancy.TryGetValue(PairPath.Rebase(path, move.Path, move.FromPath ?? move.Path), out var carried))
                return (ctx.Rows[carried.Row], carried.Item with { Path = path }, carried.Item.Path);
        if (!occupancy.TryGetValue(path, out var here) ||
            moves.Any(m => PairPath.IsUnder(here.Item.Path, m.FromPath ?? m.Path)))
            return null;
        return (ctx.Rows[here.Row], here.Item, here.Item.Path);
    }

    /// <summary>Records the unhashed sides of an unconfirmed collision at their current (inventory) paths; the other
    /// item is on <paramref name="otherSide"/> (the target for an occupant, the source side for a duplicate create).</summary>
    private static void NeedHashes(Context ctx, PlanRow row, SyncAction action, ItemState incoming, ItemState other,
        string otherPath, Side otherSide)
    {
        var source = Opposite(action.Target);
        if (incoming.Hash is null && Cur(row, source) is { } current)
            ctx.HashNeeds.Add((source, current.Path));
        if (other.Hash is null)
            ctx.HashNeeds.Add((otherSide, otherPath));
    }

    /// <summary>The row's own item is no obstacle when the row moves it away (e.g. rule 20) or deletes it first (26, 32).</summary>
    private static bool OwnVacates(Context ctx, Draft draft, HashSet<int> dropped, PlanRow row, SyncAction action,
        ItemState own) =>
        (action.Kind == ActionKind.Move && action.FromPath is not null && PairPath.Same(action.FromPath, own.Path)) ||
        ctx.Index.OfRow(row).Where(j => !dropped.Contains(j)).Select(j => draft[j].Action).Any(d =>
            d.Kind == ActionKind.Delete && d.Target == action.Target && PairPath.Same(d.Path, action.Path));

    /// <summary>The incoming version: the source side's current item (current content even for rule 28's move) at the
    /// path the action's source has after any rebase.</summary>
    private static ItemState? Incoming(PlanRow row, SyncAction action)
    {
        var current = Cur(row, Opposite(action.Target));
        if (current is null || action.Source is null)
            return current ?? action.Source;
        return current with { Path = action.Source.Path };
    }

    /// <summary>True when the occupant's row plans anything besides this create and its mirror toward the source side.</summary>
    private static bool OccupantBusy(Context ctx, Draft draft, HashSet<int> dropped, int i, PlanRow occRow,
        SyncAction action) =>
        ctx.Index.OfRow(occRow).Where(j => j != i && !dropped.Contains(j)).Any(j => !IsMirror(draft[j].Action, action));

    /// <summary>
    /// Incoming and occupant not confirmed equal or different (hashing failed or pending): no effect for the incoming
    /// row, the occupant row's mirror create is dropped too; Info notice (rule 30). HashRequests asks for the hashes.
    /// </summary>
    private static void SkipUnconfirmed(Context ctx, Draft draft, HashSet<int> dropped, int i, PlanRow occRow)
    {
        var (row, action) = draft[i];
        dropped.UnionWith(ctx.Index.OfRow(row));
        dropped.UnionWith(ctx.Index.OfRow(occRow).Where(j => IsMirror(draft[j].Action, action)));
        if (ReferenceEquals(occRow, row))
            WithdrawTwoPlaceNotice(ctx, row);
        AddNotice(ctx, 30, NoticeKind.Info, action.Path, PlanNotices.ContentUnconfirmed);
    }

    private static bool IsMirror(SyncAction candidate, SyncAction action) =>
        ActionPhases.IsCreate(candidate.Kind) && candidate.Target == Opposite(action.Target) &&
        PairPath.Same(candidate.Path, action.Path);

    private static bool Flagged(ItemState item) =>
        (item.Flags & (ItemFlags.Ignored | ItemFlags.Unreadable | ItemFlags.Locked | ItemFlags.Placeholder)) != 0;

    /// <summary>
    /// Rule 12: an identical file is already there. A move completes onto it: Adopt records the pair and the old path
    /// is removed (recovery first) with the move itself as evidence (the moved item is the Delete's Source). Any other
    /// create is dropped; the next run adopts the occupant.
    /// </summary>
    private static void OntoIdentical(Context ctx, Draft draft, HashSet<int> dropped, int i, ItemState occupant,
        PlanRow occRow, ItemState incoming)
    {
        var (row, action) = draft[i];
        if (action.Kind != ActionKind.Move || action.FromPath is null || action.ExpectedTarget is null)
        {
            dropped.Add(i);
            return;
        }
        var target = action.Target;
        var (a, b) = target == Side.B ? (incoming, occupant) : (occupant, incoming);
        var successor = NewAction(12, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, action.ObjectKey)
            with { ActionId = action.ActionId };
        draft[i] = (row, occRow.Base is { } occBase && !ReferenceEquals(occRow, row)
            ? RetiringBaseline(successor, occBase.ObjectKey) : successor);
        DropDependants(ctx, draft, dropped, i, row, target, action.Path);
        DropMirrors(ctx, draft, dropped, occRow, action);
        Append(ctx, draft, row, NewAction(12, ActionKind.Delete, target, action.FromPath, null, incoming,
            action.ExpectedTarget, true, null));
    }

    /// <summary>
    /// First sync, a move or a folder-rebased create onto a different file the newer modification
    /// time wins and the older version goes to Recovery; a tie within the tie window holds both rows for the user.
    /// </summary>
    private static void FirstSyncOccupied(Context ctx, Draft draft, HashSet<int> dropped, int i, ItemState occupant,
        PlanRow occRow, ItemState incoming)
    {
        var (row, action) = draft[i];
        DropMirrors(ctx, draft, dropped, occRow, action);
        var own = ReferenceEquals(occRow, row);
        if (own)   // the row's two-place copy (rules 10/11) collided with itself: no longer a keep-both outcome
            WithdrawTwoPlaceNotice(ctx, row);
        var diff = incoming.Mtime - occupant.Mtime;
        if (diff.Duration() <= ctx.Options.TieWindow)
        {
            HoldClash(ctx, draft, dropped, i, occRow);                                     // rule 5
            return;
        }
        AddNotice(ctx, 4, NoticeKind.AutoConflict, action.Path, PlanNotices.AutoConflict);
        var key = own ? row.Base?.ObjectKey ?? action.ObjectKey : occRow.Base?.ObjectKey;
        if (diff > TimeSpan.Zero)
            IncomingWins(ctx, draft, i, occupant, occRow, incoming, own, key);
        else
            OccupantWins(ctx, draft, dropped, i, occupant, occRow, incoming, own, key);
    }

    /// <summary>Incoming newer: a move vacates the occupant to Recovery and lands; a create becomes a recovery-first Update.</summary>
    private static void IncomingWins(Context ctx, Draft draft, int i, ItemState occupant, PlanRow occRow,
        ItemState incoming, bool own, string? key)
    {
        var (row, action) = draft[i];
        if (action.Kind == ActionKind.Move)
        {
            Append(ctx, draft, occRow, NewAction(4, ActionKind.Delete, action.Target, occupant.Path, null, incoming, occupant, true, key));
            return;
        }
        var successor = NewAction(4, ActionKind.Update, action.Target, occupant.Path, null, incoming, occupant, true,
            own ? key : action.ObjectKey) with { ActionId = action.ActionId };
        draft[i] = (row, !own && key is not null ? RetiringBaseline(successor, key) : successor);
    }

    /// <summary>Occupant newer: it overwrites the incoming item (older to Recovery); a moved item's old copy goes to
    /// Recovery too, and an incoming identity that no longer exists is forgotten.</summary>
    private static void OccupantWins(Context ctx, Draft draft, HashSet<int> dropped, int i, ItemState occupant,
        PlanRow occRow, ItemState incoming, bool own, string? key)
    {
        var (row, action) = draft[i];
        var target = action.Target;
        dropped.Add(i);
        DropDependants(ctx, draft, dropped, i, row, target, action.Path);
        if (action.Kind == ActionKind.Move)
            Append(ctx, draft, row, NewAction(4, ActionKind.Delete, target, action.FromPath ?? action.Path, null, incoming,
                action.ExpectedTarget, true, action.ObjectKey));
        var successor = NewAction(4, ActionKind.Update, Opposite(target), incoming.Path, null, occupant, incoming, true,
            key ?? PlanIds.ObjectKey(ctx.PairId, target, occupant.ObjectId));
        if (action.Kind != ActionKind.Move && !own && action.ObjectKey is { } stale)
            successor = RetiringBaseline(successor, stale);
        Append(ctx, draft, occRow, successor);
    }

    /// <summary>Rule 5 for an occupied first-sync destination: one Hold at the path; both rows' actions are held.</summary>
    private static void HoldClash(Context ctx, Draft draft, HashSet<int> dropped, int i, PlanRow occRow)
    {
        var (row, action) = draft[i];
        dropped.UnionWith(ctx.Index.OfRow(row).Concat(ctx.Index.OfRow(occRow))
            .Where(j => j != i && draft[j].Action.Kind != ActionKind.Hold));
        var held = ctx.Index.At(Side.A, action.Path).Concat(ctx.Index.At(Side.B, action.Path))
            .Any(j => !dropped.Contains(j) && j != i && draft[j].Action.Kind == ActionKind.Hold &&
                PairPath.Same(draft[j].Action.Path, action.Path));
        if (held)
            dropped.Add(i);
        else
            draft[i] = (row, NewAction(5, ActionKind.Hold, Side.B, action.Path, null, null, null, false, null)
                with { ActionId = action.ActionId });
        AddNotice(ctx, 5, NoticeKind.UserDecision, action.Path, PlanNotices.NeedDecision);
    }

    /// <summary>The successor link owns the obsolete correspondence and retires it atomically when committed Done.
    /// A link can never both retain and retire one key; that would leave two claims on one object.</summary>
    private static SyncAction RetiringBaseline(SyncAction successor, string? obsolete)
    {
        if (obsolete is null)
            return successor;
        if (successor.ObjectKey is null || string.Equals(successor.ObjectKey, obsolete, StringComparison.Ordinal))
            throw new InvalidOperationException("Planner: a baseline successor must retain exactly one key claim.");
        return successor with { ObsoleteBaselineKey = obsolete };
    }

    /// <summary>Drops the occupant row's creates toward this create's source side at the same path.</summary>
    private static void DropMirrors(Context ctx, Draft draft, HashSet<int> dropped, PlanRow occRow, SyncAction action) =>
        dropped.UnionWith(ctx.Index.OfRow(occRow).Where(j => IsMirror(draft[j].Action, action)));

    /// <summary>Drops the same row's other actions on the target that address the destination (e.g. rule 28's Update).</summary>
    private static void DropDependants(Context ctx, Draft draft, HashSet<int> dropped, int i, PlanRow row, Side target,
        string path) =>
        dropped.UnionWith(ctx.Index.OfRow(row).Where(j => j != i && References(draft[j].Action, target, path)));
}
