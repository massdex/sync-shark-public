namespace Massdex.SyncShark;

// Providers whose URL identifies an item can resolve its exact permission without an
// account-wide inventory. Other providers retain the existing bounded listing path.
public interface IAccountSharingExactPermissionLookup
{
    Task<AccountSharedLink> GetLinkPermissionAsync(string namespaceId, string exactUrl,
        string? permissionId, CancellationToken cancellationToken = default);
}

public static class AccountSharingLookup
{
    public static void ValidateUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host is "drive.google.com" or "docs.google.com")
            GoogleDriveAccountSharingClient.ValidateUrl(url);
        else DropboxAccountSharingClient.ValidateUrl(url);
    }

    public static async Task<AccountSharedLink> ExactAsync(IAccountSharingClient client, string namespaceId, string url,
        string? permissionId, CancellationToken cancellationToken = default)
    {
        ValidateUrl(url);
        if (client is IAccountSharingExactPermissionLookup exact)
        {
            var link = await exact.GetLinkPermissionAsync(namespaceId, url, permissionId, cancellationToken);
            if (link.ConnectionId != client.ConnectionId || link.NamespaceId != namespaceId || link.Url != url ||
                permissionId is not null && link.PermissionId != permissionId)
                throw new InvalidDataException("The provider returned a different link identity. No access was changed.");
            return link;
        }
        if (permissionId is null) return await client.GetLinkAsync(namespaceId, url, cancellationToken);
        AccountSharingCursor? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 20; page++)
        {
            var result = await client.ListLinksAsync(namespaceId, cursor, cancellationToken);
            var matches = result.Items.Where(item => item.ConnectionId == client.ConnectionId && item.NamespaceId == namespaceId &&
                item.Url == url && item.PermissionId == permissionId).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) throw new InvalidDataException("The exact link permission is ambiguous. No access was changed.");
            cursor = result.Next;
            if (cursor is null) break;
            if (!cursors.Add(cursor.Value)) throw new InvalidDataException("The provider repeated its continuation. Refresh before selecting a permission.");
        }
        throw new InvalidOperationException("The exact permission was not found within the bounded account listing. Refresh or narrow the selection. No access was changed.");
    }
}
