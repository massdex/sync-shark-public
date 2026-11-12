using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Exclusion policy (section 7.7, D26): the action override, the protected-content notice, the conservative
/// preview predicate and subtree listing eligibility.</summary>
public static class Exclusions
{
    /// <summary>The action to execute for one excluded item, or null to skip it. A null decided action is the
    /// unsaved-item path, whose caller already built a one-sided Row with the other-side destination as its target.</summary>
    public static SyncAction? For(Side excludedSide, Entry item, Row row, SyncAction? decided)
    {
        var kind = SyncAction.CopyOn(item.IsFolder, PathNames.Other(excludedSide));
        if (decided is null) return new SyncAction(kind, row, Target: row.Context.Destination);
        var action = decided;
        // The kept side already has the newer state; do not sync it back to the excluded side.
        if (action.Kind is ActionKind.Upload or ActionKind.Download or ActionKind.DeleteLocal or ActionKind.DeleteDropbox
            && action.WrittenSide() == excludedSide)
            return null;
        if (action.Kind == ActionKind.DeferFolder
            && (excludedSide == Side.Local ? row.Dropbox is null : row.Local is null))
            return null;
        if (action.Kind == ActionKind.ReplaceKind)
            return new SyncAction(ActionKind.Notice, row,
                NoticeKind: IssueKind.NameClash, Cause: TextKey.ClashFileFolder);
        if (action.Kind != kind && action.Kind is not (ActionKind.Record or ActionKind.KeepBoth or ActionKind.Notice)
            && !action.IsInSync())
            return new SyncAction(ActionKind.Notice, row,
                NoticeKind: IssueKind.Failure, Cause: TextKey.FailChanged);
        return action;
    }

    /// <summary>The failure notice for an exclusion that would remove protected or skipped content.</summary>
    public static SyncAction Blocked(Row row) => new(ActionKind.Notice, row,
        NoticeKind: IssuePolicy.ProtectedExclusion.Kind, Cause: IssuePolicy.ProtectedExclusion.Cause);

    /// <summary>True when the item may still need a copy: no saved record or a saved folder, otherwise the side's
    /// change test. Local content without a hash counts as unknown, so it counts as a potential copy.</summary>
    public static bool MayCopyFile(Entry item, SyncedEntry? saved, Side excludedSide)
    {
        if (saved is null || saved.IsFolder) return true;
        return excludedSide == Side.Dropbox
            ? Decide.DropboxChanged(item, saved)
            : Decide.LocalChanged(item, saved, null, localContentDirty: false) != false;
    }

    /// <summary>True when the exclusion root is a folder that may be listed; callers already refused nested roots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ShouldList(Entry root) => root.IsFolder && root.Skip is null;
}
