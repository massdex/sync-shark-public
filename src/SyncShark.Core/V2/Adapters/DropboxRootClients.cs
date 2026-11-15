namespace Massdex.SyncShark.V2;

/// <summary>Opens a Dropbox client for one v2 root; the root's AccountId is the retained connection id (decision 29).</summary>
/// <remarks>Every missing piece (no state, no connection, no credential) is refused before any request is sent.</remarks>
public sealed class DropboxRootClients
{
    private readonly DropboxAccountClients? _accounts;
    private readonly DropboxAccountClients? _watchAccounts;

    /// <param name="accounts">The account-bound client opener, or null when no product state is available.</param>
    /// <param name="watchAccounts">
    /// The same opener over the long-poll HTTP client, whose timeout outlasts a long-poll (defaults to
    /// <paramref name="accounts"/>).
    /// </param>
    public DropboxRootClients(DropboxAccountClients? accounts, DropboxAccountClients? watchAccounts = null)
    {
        _accounts = accounts;
        _watchAccounts = watchAccounts ?? accounts;
    }

    /// <summary>Opens a client rooted and bound to <paramref name="root"/>'s recorded folder id.</summary>
    public Task<DropboxPilotClient> OpenAsync(RootDef root, CancellationToken ct) =>
        OpenBoundAsync(_accounts, root, ct);

    /// <summary>Opens a client for <paramref name="root"/>'s change watch, over the long-poll HTTP client.</summary>
    public Task<DropboxPilotClient> OpenWatchAsync(RootDef root, CancellationToken ct) =>
        OpenBoundAsync(_watchAccounts, root, ct);

    /// <summary>Opens a client rooted at <paramref name="path"/>, bound to <paramref name="rootId"/> when known.</summary>
    public Task<DropboxPilotClient> OpenAsync(string? connectionId, string path, string? rootId,
        CancellationToken ct) => OpenCoreAsync(_accounts, connectionId, path, rootId, ct);

    private Task<DropboxPilotClient> OpenBoundAsync(DropboxAccountClients? accounts, RootDef root,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(root.RootObjectId))
            throw new SyncRunRefusedException("The Dropbox folder '" + root.RootPath + "' has no recorded identity.");
        return OpenCoreAsync(accounts, root.AccountId, root.RootPath, root.RootObjectId, ct);
    }

    private static async Task<DropboxPilotClient> OpenCoreAsync(DropboxAccountClients? accounts, string? connectionId,
        string path, string? rootId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new SyncRunRefusedException("The Dropbox folder '" + path + "' names no Dropbox connection.");
        if (accounts is null)
            throw new SyncRunRefusedException("The Dropbox connections cannot be read here, so '" + path +
                                              "' cannot be opened.");
        DropboxAccountClient? opened;
        try
        {
            opened = await accounts.OpenRootedAsync(connectionId, path, rootId, ct).ConfigureAwait(false);
        }
        catch (DropboxCredentialUnavailableException)
        {
            throw new SyncRunRefusedException("The Dropbox connection '" + connectionId +
                                              "' has no saved sign-in. Sign in to Dropbox again.");
        }
        return opened?.Client ?? throw new SyncRunRefusedException(
            "The Dropbox connection '" + connectionId + "' is not connected. Connect that exact account first.");
    }
}
