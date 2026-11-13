namespace Massdex.SyncShark;

/// <summary>The four read commands the Engine host answers from its own store (bug #92). They take no product lock
/// and never start a runtime; the values are the shared names the registry rows use.</summary>
public enum EngineHostReadKind
{
    AutoSyncStatus,
    AutoSyncHistory,
    ConflictsList,
    AttentionList
}
