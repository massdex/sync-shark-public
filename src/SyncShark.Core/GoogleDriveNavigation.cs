using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>A read-only resolution, not a grant to mutate permissions. Revalidate through the sharing client before changes.</summary>
public sealed record GoogleDriveResolvedWebLocation(string ConnectionId, string NamespaceId, string RootFolderId,
    string RelativePath, string FileId, GoogleDriveEntryKind Kind, string? Name, string Version,
    string? SharedDriveId, string? ResourceKey, string WebViewLink);

public sealed partial class GoogleDrivePilotClient
{
    // Account id means the retained ConnectionId, never an email, active-account default or URL hint.
    // Only the selected path's components are read. Both passes use the existing paged name lookup;
    // no inventory, shortcut traversal, folder creation or permission mutation is involved.
    public async Task<string> GetWebLocationAsync(string expectedAccountId, string relativePath = ".",
        CancellationToken cancellationToken = default) =>
        (await ResolveWebLocationAsync(expectedAccountId, relativePath, cancellationToken)).WebViewLink;

    public async Task<GoogleDriveResolvedWebLocation> ResolveWebLocationAsync(string expectedAccountId,
        string relativePath = ".", CancellationToken cancellationToken = default)
    {
        var (target, corpus, segments) = await ResolveNavigationLocationAsync(expectedAccountId,
            relativePath, cancellationToken, requireWebLocation: true);
        var url = NavigationWebUrl(target);
        return new(_connection.ConnectionId, corpus ?? _connection.PermissionId, _rootFolderId,
            segments.Length == 0 ? "." : string.Join('/', segments), target.Entry.FileId, target.Entry.Kind,
            target.Entry.Name, target.Entry.Version!, corpus, NavigationResourceKey(target, url), url);
    }

    // A provider object identity is independent of whether Google supplies a browser URL. Recovery
    // reuses the same account/corpus/ancestry readback, without requiring an unrelated web capability.
    private async Task<(NavigationFile Target, string? Corpus, string[] Segments)> ResolveNavigationLocationAsync(
        string expectedAccountId, string relativePath, CancellationToken cancellationToken,
        bool requireWebLocation = false)
    {
        if (string.IsNullOrWhiteSpace(expectedAccountId) ||
            !string.Equals(expectedAccountId, _connection.ConnectionId, StringComparison.Ordinal))
            throw new InvalidOperationException("The requested Google location belongs to a different account.");
        var segments = NavigationSegments(relativePath);
        GoogleDriveProviderConfiguration.ValidateConnection(_connection);
        await VerifyAccountAsync(cancellationToken);

        var root = await ReadNavigationFileAsync(_rootFolderId, cancellationToken);
        if (root.Entry.Kind != GoogleDriveEntryKind.Folder)
            throw new GoogleDriveBlockedException("The selected Google root is no longer a folder.");
        var corpus = root.Entry.DriveId;
        if ((corpus is not null && (!_allowSharedDrives || !_connection.SharedDrivesEnabled)) ||
            (_sharedDriveId is not null && !string.Equals(_sharedDriveId, corpus, StringComparison.Ordinal)))
            throw new GoogleDriveBlockedException("The selected Google root no longer matches its enabled drive corpus.");
        _sharedDriveId = corpus;
        VerifyCorpusMembership(root.Entry);

        var chain = await ResolveNavigationChainAsync(segments, corpus, cancellationToken);
        var target = chain.Count == 0 ? root : await ReadNavigationFileAsync(chain[^1].FileId, cancellationToken);
        if (chain.Count != 0 && !SameNavigationEntry(chain[^1], target.Entry))
            throw NavigationChanged();
        VerifyCorpusMembership(target.Entry);
        // Reject an unsafe browser destination before doing further reads. Metadata-only consumers
        // deliberately have no browser-URL dependency, including its optional resource key.
        if (requireWebLocation) _ = NavigationResourceKey(target, NavigationWebUrl(target));

        // A second bounded read catches moves, duplicate-name replacements and changed targets during
        // resolution. This is readback, not a claim of a provider transaction or perpetual URL validity.
        var rootAfter = await ReadNavigationFileAsync(_rootFolderId, cancellationToken);
        if (!SameNavigationEntry(root.Entry, rootAfter.Entry)) throw NavigationChanged();
        var chainAfter = await ResolveNavigationChainAsync(segments, corpus, cancellationToken);
        if (chain.Count != chainAfter.Count || chain.Where((entry, index) =>
                !SameNavigationEntry(entry, chainAfter[index])).Any()) throw NavigationChanged();
        var after = chain.Count == 0 ? rootAfter : await ReadNavigationFileAsync(target.Entry.FileId, cancellationToken);
        if (!SameNavigationEntry(target.Entry, after.Entry) || (requireWebLocation &&
            (target.WebViewLink != after.WebViewLink || target.ResourceKey != after.ResourceKey)))
            throw NavigationChanged();
        VerifyCorpusMembership(after.Entry);
        return (target, corpus, segments);
    }

