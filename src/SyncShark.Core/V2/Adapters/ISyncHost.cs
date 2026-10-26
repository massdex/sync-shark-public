namespace Massdex.SyncShark;

/// <summary>The sync-host surface the window and its partials call, implemented by core v1 and core v2.</summary>
/// <remarks>
/// The members are grouped into one interface per screen area (the <c>ISync*Host</c> interfaces in this folder),
/// and <see cref="ISyncHost"/> composes them without declaring any member of its own. Signatures are copied
/// verbatim from the v1 <see cref="SyncAuthorityRuntimeHost"/> declarations (and from
/// <see cref="SyncAuthorityDatabase"/> for the eight members marked "lifted"). The list is exactly the members the
/// window calls; nothing the UI does not call is added, and overloads it never calls are omitted.
/// </remarks>
public interface ISyncHost :
    ISyncHostState,
    ISyncPairHost,
    ISyncRunHost,
    ISyncFirstSyncHost,
    ISyncConflictHost,
    ISyncHistoryHost,
    ISyncRecoveryHost,
    ISyncStorageHost,
    ISyncSettingsHost,
    ISyncAccountHost,
    ISyncSharingHost,
    ISyncExplorerHost
{
}
