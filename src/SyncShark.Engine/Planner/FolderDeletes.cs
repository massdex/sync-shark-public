namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Subtree selection and first Repair folder decisions without a second content walk.</summary>
public static class FolderDeletes
{
    public enum FolderVerdict { Removed, Survives }

    /// <summary>Ignore alone, changed content and shared mounts do not protect a subtree.</summary>
    public static FolderVerdict Classify(bool excluded, bool nestedRoot, bool link, bool unreadable, bool movedOut) =>
        excluded || nestedRoot || link || unreadable || movedOut ? FolderVerdict.Survives : FolderVerdict.Removed;

    /// <summary>Parent-first traversal selects a maximal free branch once.</summary>
    public static bool SelectBranch(FolderVerdict verdict, bool hasSurvivorAtOrBelow, bool hasSelectedAncestor) =>
        verdict == FolderVerdict.Removed && !hasSurvivorAtOrBelow && !hasSelectedAncestor;

    /// <summary>Use the completed comparison mark and actual read failure, if any.</summary>
    public static SyncAction For(Row row, bool holdsProtectedItem, TextKey? unreadableCause = null)
    {
        var context = row.Context;
        if (context.OutOfScope) return new(ActionKind.None, row, NoneReason.OutOfScope);
        if (unreadableCause is { } failure) return Failed(row, failure);
        if (context.Skipped || row.Local?.Skip is not null) return new(ActionKind.None, row, NoneReason.Skipped);
        if (context.Held || !context.CompleteInventory || row.Ambiguous || row.Synced is not null ||
            row.Local is not { IsFolder: true } folder || row.Dropbox is not null)
            return new(ActionKind.None, row, NoneReason.Held);
        if (context.Destination is not { } target || row.Deletion is { } evidence && !PathNames.Same(evidence.Path, target))
            return Failed(row, TextKey.FailChanged);
        if (row.Deletion is DeletionEvidence.LiveAtTarget or DeletionEvidence.LiveElsewhere) return Failed(row, TextKey.FailChanged);
        if (!holdsProtectedItem && !context.HoldsNestedRoot && context.DeletionEligible)
        {
            if (row.Deletion is null) return new(ActionKind.NeedDeletionEvidence, row, Target: target);
            if (row.Deletion is DeletionEvidence.DeletedPath) return new(ActionKind.DeleteLocal, row, Target: folder.Path);
        }
        return new(ActionKind.EnsureDropboxFolder, row, Target: target);
    }

    private static SyncAction Failed(Row row, TextKey cause) => new(ActionKind.Notice, row, NoticeKind: NoticeKind.Failure, Cause: cause);
}
