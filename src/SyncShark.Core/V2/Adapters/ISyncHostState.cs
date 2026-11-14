namespace Massdex.SyncShark;

/// <summary>The host's observable state: run activity, authority-state flags and the pending authorization.</summary>
public interface ISyncHostState : IDisposable
{
    /// <summary>Whether a run is currently active.</summary>
    bool HasActiveRun { get; }

    /// <summary>The active run's snapshot, or null when none is running.</summary>
    SyncActiveRunSnapshot? ActiveRun { get; }

    /// <summary>Whether the sync database file exists yet.</summary>
    bool AuthorityStateExists { get; }

    /// <summary>The last activity-log failure, or null when none.</summary>
    string? ActivityLogError { get; }

    /// <summary>Whether the in-memory activity log has unsaved events.</summary>
    bool ActivityLogIncomplete { get; }

    /// <summary>How many activity events wait in memory to be saved.</summary>
    int ActivityLogPendingCount { get; }

    /// <summary>The v1 transfer limiter's resource snapshot (shows idle under core v2).</summary>
    SyncTransferResourceStatus TransferResourceStatus { get; }

    /// <summary>The authorization attempt waiting for the user, or null.</summary>
    SyncPendingAuthorizationSnapshot? PendingAuthorization { get; }

    /// <summary>Raised when an authorization flow wants the browser returned to the app.</summary>
    event Action? AuthorizationReturnRequested;

    /// <summary>Raised when a pending authorization attempt changes.</summary>
    event Action? AuthorizationAttemptChanged;
}
