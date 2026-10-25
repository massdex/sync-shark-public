namespace Massdex.SyncShark.V2;

public static partial class Planner
{
    /// <summary>The changed side against an unchanged one (rules 2, 9, 20, 25, 28).</summary>
    private static void ChangedAgainstSame(Context ctx, PlanRow row, Side changed, RowState s, string key)
    {
        var same = Opposite(changed);
        var curS = changed == Side.A ? row.A! : row.B!;
        var curT = same == Side.A ? row.A! : row.B!;
        switch (s)
        {
            case RowState.Edited:
                Emit(ctx, row, 2, ActionKind.Update, same, curT.Path, null, curS, curT, false, key); // rule 2
                return;
            case RowState.Moved:
                ChangedMove(ctx, row, same, curS, curT, key);
                return;
            case RowState.MovedEdited:
                if (PairPath.SameLeafLocation(ctx.Mapping.TargetPath(same, curS.Path), curT.Path))
                {
                    // The content still needs propagating, but no move or invented target spelling does.
                    Emit(ctx, row, 28, ActionKind.Update, same, curT.Path, null, curS, curT, false, key);
                    return;
                }
                var old = changed == Side.A ? row.Base!.A : row.Base!.B;
                var moveSource = curS with { Hash = old.Hash, Size = old.Size, VersionId = old.VersionId, Mtime = old.Mtime };
                Emit(ctx, row, 28, ActionKind.Move, same, curS.Path, curT.Path, moveSource, curT, false, key); // rule 28
                var destination = ctx.Mapping.TargetPath(same, curS.Path);
                Emit(ctx, row, 28, ActionKind.Update, same, destination, null, curS, curT with { Path = destination }, false, key);
                return;
            default:
                throw new InvalidOperationException("Planner: unhandled row");
        }
    }

    private static void ChangedMove(Context ctx, PlanRow row, Side target, ItemState source, ItemState current, string key)
    {
        var rule = source.Kind == ItemKind.Folder ? 25 : PairPath.Same(source.Path, current.Path) ? 20 : 9;
        var path = ctx.Mapping.TargetPath(target, source.Path);
        if (PairPath.SameLeafLocation(path, current.Path))
        {
            SameLocation(ctx, row, rule, source, current, key);
            return;
        }
        if (PairPath.Same(path, current.Path) && source.Kind == ItemKind.File)
        {
            var content = Content.SameContent(source, current);
            if (content is null) { SkipUnconfirmedRow(ctx, row); return; }
            if (content == false) { Hold(ctx, row, 29, PlanNotices.NeedDecision); return; }
        }
        Emit(ctx, row, rule, ActionKind.Move, target, source.Path, current.Path, source, current, false, key);
    }

    // Competing leaf spellings need a metadata winner; content equality is not rename authority.
    private static void ResolveCaseConflict(Context ctx, PlanRow row, string key)
    {
        if (!ModificationTime.IsPlausible(row.A!.Mtime, ctx.Options.ObservedAt) ||
            !ModificationTime.IsPlausible(row.B!.Mtime, ctx.Options.ObservedAt) ||
            (row.A.Mtime - row.B.Mtime).Duration() <= ctx.Options.TieWindow)
            Hold(ctx, row, 20, PlanNotices.NeedDecision);
        else
        {
            var winner = row.A.Mtime > row.B.Mtime ? Side.A : Side.B;
            ChangedAgainstSame(ctx, row, winner, RowState.Moved, key);
            AddNotice(ctx, 20, NoticeKind.AutoConflict, PrimaryPath(row), PlanNotices.AutoConflict);
        }
    }

    // Ancestor-only spelling is the same location; adoption still requires content proof.
    private static void SameLocation(Context ctx, PlanRow row, int rule,
        ItemState source, ItemState current, string key)
    {
        var content = Content.SameContent(source, current);
        if (content == true)
            AdoptAction(ctx, row, rule, Side.B, row.B!.Path, row.A!, row.B!, key);
        else if (content is null)
            SkipUnconfirmedRow(ctx, row);
        else
            // Neither content changed against its own baseline: spelling is not winner authority.
            Hold(ctx, row, 29, PlanNotices.NeedDecision);
    }

}
