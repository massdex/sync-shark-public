namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static IReadOnlySet<(Side Side, string ObjectId)> MovingIdentitySet(PairDef pair,
        SqliteStateStore sql, IReadOnlyList<ItemState> a, IReadOnlyList<ItemState> b)
    {
        var result = new HashSet<(Side, string)>();
        foreach (var (side, rows) in new[] { (Side.A, a), (Side.B, b) })
        {
            var moved = rows.GroupBy(row => row.ObjectId).Where(group =>
                group.Select(row => row.Path).Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(group => group.Key).ToArray();
            foreach (var id in moved) result.Add((side, id));
            foreach (var basis in sql.BaselinesTouching(pair.PairId, side, moved, []))
            {
                result.Add((Side.A, basis.A.ObjectId));
                result.Add((Side.B, basis.B.ObjectId));
            }
        }
        return result;
    }

    private static SyncAction AfterUnitMove(SyncAction action, SyncAction move, ReconcileBatch batch)
    {
        if (action.Target != move.Target || action.ActionId == move.ActionId ||
            PairPath.Same(action.Path, move.Path) || !PairPath.IsSelfOrUnder(action.Path, move.Path)) return action;
        // A different occupant already at the destination is a genuine Vacate dependency.
        // Only a delete of a captured pre-move child is ordered after its enclosing move.
        if (action.Kind == ActionKind.Delete && !(action.ExpectedTarget is { } target &&
            move.FromPath is { } from && (action.Target == Side.A ? batch.RowsA : batch.RowsB)
                .Any(row => row.ObjectId == target.ObjectId && PairPath.IsSelfOrUnder(row.Path, from)))) return action;
        return action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.Delete or ActionKind.MkDir
            ? action with { AfterFolderMove = move.ActionId } : action;
    }

    private static bool BreaksMovingIdentity(SyncAction action, ReconcileBatch batch) =>
        action.Kind is ActionKind.CopyNew or ActionKind.Delete &&
        (action.Source is { } source && batch.MovingIdentities.Contains(
            (action.Target == Side.A ? Side.B : Side.A, source.ObjectId)) ||
         action.ExpectedTarget is { } target && batch.MovingIdentities.Contains((action.Target, target.ObjectId)));
}