    private static string[] NavigationSegments(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath == ".") return [];
        // Refuse absolute paths and traversal before normalization could conceal their origin.
        var path = relativePath.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Choose a relative path inside the selected Google folder.");
        var normalized = PilotPath.NormalizeRelative(path);
        var segments = normalized.Split('/');
        if (segments.Length > 128)
            throw new InvalidDataException("The Google navigation path exceeds the bounded lookup depth.");
        return segments;
    }

    private async Task<List<GoogleDriveInventoryEntry>> ResolveNavigationChainAsync(string[] segments,
        string? corpus, CancellationToken cancellationToken)
    {
        var chain = new List<GoogleDriveInventoryEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { _rootFolderId };
        var parent = _rootFolderId;
        for (var index = 0; index < segments.Length; index++)
        {
            var matches = await ListChildrenByNameAsync(parent, segments[index], cancellationToken);
            if (matches.Count != 1)
                throw new GoogleDriveBlockedException(matches.Count == 0
                    ? "The Google item is unavailable at the selected path. Refresh the pair before opening it."
                    : "More than one Google item has this name. Select an unambiguous location before opening it.");
            var entry = matches[0];
            RequireNavigationEntry(entry);
            if (entry.Name != segments[index] || !entry.Parents.Contains(parent, StringComparer.Ordinal) ||
                !string.Equals(entry.DriveId, corpus, StringComparison.Ordinal) || !seen.Add(entry.FileId))
                throw NavigationChanged();
            VerifyCorpusMembership(entry);
            if (index < segments.Length - 1 && entry.Kind != GoogleDriveEntryKind.Folder)
                throw new GoogleDriveBlockedException("A Google navigation ancestor is not a folder. Shortcuts are not followed.");
            chain.Add(entry);
            parent = entry.FileId;
        }
        return chain;
    }

    private async Task<NavigationFile> ReadNavigationFileAsync(string fileId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("files/" + Uri.EscapeDataString(fileId) +
            "?fields=" + FileFields + ",webViewLink,resourceKey&supportsAllDrives=" +
            (_allowSharedDrives && _connection.SharedDrivesEnabled ? "true" : "false"), cancellationToken);
        var root = document.RootElement;
        var entry = ReadInventoryEntry(root, string.Empty);
        RequireNavigationEntry(entry);
        if (!string.Equals(entry.FileId, fileId, StringComparison.Ordinal)) throw NavigationChanged();
        return new(entry, NavigationString(root, "webViewLink"), NavigationString(root, "resourceKey"));
    }

    private static string? NavigationString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Google returned malformed navigation metadata.");
        return value.GetString();
    }

    private static void RequireNavigationEntry(GoogleDriveInventoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.FileId) || string.IsNullOrWhiteSpace(entry.Version) ||
            entry.Trashed || entry.Kind == GoogleDriveEntryKind.Unknown)
            throw new GoogleDriveBlockedException("The Google item is unavailable or lacks a verifiable navigation identity.");
    }

    private static bool SameNavigationEntry(GoogleDriveInventoryEntry left, GoogleDriveInventoryEntry right) =>
        left.FileId == right.FileId && left.Version == right.Version && left.Kind == right.Kind &&
        left.MimeType == right.MimeType && left.Name == right.Name && left.DriveId == right.DriveId &&
        left.Trashed == right.Trashed && left.Size == right.Size && left.Sha256 == right.Sha256 &&
        left.Md5 == right.Md5 && left.HeadRevisionId == right.HeadRevisionId &&
        left.ShortcutTargetId == right.ShortcutTargetId && left.ShortcutTargetMimeType == right.ShortcutTargetMimeType &&
        left.Parents.ToHashSet(StringComparer.Ordinal).SetEquals(right.Parents);

    private static GoogleDriveBlockedException NavigationChanged() =>
        new("The Google item, its parents or its drive changed while the location was checked. Refresh and try again.");

    private static string? NavigationResourceKey(NavigationFile file, string validatedUrl)
    {
        if (file.ResourceKey is not null) return file.ResourceKey;
        foreach (var part in new Uri(validatedUrl).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(pair[0]), "resourcekey", StringComparison.OrdinalIgnoreCase))
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
        }
        return null;
    }

    private static string NavigationWebUrl(NavigationFile file)
    {
        var value = file.WebViewLink;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 || uri.UserInfo.Length != 0 ||
            uri.Host is not ("drive.google.com" or "docs.google.com"))
            throw new GoogleDriveBlockedException("Google did not return a supported secure web location for this item.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Select(pair => new KeyValuePair<string, string>(
                Uri.UnescapeDataString(pair[0]), pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "")).ToArray();
        var ids = query.Where(pair => pair.Key == "id").Select(pair => pair.Value).ToArray();
        string? pathId = null;
        if (uri.Host == "drive.google.com")
        {
            if (segments.Length >= 3 && segments[0] == "file" && segments[1] == "d") pathId = segments[2];
            else if (segments.Length >= 3 && segments[0] == "drive" && segments[1] == "folders") pathId = segments[2];
            else if (segments.Length >= 5 && segments[0] == "drive" && segments[1] == "u" &&
                     int.TryParse(segments[2], out _) && segments[3] == "folders") pathId = segments[4];
            else if (segments.Length == 1 && segments[0] == "open" && ids.Length == 1) pathId = ids[0];
        }
        else if (segments.Length >= 3 && (segments[0] is "document" or "spreadsheets" or "presentation" or "drawings" or "forms"))
        {
            if (segments[1] == "d") pathId = segments[2];
            else if (segments.Length >= 5 && segments[1] == "u" && int.TryParse(segments[2], out _) && segments[3] == "d")
                pathId = segments[4];
        }
        if (!string.Equals(pathId, file.Entry.FileId, StringComparison.Ordinal) || ids.Length > 1 ||
            ids.Any(id => id != file.Entry.FileId))
            throw new GoogleDriveBlockedException("The returned Google web location does not name the verified item. Shortcut targets are not substituted.");
        var keys = query.Where(pair => string.Equals(pair.Key, "resourcekey", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value).ToArray();
        if (keys.Length > 1 || file.ResourceKey is not null && keys.Length == 1 && keys[0] != file.ResourceKey)
            throw new GoogleDriveBlockedException("The Google web location has an inconsistent resource key.");
        if (string.IsNullOrEmpty(file.ResourceKey) || keys.Length == 1) return value;
        var fragmentAt = value.IndexOf('#');
        var head = fragmentAt < 0 ? value : value[..fragmentAt];
        return head + (head.Contains('?') ? "&" : "?") + "resourcekey=" + Uri.EscapeDataString(file.ResourceKey) +
               (fragmentAt < 0 ? "" : value[fragmentAt..]);
    }

    private sealed record NavigationFile(GoogleDriveInventoryEntry Entry, string? WebViewLink, string? ResourceKey);
}
