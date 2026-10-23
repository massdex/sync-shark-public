namespace Massdex.SyncShark;

// The Dropbox CLI seam on the shared operations base: the authority host's account clients and the
// product's own result folder. The direct (lock-owning) mode and the running-application mode share it,
// and the v2 runtime implements the same interface the same way.
public abstract partial class SyncCliOperationsBase : IDropboxCliAccess
{
    /// <summary>The retained Dropbox accounts and their route clients.</summary>
    public DropboxAccountClients DropboxAccounts => Host.DropboxAccounts;

    /// <summary>The absolute folder oversized Dropbox results are spilled to.</summary>
    public string ResultFolder =>
        Path.Combine(SyncSharkRuntimePaths.ProductRoot, DropboxCliSettings.Default.ResultFolderName);

    /// <summary>The Dropbox CLI tunables in effect.</summary>
    public DropboxCliSettings DropboxCliSettings => DropboxCliSettings.Default;
}
