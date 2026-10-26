namespace Massdex.SyncShark.V2;

/// <summary>Planner rule 32 for one-sided rows: the user's per-folder first-sync choice (first-sync-choices.md).</summary>
public static partial class Planner
{
    /// <summary>
    /// Applies the longest matching one-sided choice to a first-sync row with no baseline and one side present, when
    /// the absent side has nothing at that path ignoring case; if the absent side's inventory does not cover the path
    /// the row is deferred (rule 30 notice, no effect). Remove deletes recovery-first, copy creates on the
    /// absent side; both override rule 19's deletion evidence and raise no per-row notice.
    /// </summary>
    private static bool TryOneSided(Context ctx, PlanRow row)
    {
        if (ctx.OneSided.Count == 0 || !ctx.Options.FirstSync || row.Base is not null || (row.A is null) == (row.B is null))
            return false;
        var present = row.A is not null ? Side.A : Side.B;
        var cur = Cur(row, present)!;
        var absent = Opposite(present);
        if (ctx.Occupancy(absent).ContainsKey(cur.Path) ||
            OneSidedChoiceResolver.Resolve(ctx.OneSided, present, cur.Path) is not { } choice)
            return false;
        if (!(absent == Side.A ? row.Ev.KnownA : row.Ev.KnownB))
        {
            // The choice applies but no complete inventory confirms the absence: defer the row (no effect, never the
            // default table against the user's choice) and retry it like unconfirmed content (rule 30).
            AddNotice(ctx, 30, NoticeKind.Info, cur.Path, PlanNotices.ContentUnconfirmed);
            return true;
        }
        if (choice.Remove)
            Emit(ctx, row, 32, ActionKind.Delete, present, cur.Path, null, null, cur, true, null,
                choiceRemove: true, firstSyncChoice: true); // rule 32 remove
        else
            CreateOn(ctx, row, 32, absent, cur.Path, cur, PlanIds.ObjectKey(ctx.PairId, present, cur.ObjectId)); // rule 32 copy
        return true;
    }
}
