using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record DropboxSharedLink(string Url, string ItemId, string Visibility, string Access,
    bool CanRevoke, DateTimeOffset? Expires);
public sealed record DropboxSharingItem(string RelativePath, string ItemId, string Name,
    IReadOnlyList<DropboxSharedLink> Links, string ConnectionId = "", string RootId = "");
public enum DropboxLinkAudience { AnyoneWithLink, TeamOnly }

public sealed partial class DropboxPilotClient
{
    public async Task<PilotRemoteRootBinding> GetDirectoryIdentityAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        await VerifyRootIdentityAsync(cancellationToken);
        var relative = PilotPath.NormalizeRelative(relativePath);
        var folder = await GetFolderIdentityAsync(relative.Length == 0 ? RequiredRootId() : RequiredRootId() + "/" + relative, cancellationToken);
        if (!string.Equals(ToRelative(folder.Path), relative, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The folder identity no longer matches this pair location.");
        return folder;
    }

    public async Task<DropboxSharingItem> GetSharingItemAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        await VerifyRootIdentityAsync(cancellationToken);
        var relative = PilotPath.NormalizeRelative(relativePath);
        using var metadata = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata",
            new { path = relative.Length == 0 ? RequiredRootId() : RequiredRootId() + "/" + relative, include_deleted = false }, cancellationToken);
        var item = metadata.RootElement;
        if (item.GetProperty(".tag").GetString() is not ("file" or "folder"))
            throw new InvalidOperationException("This Dropbox item does not support shared links.");
        var id = item.GetProperty("id").GetString() ?? throw new InvalidDataException("Dropbox item identity is absent.");
        var displayed = item.TryGetProperty("path_display", out var display) ? display.GetString() :
            item.GetProperty("path_lower").GetString();
        if (!string.Equals(ToRelative(displayed ?? ""), relative, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Dropbox item moved. Reopen sharing for its current location.");
        return new DropboxSharingItem(relative, id, item.GetProperty("name").GetString() ?? relative,
            await ReadDirectSharedLinksAsync(id, cancellationToken), ExpectedConnection.ConnectionId, RequiredRootId());
    }

    private async Task<IReadOnlyList<DropboxSharedLink>> ReadDirectSharedLinksAsync(string itemId,
        CancellationToken cancellationToken)
    {
        using var response = await PostJsonAsync("https://api.dropboxapi.com/2/sharing/list_shared_links",
            new { path = itemId, direct_only = true }, cancellationToken);
        if (response.RootElement.TryGetProperty("has_more", out var more) && more.GetBoolean())
            throw new IOException("Dropbox returned an incomplete direct-link list; no link changes were made.");
        var links = response.RootElement.GetProperty("links");
        if (links.GetArrayLength() > 1000) throw new IOException("Dropbox returned too many direct links.");
        return links.EnumerateArray().Select(item => ReadSharedLink(item, itemId)).ToArray();
    }

    public async Task<DropboxSharedLink> CreateSharedLinkAsync(string relativePath, string expectedItemId,
        DropboxLinkAudience audience, CancellationToken cancellationToken = default)
    {
        var current = await GetSharingItemAsync(relativePath, cancellationToken);
        RequireSharingIdentity(current, expectedItemId);
        if (current.Links.Count != 0)
            throw new InvalidOperationException("A direct shared link already exists. Use its settings or copy it.");
        using var response = await PostEffectJsonAsync("https://api.dropboxapi.com/2/sharing/create_shared_link_with_settings",
            new { path = current.ItemId, settings = new { requested_visibility = AudienceTag(audience), access = "viewer" } }, cancellationToken);
        return ReadSharedLink(response.RootElement, current.ItemId);
    }

    public async Task<DropboxSharedLink> ChangeSharedLinkAudienceAsync(string relativePath, string expectedItemId,
        string exactUrl, DropboxLinkAudience audience, CancellationToken cancellationToken = default)
    {
        var current = await GetSharingItemAsync(relativePath, cancellationToken);
        RequireSharingIdentity(current, expectedItemId);
        RequireDirectLink(current, exactUrl);
        // Access and expiry are deliberately omitted: this changes only the confirmed audience.
        using var response = await PostEffectJsonAsync("https://api.dropboxapi.com/2/sharing/modify_shared_link_settings",
            new { url = exactUrl, settings = new { requested_visibility = AudienceTag(audience) } }, cancellationToken);
        return ReadSharedLink(response.RootElement, current.ItemId);
    }

    public async Task RevokeSharedLinkAsync(string relativePath, string expectedItemId, string exactUrl,
        CancellationToken cancellationToken = default)
    {
        var current = await GetSharingItemAsync(relativePath, cancellationToken);
        RequireSharingIdentity(current, expectedItemId);
        var link = RequireDirectLink(current, exactUrl);
        if (!link.CanRevoke) throw new InvalidOperationException("Dropbox does not allow this account to revoke this link.");
        using var response = await PostEffectJsonAsync("https://api.dropboxapi.com/2/sharing/revoke_shared_link",
            new { url = exactUrl }, cancellationToken);
        if ((await ReadDirectSharedLinksAsync(current.ItemId, cancellationToken)).Any(item => item.Url == exactUrl))
            throw new IOException("Dropbox has not confirmed that the link was revoked. Refresh before retrying.");
    }

    private static void RequireSharingIdentity(DropboxSharingItem item, string expectedId)
    {
        if (!string.Equals(item.ItemId, expectedId, StringComparison.Ordinal))
            throw new IOException("The item was replaced after sharing was opened. No access was changed.");
    }
    private static DropboxSharedLink RequireDirectLink(DropboxSharingItem item, string url) =>
        item.Links.SingleOrDefault(link => string.Equals(link.Url, url, StringComparison.Ordinal)) ??
        throw new InvalidOperationException("This is no longer a direct link to the selected item. Refresh sharing.");
    private static string AudienceTag(DropboxLinkAudience audience) => audience switch
    {
        DropboxLinkAudience.AnyoneWithLink => "public", DropboxLinkAudience.TeamOnly => "team_only",
        _ => throw new InvalidDataException("Unknown shared-link audience.")
    };
    internal static DropboxSharedLink ReadSharedLink(JsonElement item, string expectedId)
    {
        var id = item.TryGetProperty("id", out var value) ? value.GetString() : null;
        var url = item.GetProperty("url").GetString();
        if (id != expectedId || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host is not ("www.dropbox.com" or "dropbox.com") || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
            throw new InvalidDataException("Dropbox returned a link outside the selected item or supported origin.");
        var permissions = item.GetProperty("link_permissions");
        string Tag(string property) => permissions.TryGetProperty(property, out var field) &&
            field.TryGetProperty(".tag", out var tag) ? tag.GetString() ?? "unknown" : "unknown";
        var visibility = Tag("resolved_visibility");
        if (visibility == "unknown") visibility = Tag("effective_audience");
        return new DropboxSharedLink(url!, expectedId, visibility, Tag("link_access_level"),
            permissions.TryGetProperty("can_revoke", out var revoke) && revoke.GetBoolean(),
            item.TryGetProperty("expires", out var expiry) && expiry.TryGetDateTimeOffset(out var date) ? date : null);
    }
}

public sealed partial class SyncAuthorityRuntimeHost
{
    private async Task<DropboxPilotClient> SharingClientAsync(string pairId, CancellationToken cancellationToken,
        DropboxSharingItem? expected = null)
    {
        stateAuthority.VerifyAuthority();
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.Single(item => item.Id == pairId);
        if (SyncAuthorityRuntimeHost.IsGooglePair(pair))
            throw SyncAuthorityRuntimeHost.DriveCapabilityRefusal("Dropbox shared links",
                "Drive sharing uses permissions and webViewLink instead; the Drive sharing surface is not " +
                "wired in this build, so no link change was made.");
        var connection = await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
        if (expected is not null && (expected.ConnectionId != connection.ConnectionId || expected.RootId != pair.DropboxFolderId))
            throw new InvalidOperationException("The sharing account or folder binding changed. Reopen this item before changing access.");
        return new DropboxPilotClient(httpClient, AccountTokenSource(connection), pair.DropboxRoot,
            connection, pair.DropboxFolderId, _transferLimiter);
    }
    public async Task<DropboxSharingItem> GetSharingItemAsync(string pairId, string relativePath,
        CancellationToken cancellationToken = default) =>
        await (await SharingClientAsync(pairId, cancellationToken)).GetSharingItemAsync(relativePath, cancellationToken);
    public async Task<DropboxSharedLink> CreateSharedLinkAsync(string pairId, DropboxSharingItem item,
        DropboxLinkAudience audience, CancellationToken cancellationToken = default) =>
        await (await SharingClientAsync(pairId, cancellationToken, item)).CreateSharedLinkAsync(item.RelativePath,
            item.ItemId, audience, cancellationToken);
    public async Task<DropboxSharedLink> ChangeSharedLinkAudienceAsync(string pairId, DropboxSharingItem item,
        string url, DropboxLinkAudience audience, CancellationToken cancellationToken = default) =>
        await (await SharingClientAsync(pairId, cancellationToken, item)).ChangeSharedLinkAudienceAsync(item.RelativePath,
            item.ItemId, url, audience, cancellationToken);
    public async Task RevokeSharedLinkAsync(string pairId, DropboxSharingItem item, string url,
        CancellationToken cancellationToken = default) =>
        await (await SharingClientAsync(pairId, cancellationToken, item)).RevokeSharedLinkAsync(item.RelativePath,
            item.ItemId, url, cancellationToken);
}
