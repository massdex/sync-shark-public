namespace Massdex.SyncShark;

/// <summary>The account connect/disconnect and binding surface the window calls.</summary>
public interface ISyncAccountHost
{
    /// <summary>Loads one account binding, or null.</summary>
    SyncAccountBinding? LoadAccountBinding(string? connectionId = null);

    /// <summary>Whether any account is connected.</summary>
    Task<bool> IsAnyAccountConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>Connects a Google Drive account.</summary>
    Task<GoogleDriveConnectionBinding> ConnectGoogleDriveAsync(Func<Uri, Task> openPage, CancellationToken cancellationToken = default, string? connectionId = null);

    /// <summary>Gets an account's current status.</summary>
    Task<SyncAccountStatus> GetAccountStatusAsync(bool verifyLive = true, CancellationToken cancellationToken = default, string? connectionId = null);

    /// <summary>Enables or disables Google shared drives for a connection.</summary>
    Task<GoogleDriveConnectionBinding> SetGoogleSharedDrivesEnabledAsync(string connectionId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>The provider connections the app knows.</summary>
    IReadOnlyList<ProviderConnectionRecord> LoadProviderConnections();

    /// <summary>Loads the Dropbox connection binding, or null.</summary>
    DropboxConnectionBinding? LoadDropboxConnection(string? connectionId = null);

    /// <summary>Whether Dropbox is connected.</summary>
    Task<bool> IsDropboxConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>Reauthorizes the Dropbox connection.</summary>
    Task<DropboxConnectionBinding> ReauthorizeDropboxAsync(Func<Uri, Task> openAuthorizationPage, CancellationToken cancellationToken = default, string? connectionId = null, bool includeFileRequests = false);

    /// <summary>Connects a Dropbox account.</summary>
    Task<DropboxConnectionBinding> ConnectDropboxAsync(Func<Uri, Task> openAuthorizationPage, CancellationToken cancellationToken = default, bool addAccount = false);

    /// <summary>Creates a Dropbox folder browser.</summary>
    Task<IDropboxFolderBrowserService> CreateFolderBrowserAsync(CancellationToken cancellationToken = default, string? connectionId = null);

    /// <summary>Loads the Google Drive connection binding, or null.</summary>
    GoogleDriveConnectionBinding? LoadGoogleDriveConnection(string? connectionId = null);

    /// <summary>Loads all Google Drive connection bindings.</summary>
    IReadOnlyList<GoogleDriveConnectionBinding> LoadGoogleDriveConnections();

    /// <summary>Requests cancellation of a pending authorization attempt.</summary>
    bool RequestAuthorizationAttemptCancellation(string attemptId);

    /// <summary>Whether an account (or the primary account) is paused.</summary>
    bool IsAccountPaused(string? connectionId);

    /// <summary>Whether a pair's account is paused.</summary>
    bool IsPairAccountPaused(SyncPair pair);

    /// <summary>Disconnects Dropbox.</summary>
    Task DisconnectDropboxAsync(CancellationToken cancellationToken = default, string? connectionId = null);

    /// <summary>Disconnects a Google Drive connection.</summary>
    Task DisconnectGoogleDriveAsync(string connectionId, CancellationToken cancellationToken = default);

    /// <summary>Ensures the recommended recovery folder exists for a connection.</summary>
    Task<DropboxFolderEntry> EnsureRecommendedRecoveryFolderAsync(CancellationToken cancellationToken = default, string? connectionId = null);
}
