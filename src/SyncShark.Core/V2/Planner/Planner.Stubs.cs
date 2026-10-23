namespace Massdex.SyncShark.V2;

/// <summary>
/// Rule 35, the one planner policy point for online-only cloud-files stubs . A stub is an item
/// flagged <see cref="ItemFlags.Placeholder"/>: it exists, its content is only on the other side, and it is never
/// read, hashed or treated as a deletion. Today's policy downloads: a stub opposite a readable file is replaced by
/// that file (an Update with no Recovery copy, whatever the modification times say); a stub with no counterpart is
/// only reported. A later cloud-files provider mode (backlog) changes this one method to "restore the pointer".
/// </summary>
public static partial class Planner
{
    /// <summary>The rule that replaces an online-only stub with the other side's file.</summary>
    public const int StubRule = 35;   // 34 is reserved by the exclusions design (exclusions.md)

    private static bool IsStub(ItemState? item) =>
        item is { Kind: ItemKind.File } && item.Flags.HasFlag(ItemFlags.Placeholder);

    /// <summary>Applies rule 35 when either side is a stub; true when the row is decided.</summary>
    private static bool TryStub(Context ctx, PlanRow row)
    {
        var stubSide = IsStub(row.A) ? Side.A : IsStub(row.B) ? Side.B : (Side?)null;
        if (stubSide is not { } side)
            return false;
        var stub = Cur(row, side)!;
        var other = Cur(row, Opposite(side));
        if (other is null)
        {
            AddNotice(ctx, StubRule, NoticeKind.Info, stub.Path, PlanNotices.StubWithoutCounterpart); // never a delete
            return true;
        }
        if (!Comparable(other))
        {
            AddNotice(ctx, 30, NoticeKind.Info, stub.Path, PlanNotices.CannotRead);         // nothing usable to download
            return true;
        }
        if (other.Hash is null)
        {
            SkipUnconfirmedRow(ctx, row);                                                 // HashRequests asks for it
            return true;
        }
        var key = row.Base?.ObjectKey ?? PlanIds.ObjectKey(ctx.PairId, Opposite(side), other.ObjectId);
        Emit(ctx, row, StubRule, ActionKind.Update, side, stub.Path, null, other, stub, false, key);
        return true;
    }

    /// <summary>The download source of a stub row needs its hash (the replacement is verified against it).</summary>
    private static void StubHashNeed(PlanRow row, SortedSet<string> a, SortedSet<string> b)
    {
        if (IsStub(row.A) && Comparable(row.B) && row.B!.Hash is null) b.Add(row.B.Path);
        else if (IsStub(row.B) && Comparable(row.A) && row.A!.Hash is null) a.Add(row.A.Path);
    }

    /// <summary>What rule 35 does in a plan: the stubs replaced by a download, their bytes, and the stubs only reported.</summary>
    public static (int Replaced, long Bytes, int WithoutCounterpart) StubSummary(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var replaced = plan.Actions.Where(action => action.Rule == StubRule && action.Kind == ActionKind.Update).ToArray();
        return (replaced.Length, replaced.Sum(action => action.Source?.Size ?? 0),
            plan.Notices.Count(notice => notice.Rule == StubRule &&
                notice.ReasonKey == PlanNotices.StubWithoutCounterpart.Key));
    }
}
