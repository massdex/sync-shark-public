using System.Net.Http.Headers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record DropboxFolderEntry(string Name, string Path, string FolderId);

public sealed record DropboxFolderPage(IReadOnlyList<DropboxFolderEntry> Folders, string? ContinueCursor);

public interface IDropboxFolderBrowserService
{
    Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null,
        CancellationToken cancellationToken = default);

    Task<DropboxFolderPage> ContinueFoldersAsync(string cursor,
        CancellationToken cancellationToken = default);

    Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
        CancellationToken cancellationToken = default);

    Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
        CancellationToken cancellationToken = default);

    Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
        CancellationToken cancellationToken = default);
}

public sealed class DropboxBrowserClient(
    HttpClient httpClient,
    IDropboxAccessTokenSource tokenSource,
    DropboxConnectionBinding expectedConnection) : IDropboxFolderBrowserService
{
    private const int MaximumFoldersPerPage = 2_000;
    private const int InteractiveFoldersPerPage = 200;
    private const int MaximumJsonResponseBytes = 8 * 1024 * 1024;
    private const int MaximumContinuationPages = 100_000;
    private const int MaximumCursorBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private bool _accountVerified;

    public async Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null, CancellationToken cancellationToken = default)
    {
        await EnsureAccountAsync(cancellationToken);
        var path = BrowserPath(folderId);
        using var document = await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder", new
        {
            path,
            recursive = false,
            include_deleted = false,
            include_mounted_folders = true,
            include_non_downloadable_files = true,
            limit = InteractiveFoldersPerPage
        }, cancellationToken);
        return ParseFolderPage(document.RootElement);
    }

    public async Task<DropboxFolderPage> ContinueFoldersAsync(string cursor, CancellationToken cancellationToken = default)
    {
        cursor = RequireOpaqueCursor(cursor);
        await EnsureAccountAsync(cancellationToken);
        using var document = await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder/continue", new { cursor }, cancellationToken);
        return ParseFolderPage(document.RootElement);
    }

    public async Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        await EnsureAccountAsync(cancellationToken);
        var path = BrowserPath(folderId);
        using var document = await PostJsonAsync("https://api.dropboxapi.com/2/files/search_v2", new
        {
            query = query.Trim(),
            options = new
            {
                path,
                max_results = InteractiveFoldersPerPage,
                file_status = "active",
                filename_only = false
            }
        }, cancellationToken);
        return ParseFolderSearchPage(document.RootElement);
    }

    public async Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
        CancellationToken cancellationToken = default)
    {
        cursor = RequireOpaqueCursor(cursor);
        await EnsureAccountAsync(cancellationToken);
        using var document = await PostJsonAsync("https://api.dropboxapi.com/2/files/search/continue_v2",
            new { cursor }, cancellationToken);
        return ParseFolderSearchPage(document.RootElement);
    }

    public async IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var page = await ListFoldersAsync(folderId, cancellationToken);
        var cursorFingerprints = new HashSet<string>(StringComparer.Ordinal);
        var continuationPages = 0;
        while (true)
        {
            foreach (var folder in page.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return folder;
            }

            var cursor = page.ContinueCursor;
            if (cursor is null) yield break;
            if (++continuationPages > MaximumContinuationPages)
                throw new InvalidDataException(
                    $"Dropbox exceeded the bounded folder-browser continuation limit of {MaximumContinuationPages:N0} pages.");
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cursor)));
            if (!cursorFingerprints.Add(fingerprint))
                throw new InvalidDataException("Dropbox cycled a folder-browser cursor without completing the listing.");
            page = await ContinueFoldersAsync(cursor, cancellationToken);
        }
    }

    public async Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name, CancellationToken cancellationToken = default)
    {
        await EnsureAccountAsync(cancellationToken);
        var cleanName = NormalizeFolderName(name);
        var parent = BrowserPath(parentFolderId);
        var path = parent + "/" + cleanName;
        using var document = await PostJsonAsync("https://api.dropboxapi.com/2/files/create_folder_v2", new
        {
            path,
            autorename = false
        }, cancellationToken);
        var metadata = document.RootElement.GetProperty("metadata");
        var created = ParseFolder(metadata, requireTag: false);
        using var readbackDocument = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
        {
            path = created.FolderId,
            include_deleted = false,
            include_has_explicit_shared_members = false,
            include_media_info = false
        }, cancellationToken);
        var readback = ParseFolder(readbackDocument.RootElement);
        if (!string.Equals(readback.FolderId, created.FolderId, StringComparison.Ordinal) ||
            !string.Equals(readback.Path, created.Path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(readback.Name, created.Name, StringComparison.Ordinal))
            throw new InvalidDataException("Dropbox folder creation readback differs from the create acknowledgement.");
        return readback;
    }

    private async Task EnsureAccountAsync(CancellationToken cancellationToken)
    {
        if (_accountVerified) return;
        await _accountGate.WaitAsync(cancellationToken);
        try
        {
            if (_accountVerified) return;
            DropboxProviderConfiguration.ValidateConnection(expectedConnection);
            using var document = await PostJsonAsync("https://api.dropboxapi.com/2/users/get_current_account", null, cancellationToken);
            var root = document.RootElement;
            var rootInfo = root.GetProperty("root_info");
            if (!string.Equals(root.GetProperty("account_id").GetString(), expectedConnection.AccountId, StringComparison.Ordinal) ||
                !string.Equals(rootInfo.GetProperty("root_namespace_id").GetString(), expectedConnection.RootNamespaceId, StringComparison.Ordinal) ||
                !string.Equals(rootInfo.GetProperty("home_namespace_id").GetString(), expectedConnection.HomeNamespaceId, StringComparison.Ordinal))
                throw new InvalidOperationException("Dropbox account or namespace differs from the saved connection.");
            _accountVerified = true;
        }
        finally { _accountGate.Release(); }
    }

    private static DropboxFolderPage ParseFolderPage(JsonElement root)
    {
        var folders = new List<DropboxFolderEntry>();
        var entriesSeen = 0;
        foreach (var item in root.GetProperty("entries").EnumerateArray())
        {
            if (++entriesSeen > MaximumFoldersPerPage)
                throw new InvalidDataException($"Dropbox returned an oversized folder page with more than {MaximumFoldersPerPage:N0} entries.");
            if (!string.Equals(item.GetProperty(".tag").GetString(), "folder", StringComparison.Ordinal)) continue;
            folders.Add(ParseFolder(item));
        }
        folders.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        var hasMore = root.GetProperty("has_more").GetBoolean();
        var cursor = hasMore
            ? RequireCursor(root, "Dropbox folder cursor is absent.")
            : null;
        return new DropboxFolderPage(folders, cursor);
    }

    private static DropboxFolderPage ParseFolderSearchPage(JsonElement root)
    {
        var folders = new Dictionary<string, DropboxFolderEntry>(StringComparer.Ordinal);
        var matchesSeen = 0;
        foreach (var match in root.GetProperty("matches").EnumerateArray())
        {
            if (++matchesSeen > MaximumFoldersPerPage)
                throw new InvalidDataException($"Dropbox returned an oversized folder-search page with more than {MaximumFoldersPerPage:N0} entries.");
            if (!match.TryGetProperty("metadata", out var wrapper)) continue;
            var metadata = wrapper.TryGetProperty("metadata", out var nested) ? nested : wrapper;
            if (!metadata.TryGetProperty(".tag", out var tag) ||
                !string.Equals(tag.GetString(), "folder", StringComparison.Ordinal)) continue;
            var folder = ParseFolder(metadata);
            folders[folder.FolderId] = folder;
        }
        var hasMore = root.GetProperty("has_more").GetBoolean();
        var cursor = hasMore
            ? RequireCursor(root, "Dropbox folder-search cursor is absent.")
            : null;
        return new DropboxFolderPage(folders.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray(), cursor);
    }

    private static string RequireCursor(JsonElement root, string message)
    {
        if (!root.TryGetProperty("cursor", out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException(message);
        return RequireOpaqueCursor(property.GetString()!);
    }

    private static string RequireOpaqueCursor(string cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        if (Encoding.UTF8.GetByteCount(cursor) > MaximumCursorBytes)
            throw new InvalidDataException(
                $"Dropbox returned a folder-browser cursor larger than {MaximumCursorBytes:N0} bytes.");
        return cursor;
    }

    private static DropboxFolderEntry ParseFolder(JsonElement item, bool requireTag = true)
    {
        var hasTag = item.TryGetProperty(".tag", out var tag);
        if ((!hasTag && requireTag) ||
            (hasTag && !string.Equals(tag.GetString(), "folder", StringComparison.Ordinal)))
            throw new InvalidDataException("Dropbox returned a non-folder item where a folder was required.");
        var name = item.GetProperty("name").GetString() ?? throw new InvalidDataException("Dropbox folder name is absent.");
        var id = RequireFolderId(item.GetProperty("id").GetString());
        var display = item.TryGetProperty("path_display", out var pathDisplay) ? pathDisplay.GetString() : null;
        var lower = item.TryGetProperty("path_lower", out var pathLower) ? pathLower.GetString() : null;
        var path = display ?? lower ?? throw new InvalidDataException("Dropbox folder path is absent.");
        return new DropboxFolderEntry(name, PilotPath.NormalizeDropboxRoot(path), id);
    }

    private async Task<JsonDocument> PostJsonAsync(string uri, object? payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            var token = await tokenSource.GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Dropbox access token is unavailable.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (uri.Contains("/2/files/", StringComparison.Ordinal))
            {
                var pathRoot = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [".tag"] = "root",
                    ["root"] = expectedConnection.RootNamespaceId
                });
                if (!request.Headers.TryAddWithoutValidation("Dropbox-API-Path-Root", pathRoot))
                    throw new InvalidOperationException("Dropbox root-namespace binding could not be applied.");
            }
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 5)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                            TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt, 5)));
                await Task.Delay(delay < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : delay,
                    cancellationToken);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new DropboxApiException(response.StatusCode,
                    $"Dropbox API returned HTTP {(int)response.StatusCode} while browsing folders.");
            if (response.Content.Headers.ContentLength is > MaximumJsonResponseBytes)
                throw new InvalidDataException("Dropbox returned an oversized folder-browser response.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bounded = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (bounded.Length > MaximumJsonResponseBytes - read)
                    throw new InvalidDataException("Dropbox returned an oversized folder-browser response.");
                await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return JsonDocument.Parse(bounded.GetBuffer().AsMemory(0, checked((int)bounded.Length)),
                new JsonDocumentOptions { MaxDepth = 64 });
        }
    }

    private static string NormalizeFolderName(string name)
    {
        var value = name.Trim();
        if (value.Length is < 1 or > 255 || value is "." or ".." || value.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Enter one valid Dropbox folder name without slashes.", nameof(name));
        return value;
    }

    private static string RequireFolderId(string? folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId) || !folderId.StartsWith("id:", StringComparison.Ordinal) || folderId.Length <= 3)
            throw new InvalidDataException("Dropbox folder ID is absent or invalid.");
        return folderId;
    }

    private static string BrowserPath(string? folderId) =>
        string.IsNullOrEmpty(folderId) || PilotPath.IsDropboxNamespaceRootIdentity(folderId)
            ? string.Empty
            : RequireFolderId(folderId);
}
