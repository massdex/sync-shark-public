namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Select the item action from the observed row and first-run policy.</summary>
public static class Decide
{
    /// <summary>For server deletion only, pass raw local/deletion times and Row.DeletionClock to TimeRules; do not adjust client-modified times.</summary>
    public static SyncAction For(Row row, FirstRunPolicy policy, DateTimeOffset comparedAt, bool localContentDirty, (Side Excluded, bool Owed)? exclusion = null) => throw new NotImplementedException();

    /// <summary>LocalMatchesBaseline for the selected Decide operation.</summary>
    internal static bool LocalMatchesBaseline(Entry? local, SyncedEntry? saved) => throw new NotImplementedException();
    /// <summary>DropboxMatchesBaseline for the selected Decide operation.</summary>
    internal static bool DropboxMatchesBaseline(Entry? dropbox, SyncedEntry? saved) => throw new NotImplementedException();
    /// <summary>DropboxIdentityHolds for the selected Decide operation.</summary>
    internal static bool DropboxIdentityHolds(Entry? dropbox, SyncedEntry saved) => throw new NotImplementedException();
    /// <summary>LocalChanged for the selected Decide operation.</summary>
    internal static bool? LocalChanged(Entry entry, SyncedEntry synced, string? localHash, bool localContentDirty) => throw new NotImplementedException();
    /// <summary>DropboxChanged for the selected Decide operation.</summary>
    internal static bool DropboxChanged(Entry entry, SyncedEntry synced) => throw new NotImplementedException();
    /// <summary>Changed for the selected Decide operation.</summary>
    internal static bool? Changed(Side side, Entry entry, SyncedEntry synced, string? localHash, bool localContentDirty) => throw new NotImplementedException();
    /// <summary>IsFreeIncoming for the selected Decide operation.</summary>
    public static bool IsFreeIncoming(Entry? local, Entry? dropbox, SyncedEntry? localBinding, SyncedEntry? dropboxBinding) => throw new NotImplementedException();
    /// <summary>CrossedFar for the selected Decide operation.</summary>
    public static Side? CrossedFar(Entry? local, Entry? dropbox, SyncedEntry? binding) => throw new NotImplementedException();
}
