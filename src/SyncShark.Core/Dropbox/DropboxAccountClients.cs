namespace Massdex.SyncShark;

/// <summary>Opens account-bound Dropbox clients from the retained connections (Dropbox CLI design §4, core v2 endpoints).</summary>
public sealed class DropboxAccountClients(
    HttpClient httpClient,
    DropboxConnectionStore connectionStore,
    IDropboxRefreshTokenStore credentialStore,
    Func<DropboxConnectionBinding, IDropboxAccessTokenSource> tokenSource,
    SyncTransferRateLimiter transferLimiter)
{
    /// <summary>
    /// Opens the named retained Dropbox connection as a client rooted at its namespace, or returns null when
    /// the connection is absent or is not a Dropbox connection. A missing protected credential throws.
    /// </summary>
    public Task<DropboxAccountClient?> OpenAsync(string connectionId, string? namespaceId,
        CancellationToken cancellationToken = default) =>
        OpenRootedAsync(connectionId, "/",
            connection => PilotPath.DropboxNamespaceRootIdentity(namespaceId ?? connection.RootNamespaceId),
            cancellationToken);

    /// <summary>
    /// Opens the named retained Dropbox connection as a client rooted at <paramref name="rootPath"/>, bound to the
    /// folder id <paramref name="rootId"/> when one is known; null when the connection is absent. A missing
    /// protected credential throws. No request is sent.
    /// </summary>
    public Task<DropboxAccountClient?> OpenRootedAsync(string connectionId, string rootPath, string? rootId,
        CancellationToken cancellationToken = default) =>
        OpenRootedAsync(connectionId, rootPath, _ => rootId, cancellationToken);

    private async Task<DropboxAccountClient?> OpenRootedAsync(string connectionId, string rootPath,
        Func<DropboxConnectionBinding, string?> rootId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        var connection = connectionStore.Load(connectionId);
        if (connection is null) return null;
        if (string.IsNullOrWhiteSpace(await credentialStore.ForConnection(connection).ReadAsync(cancellationToken)))
            throw new DropboxCredentialUnavailableException(connectionId);
        var client = new DropboxPilotClient(httpClient, tokenSource(connection), rootPath, connection,
            rootId(connection), transferLimiter);
        return new DropboxAccountClient(connection, client);
    }
}
