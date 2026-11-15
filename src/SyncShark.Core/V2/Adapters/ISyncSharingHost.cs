namespace Massdex.SyncShark;

/// <summary>The shared-link and web-location surface the window calls.</summary>
public interface ISyncSharingHost
{
    /// <summary>Creates an account-sharing client for a connection.</summary>
    Task<IAccountSharingClient> CreateAccountSharingClientAsync(string connectionId, CancellationToken cancellationToken = default);

    /// <summary>Gets the sharing item for one pair path.</summary>
    Task<DropboxSharingItem> GetSharingItemAsync(string pairId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Creates a shared link for a sharing item.</summary>
    Task<DropboxSharedLink> CreateSharedLinkAsync(string pairId, DropboxSharingItem item, DropboxLinkAudience audience, CancellationToken cancellationToken = default);

    /// <summary>Changes a shared link's audience.</summary>
    Task<DropboxSharedLink> ChangeSharedLinkAudienceAsync(string pairId, DropboxSharingItem item, string url, DropboxLinkAudience audience, CancellationToken cancellationToken = default);

    /// <summary>Revokes one shared link.</summary>
    Task RevokeSharedLinkAsync(string pairId, DropboxSharingItem item, string url, CancellationToken cancellationToken = default);

    /// <summary>Revokes several shared links.</summary>
    Task<IReadOnlyList<SharedLinkRevokeResult>> RevokeSharedLinksAsync(IReadOnlyList<SharedLinkRevokeTarget> targets, CancellationToken cancellationToken = default);

    /// <summary>Resolves the Google Drive web location for one pair path.</summary>
    Task<GoogleDriveResolvedWebLocation> ResolveGoogleDriveWebLocationAsync(string pairId, string expectedAccountId, string relativePath = ".", CancellationToken cancellationToken = default);

    /// <summary>Resolves the Google Drive web location for one endpoint context.</summary>
    Task<GoogleDriveResolvedWebLocation> ResolveGoogleDriveEndpointWebLocationAsync(string pairId, SyncPairSide side, SyncEndpointContext expectedContext, string relativePath = ".", CancellationToken cancellationToken = default);

    /// <summary>The Google Drive web location URL for one pair path.</summary>
    Task<string> GetGoogleDriveWebLocationAsync(string pairId, string expectedAccountId, string relativePath = ".", CancellationToken cancellationToken = default);
}
