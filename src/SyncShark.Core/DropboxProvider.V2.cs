using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark;

// Core v2 additions to the Dropbox adapter. Its members reuse the existing private request helpers, root
// verification and metadata parsing; no existing line changes. All Dropbox HTTP stays inside this adapter.
public sealed partial class DropboxPilotClient
{
    /// <summary>
    /// The stable folder id of this client's root, read once when a pair is created. The account root is its
    /// namespace identity; any other root must be a folder still at exactly this path.
    /// </summary>
    internal async Task<string> ReadRootFolderIdAsync(CancellationToken ct)
    {
        DropboxProviderConfiguration.ValidateConnection(expectedConnection);
        await VerifyAccountAsync(ct);
        if (string.Equals(_root, "/", StringComparison.Ordinal))
            return PilotPath.DropboxNamespaceRootIdentity(expectedConnection.RootNamespaceId);
        var identity = await GetFolderIdentityAsync(_root, ct);
        if (!string.Equals(identity.Path, _root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Dropbox folder is not at exactly the requested path.");
        return identity.FolderId;
    }

    /// <summary>
    /// True when a failure is a Dropbox account-access refusal: HTTP 401, an invalid_grant token refresh (the typed
    /// OAuth error the token boundary carries), or a missing protected credential. The v2 adapter maps it to
    /// <c>EndpointAccessException</c>, and no fallback catch may swallow it.
    /// </summary>
    internal static bool IsAccessRefusal(Exception exception) => exception switch
    {
        DropboxCredentialUnavailableException => true,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => true,
        DropboxApiException { OAuthError: DropboxOAuthError.InvalidGrant } => true,
        _ => false
    };

    /// <summary>
    /// One parsed change-feed page that carries each changed item's full metadata (size, hash, revision), so a
    /// proportional delta needs no per-item follow-up read. A folder that is this exact root is skipped.
    /// </summary>
    internal async Task<DropboxChangePage> ReadChangePageAsync(string cursor, CancellationToken ct,
        V2.ListingCheckpoint? checkpoint = null, TimeSpan? readBudget = null)
    {
        cursor = RequireOpaqueCursor(cursor);
        const string route = "https://api.dropboxapi.com/2/files/list_folder/continue";
        using var document = checkpoint is null ? await PostJsonAsync(route, new { cursor }, ct, readBudget) :
            await checkpoint.PageAsync(route, new { cursor }, () => PostJsonAsync(route, new { cursor }, ct, readBudget));
        var root = document.RootElement;
        var next = RequireCursor(root, "Dropbox returned no continuation cursor.");
        var hasMore = root.GetProperty("has_more").GetBoolean();
        if (hasMore && string.Equals(next, cursor, StringComparison.Ordinal))
            throw new InvalidDataException("Dropbox repeated a change-feed cursor while more changes were declared.");
        var changes = new List<DropboxChange>();
        foreach (var item in root.GetProperty("entries").EnumerateArray())
        {
            var tag = item.GetProperty(".tag").GetString();
            var display = item.TryGetProperty("path_display", out var pathDisplay)
                ? pathDisplay.GetString()
                : item.TryGetProperty("path_lower", out var pathLower) ? pathLower.GetString() : null;
            if (display is null) throw new InvalidDataException("Dropbox path is absent.");
            if (string.Equals(tag, "deleted", StringComparison.Ordinal))
            {
                changes.Add(new DropboxChange(ToInventoryRelative(display), null));
                continue;
            }
            if (tag is not ("file" or "folder"))
                throw new InvalidDataException("Dropbox returned an unsupported metadata type.");
            if (tag == "folder" && _rootId is not null &&
                string.Equals(item.TryGetProperty("id", out var id) ? id.GetString() : null, _rootId,
                    StringComparison.Ordinal) &&
                string.Equals(display, _root, StringComparison.OrdinalIgnoreCase))
                continue;
            var entry = RemoteEntryFromMetadata(item, inventoryOnly: true);
            changes.Add(new DropboxChange(entry.RelativePath, entry));
        }
        await VerifyRootIdentityAsync(ct);
        return new DropboxChangePage(next, hasMore, changes);
    }

    /// <summary>Concurrent metadata reads one membership check may hold open (a provider courtesy bound).</summary>
    private const int MembershipReadParallelism = 8;

    /// <summary>
    /// Where each object is now, by its exact id: its path relative to this root, or null when Dropbox no longer
    /// holds it or holds it outside this root (B-126: membership, not namespace-wide existence). The root is verified
    /// once; each distinct id is read once, a few at a time. Transport, permission and malformed replies stay errors;
    /// only Dropbox's classified absent-path reply means absent.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string?>> CurrentRelativePathsAsync(IEnumerable<string> objectIds,
        CancellationToken ct, Action<string, Exception>? unreadable = null)
    {
        var ids = objectIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Any(id => !IsExactDropboxObjectIdentity(id)))
            throw new InvalidDataException("An exact Dropbox object ID is required for a membership check.");
        var places = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        if (ids.Length == 0) return places;
        await VerifyRootIdentityAsync(ct);
        var consecutiveTransportFailures = 0;
        Exception? transportFailure = null;
        await Parallel.ForEachAsync(ids, new ParallelOptions
        {
            MaxDegreeOfParallelism = MembershipReadParallelism, CancellationToken = ct,
        }, async (id, token) =>
        {
            if (unreadable is not null && Volatile.Read(ref consecutiveTransportFailures) >= 3)
            {
                unreadable(id, Volatile.Read(ref transportFailure)!);
                return;
            }
            try
            {
                places[id] = await CurrentRelativePathAsync(id, token);
                Interlocked.Exchange(ref consecutiveTransportFailures, 0);
            }
            catch (Exception error) when (unreadable is not null && !token.IsCancellationRequested &&
                error is not V2.PairPausedException && !IsAccessRefusal(error) &&
                (error is IOException or HttpRequestException or TimeoutException || ProviderTransientFailure.IsTransient(error, token)))
            {
                // Missing dictionary key means unknown, not absent (null). The inventory protects that path.
                if (ProviderTransientFailure.IsTransient(error, token) || error is TimeoutException)
                {
                    Volatile.Write(ref transportFailure, error);
                    Interlocked.Increment(ref consecutiveTransportFailures);
                }
                else Interlocked.Exchange(ref consecutiveTransportFailures, 0);
                unreadable(id, error);
            }
        });
        return places;
    }

