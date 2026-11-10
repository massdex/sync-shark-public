namespace Massdex.SyncShark.Engine;

/// <summary>Dropbox root identity: account, root and home namespaces, and the configured folder's ID.</summary>
public sealed record DropboxRootIdentity(string AccountId, string RootNamespaceId,
    string HomeNamespaceId, string FolderId);
