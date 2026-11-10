using System.Globalization;

namespace Massdex.SyncShark.Engine;

/// <summary>One pair's persisted binding to its exact configured local and Dropbox roots, with both root identities.</summary>
public sealed record RootBinding(
    string LocalRoot,
    string DropboxRoot,
    string ConnectionId,
    DirectoryIdentity Local,
    DropboxRootIdentity Dropbox)
{
    /// <summary>Whether saved configuration replaces the namespace to which deletion eligibility belongs.</summary>
    public static bool HistoryIdentityChanged(PairConfig before, PairConfig after) =>
        before.LocalRoot != after.LocalRoot || before.DropboxRoot != after.DropboxRoot ||
        before.ConnectionId != after.ConnectionId || before.Roots != after.Roots;

    /// <summary>The Local journal domain for this pair: the volume serial as the adapters report it. A method, so the
    /// persisted binding keeps exactly its constructor members.</summary>
    internal string LocalVolumeKey() => Local.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>Checks the supported identity representation without observing or approving a root.</summary>
    public bool IsValid() => Local is not null && Dropbox is not null &&
        (Local.FileIdHigh is not null || Local.VolumeSerialNumber <= uint.MaxValue) &&
        !string.IsNullOrEmpty(Dropbox.AccountId) && !string.IsNullOrEmpty(Dropbox.RootNamespaceId) &&
        !string.IsNullOrEmpty(Dropbox.HomeNamespaceId) && !string.IsNullOrEmpty(Dropbox.FolderId);
}