    private async Task<string?> CurrentRelativePathAsync(string objectId, CancellationToken ct)
    {
        using var document = await PostJsonAllowingMissingPathAsync("https://api.dropboxapi.com/2/files/get_metadata",
            new { path = objectId, include_deleted = false, include_media_info = false }, ct);
        if (document is null) return null;
        var metadata = document.RootElement;
        var tag = metadata.TryGetProperty(".tag", out var tagElement) ? tagElement.GetString() : null;
        if (!string.Equals(metadata.TryGetProperty("id", out var id) ? id.GetString() : null, objectId,
                StringComparison.Ordinal) || tag is not ("file" or "folder"))
            throw new IOException("Dropbox returned metadata that does not prove the requested object identity.");
        // Only a well-formed absolute provider path can prove where the object is; anything else is unknown
        // membership and fails the read, never "outside the root" (which would delete the stored row).
        var path = (metadata.TryGetProperty("path_display", out var display) ? display.GetString() : null)
            ?? (metadata.TryGetProperty("path_lower", out var lower) ? lower.GetString() : null);
        if (path is null || path.Length < 2 || path[0] != '/' ||
            !path[1..].Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..") &&
                !segment.Any(c => c == '\\' || char.IsControl(c))))
            throw new InvalidDataException("Dropbox returned no well-formed path for the object; its membership is unknown.");
        var prefix = _root == "/" ? "/" : _root + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? PilotPath.NormalizeDropboxRelative(path[prefix.Length..])
            : null;
    }

    /// <summary>
    /// Deletes one exact-identity Dropbox folder only when empty or containing exclusively ignored children.
    /// Dropbox has no native delete-if-empty; the listing immediately precedes the delete request.
    /// </summary>
    internal async Task DeleteEmptyFolderAsync(PilotRemoteEntry expected, CancellationToken ct,
        IReadOnlyList<SyncIgnoreRule>? ignoreRules = null)
    {
        if (expected.Kind != PilotEntryKind.Directory || !IsExactDropboxObjectIdentity(expected.Id))
            throw new InvalidDataException("An exact Dropbox folder identity is required for an empty-folder delete.");
        var id = expected.Id!;
        await VerifyRootIdentityAsync(ct);
        try
        {
            await ReadExactItemMetadataAsync(expected.RelativePath, id, ct);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            throw new DropboxApiException(HttpStatusCode.Conflict, "Dropbox folder changed after inventory.");
        }
        using var listing = await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder", new
        {
            path = id,
            recursive = false,
            include_deleted = false,
            include_mounted_folders = true,
            include_non_downloadable_files = true,
            limit = ignoreRules is { Count: > 0 } ? 2000 : 1
        }, ct);
        void CheckChildren(JsonElement page)
        {
            foreach (var child in page.GetProperty("entries").EnumerateArray())
            {
                var entry = RemoteEntryFromMetadata(child, inventoryOnly: true);
                // A matched folder rule covers its entire subtree, so only immediate children need listing.
                if (ignoreRules is null || !SyncIgnorePolicy.IsIgnored(entry.RelativePath,
                        entry.Kind == PilotEntryKind.Directory, ignoreRules))
                    throw new DropboxApiException(HttpStatusCode.Conflict,
                        "Dropbox folder contains a nonignored item; it was not deleted.") { WriteErrorTag = "conflict" };
            }
        }
        CheckChildren(listing.RootElement);
        var more = listing.RootElement.GetProperty("has_more").GetBoolean();
        var cursor = more ? listing.RootElement.GetProperty("cursor").GetString() : null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (more)
        {
            if (string.IsNullOrWhiteSpace(cursor) || !seen.Add(cursor) || seen.Count > 100_000)
                throw new InvalidDataException("Dropbox returned an invalid folder-list cursor.");
            using var page = await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder/continue",
                new { cursor }, ct);
            CheckChildren(page.RootElement);
            more = page.RootElement.GetProperty("has_more").GetBoolean();
            cursor = more ? page.RootElement.GetProperty("cursor").GetString() : null;
        }
        using var deleted = await PostConditionalEffectJsonAsync("https://api.dropboxapi.com/2/files/delete_v2",
            new { path = id }, ct);
        try
        {
            var metadata = deleted.RootElement.GetProperty("metadata");
            if (!string.Equals(metadata.GetProperty(".tag").GetString(), "folder", StringComparison.Ordinal) ||
                !string.Equals(metadata.GetProperty("id").GetString(), id, StringComparison.Ordinal))
                throw new DropboxEffectUncertainException(
                    "Dropbox accepted the folder delete but returned mismatched metadata.");
        }
        catch (Exception exception) when (IsEffectBodyShapeException(exception))
        {
            throw new DropboxEffectUncertainException(
                "Dropbox accepted the folder delete but returned an unexpected success body.", exception);
        }
    }
}
