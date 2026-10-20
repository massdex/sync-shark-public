namespace Massdex.SyncShark;

// The one account-level Dropbox seam the Dropbox CLI reaches. It reuses the SAME retained connections,
// protected credentials and transfer limiter the sync engine uses; it creates no pair, plan, token or
// scope of its own.
public sealed partial class SyncAuthorityRuntimeHost
{
    private DropboxAccountClients? _dropboxAccounts;

    /// <summary>The account-bound Dropbox route clients shared by the Dropbox CLI verbs.</summary>
    public DropboxAccountClients DropboxAccounts =>
        _dropboxAccounts ??= new DropboxAccountClients(httpClient, connectionStore, credentialStore,
            AccountTokenSource, _transferLimiter);
}
