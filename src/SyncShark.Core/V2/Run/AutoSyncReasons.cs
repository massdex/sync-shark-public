namespace Massdex.SyncShark.V2;

/// <summary>Why continuous sync holds a pair: each stable key and its wording live here and nowhere else.</summary>
public static class AutoSyncReasons
{
    /// <summary>The pair's first sync has not been reviewed and applied yet.</summary>
    public static readonly NoticeReason FirstSyncNeeded = new("first-sync-needed", "review the first sync to start syncing");

    /// <summary>An account lost access (expired or revoked sign-in); waits for reconnect.</summary>
    public static readonly NoticeReason AccountAccess = new("account-access", "reconnect the account to continue syncing");

    /// <summary>A folder of the pair is missing or was replaced, so it cannot be watched; retried at each safety poll.</summary>
    public static readonly NoticeReason WatchUnavailable = new("watch-unavailable", "a synced folder is not available");

    /// <summary>Run refused the pair; the detail is replaced by Run's own refusal text.</summary>
    public static readonly NoticeReason Refused = new("run-refused", "sync was refused");
}
