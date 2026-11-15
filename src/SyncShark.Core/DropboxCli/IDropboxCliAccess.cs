namespace Massdex.SyncShark;

/// <summary>The seam the Dropbox CLI needs from any runtime that offers it.</summary>
public interface IDropboxCliAccess
{
    /// <summary>The retained Dropbox accounts and their route clients.</summary>
    DropboxAccountClients DropboxAccounts { get; }

    /// <summary>The absolute folder oversized results are spilled to.</summary>
    string ResultFolder { get; }

    /// <summary>The Dropbox CLI tunables in effect.</summary>
    DropboxCliSettings DropboxCliSettings { get; }
}
