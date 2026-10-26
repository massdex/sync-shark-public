using System.Runtime.InteropServices;
using Draft = System.Collections.Generic.List<(Massdex.SyncShark.V2.PlanRow Row, Massdex.SyncShark.V2.SyncAction Action)>;

namespace Massdex.SyncShark.V2;

/// <summary>Planner post-pass C, rule 14: retain colliding incoming entities under explicit-choice notices.</summary>
public static partial class Planner
{
    /// <summary>Decisions 84/88: keep incoming content at its original identity; never invent a copy name.</summary>
    private static void HoldIncoming(Context ctx, Draft draft, HashSet<int> dropped, int i, ItemState occupant, ItemState? pendingOccupantSource = null)
    {
        var (row, action) = draft[i];
        var incoming = Incoming(row, action);
        dropped.UnionWith(ctx.Index.OfRow(row));
        dropped.UnionWith(ctx.Index.RebasedBy(action.ActionId));
        if (incoming is null) return;
        WithdrawTwoPlaceNotice(ctx, row);
        if (incoming.Kind == ItemKind.Folder || occupant.Kind == ItemKind.Folder)
        {
            ctx.Held.Add(action.Path);
            ctx.Held.Add(incoming.Path);
            if (action.FromPath is not null) ctx.Held.Add(action.FromPath);
        }
        AddNotice(ctx, 14, NoticeKind.UserDecision, action.Path,
            new NoticeReason("incoming-name-clash-" + ShortId.Hex16(action.Target.ToString(), incoming.ObjectId), "The destination is occupied. The incoming item is unchanged at its source. Rename it, replace the destination with Recovery, or dismiss."));
        var (a, b) = action.Target == Side.B ? (incoming, occupant) : (occupant, incoming);
        AttachConflictVersions(ctx, 14, action.Path, new(a, b, row.Base, Incoming: action with { ActionId = string.Empty }, PendingOccupantSource: pendingOccupantSource));
    }

    private static bool References(SyncAction action, Side target, string path) =>
        action.Target == target && (PairPath.IsSelfOrUnder(action.Path, path) ||
            (action.FromPath is not null && PairPath.IsSelfOrUnder(action.FromPath, path)) ||
            (action.ExpectedTarget is not null && PairPath.IsSelfOrUnder(action.ExpectedTarget.Path, path)));

    /// <summary>Two creates at one target and path: retain each later incoming entity under a notice (rule 14), or skip
    /// the group while its versions cannot be confirmed different.</summary>
    private static void RedirectDuplicateCreates(Context ctx, Draft draft, HashSet<int> dropped)
    {
        static (Side, string) Key(SyncAction a) => (a.Target, a.Path.ToLowerInvariant());
        var creates = draft.Select((d, i) => (d, i))
            .Where(x => !dropped.Contains(x.i) && ActionPhases.IsCreate(x.d.Action.Kind)).ToList();
        var counts = new Dictionary<(Side, string), int>();   // count first: only shared keys are grouped
        foreach (var (d, _) in creates)
            CollectionsMarshal.GetValueRefOrAddDefault(counts, Key(d.Action), out _)++;
        var groups = creates.Where(x => counts[Key(x.d.Action)] > 1).GroupBy(x => Key(x.d.Action)).ToList();
        foreach (var group in groups)
        {
            var members = group.OrderBy(x => x.d.Action.ObjectKey ?? string.Empty, StringComparer.Ordinal).ToList();
            if (AnyUnconfirmed(members.Select(m => Incoming(m.d.Row, m.d.Action)).ToList()))
                SkipUnconfirmedGroup(ctx, draft, dropped, members.Select(m => m.i).ToList());
            else
                for (var k = 1; k < members.Count; k++)
                    HoldIncoming(ctx, draft, dropped, members[k].i,
                        Incoming(members[0].d.Row, members[0].d.Action)! with { Path = members[0].d.Action.Path },
                        Incoming(members[0].d.Row, members[0].d.Action));
        }
    }

    /// <summary>True when some two incoming files cannot be confirmed equal or different.</summary>
    private static bool AnyUnconfirmed(IReadOnlyList<ItemState?> incoming)
    {
        for (var x = 0; x < incoming.Count; x++)
            for (var y = x + 1; y < incoming.Count; y++)
                if (incoming[x] is { Kind: ItemKind.File } a && incoming[y] is { Kind: ItemKind.File } b &&
                    Content.SameContent(a, b) is null)
                    return true;
        return false;
    }

    /// <summary>Every create of an unconfirmed duplicate group is skipped (none survives), their hashes requested.</summary>
    private static void SkipUnconfirmedGroup(Context ctx, Draft draft, HashSet<int> dropped, IReadOnlyList<int> members)
    {
        foreach (var i in members)
        {
            var (row, action) = draft[i];
            var source = Opposite(action.Target);
            if (Cur(row, source) is { Kind: ItemKind.File, Hash: null } current)
                ctx.HashNeeds.Add((source, current.Path));
            SkipUnconfirmed(ctx, draft, dropped, i, row);
        }
    }
}
