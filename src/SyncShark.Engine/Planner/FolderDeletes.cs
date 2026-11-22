namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Folder-delete classification and decision</summary>
public static class FolderDeletes
{
    /// <summary>Decide whether to remove the folder given its observed contents and protected descendants.</summary>
    public static SyncAction For(Row row, bool holdsProtectedItem, TextKey? unreadableCause, FirstRunPolicy policy, DateTimeOffset comparedAt, bool localContentDirty) => throw new NotImplementedException();

    /// <summary>Classify for the selected FolderDeletes operation.</summary>
    public static FolderVerdict Classify(bool excluded, bool nestedRoot, bool link, bool unreadable, bool movedOut) => throw new NotImplementedException();
    /// <summary>SelectBranch for the selected FolderDeletes operation.</summary>
    public static bool SelectBranch(FolderVerdict verdict, bool hasSurvivorAtOrBelow, bool hasSelectedAncestor) => throw new NotImplementedException();
    /// <summary>Classifies folderverdict for the selected operation.</summary>
    public enum FolderVerdict { Removed, Survives }
}
