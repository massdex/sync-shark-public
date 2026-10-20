using System.Runtime.InteropServices;
using Draft = System.Collections.Generic.List<(Massdex.SyncShark.V2.PlanRow Row, Massdex.SyncShark.V2.SyncAction Action)>;

namespace Massdex.SyncShark.V2;

/// <summary>Planner post-pass C, rule 14: redirects to a keep-both label and the dependants that follow them.</summary>
public static partial class Planner
{
    /// <summary>Rule 14: the create goes to a labelled path and only its own dependants follow it.</summary>
    private static void RedirectToLabel(Context ctx, Draft draft, HashSet<int> dropped, int i)
    {
        var (row, action) = draft[i];
        var incoming = Cur(row, Opposite(action.Target)) ?? action.Source;
        string label;
        try { label = FreeLabel(ctx, action.Target, action.Path, SourceSideName(ctx.Options.Label, action.Target), incoming?.Mtime ?? default); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            HoldPlanningFailure(ctx, row, error);
            dropped.Add(i);
            return;
        }
        draft[i] = (row, action with { Rule = 14, Path = label });
        ctx.Index.Repathed(i, draft[i].Action);
        var followers = ctx.Index.OfRow(row).Where(j => References(draft[j].Action, action.Target, action.Path))
            .Concat(ctx.Index.RebasedBy(action.ActionId))
            .Where(j => j != i && !dropped.Contains(j)).ToHashSet();
        foreach (var j in followers)
        {
            draft[j] = (draft[j].Row, MovePaths(draft[j].Action, action.Target, action.Path, label, mirror: false));
            ctx.Index.Repathed(j, draft[j].Action);
        }
        AddNotice(ctx, 14, NoticeKind.KeepBoth, label, PlanNotices.KeepBoth);
    }

    private static bool References(SyncAction action, Side target, string path) =>
        action.Target == target && (PairPath.IsSelfOrUnder(action.Path, path) ||
            (action.FromPath is not null && PairPath.IsSelfOrUnder(action.FromPath, path)) ||
            (action.ExpectedTarget is not null && PairPath.IsSelfOrUnder(action.ExpectedTarget.Path, path)));

    /// <summary>Two creates at one target and path: the larger-key one is redirected (rule 14), or skipped while the
    /// two versions cannot be confirmed different (never keep both of possibly identical files).</summary>
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
                    RedirectToLabel(ctx, draft, dropped, members[k].i);
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
