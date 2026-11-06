using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// Provider-routed folder browser for Google Drive. It implements the same browser contract the Dropbox
// browser already implements, so the existing pair-draft and folder-picker surfaces work unchanged once
// a pair is bound to a Google account. Drive-specific refusal messages replace the Dropbox wording.
//
// Shared drives are an explicit capability and are enabled only when the constructor capability AND the
// account binding both say so. While they are disabled this browser keeps its My Drive-only behaviour:
// the same "root" sentinel, the same My Drive child listing and the same refusal for any item that
// carries a drive id. Every mode verifies the signed-in account first and revalidates an externally
// selected folder id against the provider before it can drive a child listing.
//
// While they are enabled the browser exposes verified root choices and binds every browse, search,
// continuation and create to one exact corpus: the user corpus for My Drive, or corpora=drive with one
// exact driveId for a shared drive. A shared-drive folder can never silently become a My Drive scan, a
// shared-drive reply can never be accepted in the My Drive corpus, and no request ever uses
// corpora=allDrives. The displayed path is presentation only; the stable ids and the corpus are authority.
public sealed class GoogleDriveFolderBrowserClient(
    HttpClient httpClient,
    IGoogleDriveAccessTokenSource tokenSource,
    GoogleDriveConnectionBinding expectedConnection,
    bool allowSharedDrives = false) : IDropboxFolderBrowserService
{
    private const int InteractiveFoldersPerPage = 200;
    private const int MaximumContinuations = 1000;
    private const int MaximumAncestryDepth = 128;
    private const int SharedDrivePageSize = 100;
    private const int MaximumExistencePages = 100;
    private const string MyDriveLabel = "My Drive";
    private const string SharedDrivePathPrefix = "/Shared drives/";
    private const string RootAlias = "root";

    // Documented limitation: an enabled browser with no folder selected searches the user corpus only.
    // Shared-drive content is searched only after one exact shared-drive folder is selected, and the
    // browser never widens a search to every drive at once.
    public const string SharedDriveSearchLimitation =
        "Search without a selected folder covers My Drive only. Select a shared-drive folder to search " +
        "that shared drive; Sync Shark never searches every drive at once.";

    private readonly GoogleDriveConnectionBinding _connection = expectedConnection.Normalize();
    private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _pathDriveIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Continuation> _continuations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sharedRoots = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _rootGate = new(1, 1);
    private bool _rootVerified;
    private string _myDriveRootId = string.Empty;

    // Shared-drive browsing needs BOTH the constructor capability and the account binding flag.
    private bool SharedDrivesEnabled => allowSharedDrives && _connection.SharedDrivesEnabled;

    private enum ContinuationKind { Roots, Folder, Search }

    private sealed record Continuation(ContinuationKind Kind, string? Query, string PageToken, string? DriveId);

    public async Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        await VerifyRootAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(folderId) && SharedDrivesEnabled)
            return await ListRootsPageAsync(null, cancellationToken);
        var (parent, driveId) = await ResolveCorpusAsync(folderId, cancellationToken);
        return await QueryPageAsync(ChildQuery(parent, null), null, driveId, ContinuationKind.Folder,
            cancellationToken);
    }

    public Task<DropboxFolderPage> ContinueFoldersAsync(string cursor,
        CancellationToken cancellationToken = default)
    {
        var continuation = RequireContinuation(cursor);
        if (continuation.Kind == ContinuationKind.Search)
            throw new InvalidDataException(
                "This cursor belongs to a folder search, not a folder listing. Refresh the folders.");
        return ContinueAsync(continuation, cancellationToken);
    }

    public async Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        await VerifyRootAsync(cancellationToken);
        var (parent, driveId) = await ResolveCorpusAsync(folderId, cancellationToken);
        var escaped = EscapeQuery(query.Trim());
        var q = "mimeType = '" + GoogleDrivePilotClient.FolderMimeType + "' and name contains '" +
                escaped + "' and trashed = false";
        if (!string.IsNullOrWhiteSpace(folderId)) q += " and '" + EscapeQuery(parent) + "' in parents";
        return await QueryPageAsync(q, null, driveId, ContinuationKind.Search, cancellationToken);
    }

    public Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        var continuation = RequireContinuation(cursor);
        if (continuation.Kind != ContinuationKind.Search)
            throw new InvalidDataException(
                "This cursor belongs to a folder listing, not a folder search. Refresh the search.");
        return ContinueAsync(continuation, cancellationToken);
    }

    public async IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var page = await ListFoldersAsync(folderId, cancellationToken);
        while (true)
        {
            foreach (var folder in page.Folders) yield return folder;
            if (string.IsNullOrEmpty(page.ContinueCursor)) yield break;
            page = await ContinueAsync(RequireContinuation(page.ContinueCursor!), cancellationToken);
        }
    }

    public async Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
        CancellationToken cancellationToken = default)
    {
        var clean = NormalizeFolderName(name);
        await VerifyRootAsync(cancellationToken);
        var (parent, driveId) = await ResolveCorpusAsync(parentFolderId, cancellationToken);
        var existing = await ListChildrenAsync(parent, driveId, clean, cancellationToken);
        if (existing.Count > 1)
            throw new GoogleDriveBlockedException(
                "Google Drive already contains more than one folder named '" + clean + "' here. Choose the " +
                "intended folder instead of creating another one.");
        if (existing.Count == 1) return existing[0];
        var metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = clean,
            ["mimeType"] = GoogleDrivePilotClient.FolderMimeType,
            ["parents"] = new[] { parent }
        });
        using var document = await PostJsonAsync(
            "files?fields=id,name,mimeType,parents,driveId,trashed&supportsAllDrives=" +
            (SharedDrivesEnabled ? "true" : "false"), metadata, cancellationToken);
        return await ReadFolderAsync(document.RootElement, driveId, cancellationToken);
    }

    // The first page of an enabled browser exposes the account's verified My Drive root exactly once and
    // then the account's shared-drive roots, each with the stable id Google reports for that drive.
    private async Task<DropboxFolderPage> ListRootsPageAsync(string? pageToken,
        CancellationToken cancellationToken)
    {
        await VerifyRootAsync(cancellationToken);
        var url = "drives?pageSize=" + SharedDrivePageSize + "&fields=nextPageToken,drives(id,name)";
        if (pageToken is not null) url += "&pageToken=" + Uri.EscapeDataString(pageToken);
        using var document = await GetJsonAsync(url, cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("drives", out var drives) && drives.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Google returned an invalid shared drive listing.");
        var folders = new List<DropboxFolderEntry>();
        if (pageToken is null) folders.Add(new DropboxFolderEntry(MyDriveLabel, "/", _myDriveRootId));
        if (root.TryGetProperty("drives", out drives) && drives.ValueKind == JsonValueKind.Array)
            foreach (var drive in drives.EnumerateArray()) folders.Add(ReadSharedRoot(drive));
        string? cursor = null;
        if (ReadToken(root) is { Length: > 0 } token)
            cursor = RememberContinuation(ContinuationKind.Roots, null, token, null);
        return new DropboxFolderPage(folders, cursor);
    }

    private DropboxFolderEntry ReadSharedRoot(JsonElement drive)
    {
        var id = drive.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var name = drive.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("Google returned a shared drive with no stable id or name.");
        _sharedRoots[id!] = name!;
        var path = SharedDrivePathPrefix + name;
        RememberPath(id!, path, id!);
        return new DropboxFolderEntry(name!, path, id!);
    }

    private async Task<DropboxFolderPage> ContinueAsync(Continuation continuation,
        CancellationToken cancellationToken) => continuation.Kind switch
    {
        ContinuationKind.Roots => await ListRootsPageAsync(continuation.PageToken, cancellationToken),
        ContinuationKind.Search => await QueryPageAsync(continuation.Query!, continuation.PageToken,
            continuation.DriveId, ContinuationKind.Search, cancellationToken),
        _ => await QueryPageAsync(continuation.Query!, continuation.PageToken, continuation.DriveId,
            ContinuationKind.Folder, cancellationToken)
    };

    private Continuation RequireContinuation(string cursor) =>
        _continuations.TryGetValue(cursor, out var continuation)
            ? continuation
            : throw new InvalidDataException(
                "This folder page belongs to another browser session or account. Refresh the folders.");

    private async Task<DropboxFolderPage> QueryPageAsync(string query, string? pageToken, string? driveId,
        ContinuationKind kind, CancellationToken cancellationToken)
    {
        await VerifyRootAsync(cancellationToken);
        using var document = await GetJsonAsync(ListUrl(query, pageToken, driveId), cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("incompleteSearch", out var incomplete) &&
            incomplete.ValueKind == JsonValueKind.True)
            throw new GoogleDriveBlockedException(
                "Google returned an incomplete folder search. No absent folder can be inferred.");
        var folders = new List<DropboxFolderEntry>();
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Google returned an invalid folder listing.");
        foreach (var item in files.EnumerateArray())
            folders.Add(await ReadFolderAsync(item, driveId, cancellationToken));
        string? cursor = null;
        if (ReadToken(root) is { Length: > 0 } token) cursor = RememberContinuation(kind, query, token, driveId);
        return new DropboxFolderPage(folders, cursor);
    }

    private async Task<IReadOnlyList<DropboxFolderEntry>> ListChildrenAsync(string parentId, string? driveId,
        string? exactName, CancellationToken cancellationToken)
    {
        var page = await QueryPageAsync(ChildQuery(parentId, exactName), null, driveId,
            ContinuationKind.Folder, cancellationToken);
        var folders = page.Folders.ToList();
        for (var pages = 0; page.ContinueCursor is not null; pages++)
        {
            if (pages >= MaximumExistencePages)
                throw new GoogleDriveBlockedException(
                    "Folder existence could not be fully checked; nothing was created.");
            page = await ContinueAsync(RequireContinuation(page.ContinueCursor!), cancellationToken);
            folders.AddRange(page.Folders);
        }
        return folders;
    }

    // An externally selected folder id is never trusted for the corpus. It is revalidated against the
    // provider in every mode, so a shared-drive selection can never be issued as a My Drive scan and the
    // reverse. Only the exact "root" alias keeps its original default behaviour.
    private async Task<(string Parent, string? DriveId)> ResolveCorpusAsync(string? folderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderId)) return (RootAlias, null);
        var id = folderId!.Trim();
        if (string.Equals(id, RootAlias, StringComparison.Ordinal)) return (RootAlias, null);
        if (!SharedDrivesEnabled) return await ResolveMyDriveOnlyCorpusAsync(id, cancellationToken);
        if (string.Equals(id, _myDriveRootId, StringComparison.Ordinal)) return (id, null);
        if (_sharedRoots.ContainsKey(id)) return await VerifySharedRootAsync(id, cancellationToken);
        var item = await GetFolderMetadataAsync(id, cancellationToken);
        VerifySelectedFolderMetadata(item, id);
        var driveId = ReadDriveId(item);
        if (driveId is null) return (id, null);
        if (!_sharedRoots.ContainsKey(driveId))
            throw new GoogleDriveBlockedException(
                "The selected folder belongs to shared drive " + driveId + ", which is not one of this " +
                "account's discovered shared drives. The folder was not browsed.");
        return (id, driveId);
    }

    // Disabled mode keeps its My Drive-only behaviour, but an externally selected folder id is still
    // revalidated before it can drive a child listing: the reply must be the exact requested id and an
    // untrashed folder, and any item that reports a drive id is refused.
    private async Task<(string Parent, string? DriveId)> ResolveMyDriveOnlyCorpusAsync(string id,
        CancellationToken cancellationToken)
    {
        var item = await GetFolderMetadataAsync(id, cancellationToken);
        VerifySelectedFolderMetadata(item, id);
        if (ReadDriveId(item) is not null)
            throw new GoogleDriveBlockedException("Shared drives are not enabled for this connection.");
        return (id, null);
    }

    // The selected item must be the exact folder the caller asked for, never a substituted or cached one.
    private static void VerifySelectedFolderMetadata(JsonElement item, string requestedId)
    {
        if (!string.Equals(ReadId(item), requestedId, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "Google returned a different folder than the selected one. The folder was not browsed.");
        VerifyFolderMetadata(item, "The selected Google Drive folder");
    }

    // A discovered shared-drive root must report exactly the discovered drive id, a folder kind, no
    // parent and no trash before it can bind a corpus.
    private async Task<(string Parent, string? DriveId)> VerifySharedRootAsync(string driveId,
        CancellationToken cancellationToken)
    {
        var item = await GetFolderMetadataAsync(driveId, cancellationToken);
        if (!string.Equals(ReadId(item), driveId, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The selected shared drive root does not report the discovered drive id. The folder was not browsed.");
        if (!string.Equals(ReadMimeType(item), GoogleDrivePilotClient.FolderMimeType, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The selected shared drive root is not a folder. The folder was not browsed.");
        if (ReadTrashed(item))
            throw new GoogleDriveBlockedException(
                "The selected shared drive root is in the trash. The folder was not browsed.");
        if (!string.Equals(ReadDriveId(item), driveId, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The selected shared drive root is not bound to its discovered drive id. The folder was not browsed.");
        if (ReadParents(item).Length != 0)
            throw new GoogleDriveBlockedException(
                "The selected shared drive root reports a parent, so it is not a verified drive root. " +
                "The folder was not browsed.");
        return (driveId, driveId);
    }

    private static void VerifyFolderMetadata(JsonElement item, string subject)
    {
        if (string.Equals(ReadMimeType(item), GoogleDrivePilotClient.ShortcutMimeType, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(subject + " is a shortcut. Shortcuts are never followed.");
        if (!string.Equals(ReadMimeType(item), GoogleDrivePilotClient.FolderMimeType, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(subject + " is not a folder.");
        if (ReadTrashed(item)) throw new GoogleDriveBlockedException(subject + " is in the trash.");
    }

    private string ListUrl(string query, string? pageToken, string? driveId)
    {
        var url = "files?q=" + Uri.EscapeDataString(query) + "&pageSize=" + InteractiveFoldersPerPage +
                  "&fields=nextPageToken,incompleteSearch,files(id,name,mimeType,parents,driveId,trashed)";
        if (!SharedDrivesEnabled)
        {
            url += "&supportsAllDrives=false&includeItemsFromAllDrives=false";
        }
        else if (driveId is null)
        {
            url += "&supportsAllDrives=true&includeItemsFromAllDrives=false&corpora=user";
        }
        else
        {
            url += "&supportsAllDrives=true&includeItemsFromAllDrives=true&corpora=drive&driveId=" +
                   Uri.EscapeDataString(driveId);
        }
        if (pageToken is not null) url += "&pageToken=" + Uri.EscapeDataString(pageToken);
        return url;
    }

    private string RememberContinuation(ContinuationKind kind, string? query, string pageToken,
        string? driveId)
    {
        if (_continuations.Count >= MaximumContinuations)
            throw new GoogleDriveBlockedException("Folder browsing reached its page limit. Narrow the search.");
        var cursor = Guid.NewGuid().ToString("N");
        _continuations[cursor] = new Continuation(kind, query, pageToken, driveId);
        return cursor;
    }

    private void RememberPath(string id, string path, string? driveId)
    {
        _paths[id] = path;
        _pathDriveIds[id] = driveId ?? string.Empty;
    }

    private static string EscapeQuery(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal);

    private static string ChildQuery(string parent, string? name) => "'" + EscapeQuery(parent) +
        "' in parents and mimeType = '" + GoogleDrivePilotClient.FolderMimeType + "' and trashed = false" +
        (name is null ? "" : " and name = '" + EscapeQuery(name) + "'");

    private async Task VerifyRootAsync(CancellationToken cancellationToken)
    {
        if (_rootVerified) return;
        await _rootGate.WaitAsync(cancellationToken);
        try
        {
            if (_rootVerified) return;
            // The account is verified before any folder is read in every mode, so an authorization bound
            // to a different account can never become a folder read even while shared drives are disabled.
            await VerifyAccountAsync(cancellationToken);
            using var document = await GetJsonAsync("files/root?fields=id,mimeType", cancellationToken);
            var root = document.RootElement;
            var id = ReadId(root);
            if (string.IsNullOrWhiteSpace(id) || !string.Equals(ReadMimeType(root),
                    GoogleDrivePilotClient.FolderMimeType, StringComparison.Ordinal))
                throw new InvalidDataException("Google returned no verifiable My Drive root.");
            _myDriveRootId = id;
            RememberPath(id, "/", null);
            RememberPath(RootAlias, "/", null);
            _rootVerified = true;
        }
        finally { _rootGate.Release(); }
    }

    private async Task VerifyAccountAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("about?fields=user(permissionId)", cancellationToken);
        var root = document.RootElement;
        var permissionId = root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
            ? user.TryGetProperty("permissionId", out var permission) ? permission.GetString() : null
            : null;
        if (string.IsNullOrWhiteSpace(permissionId))
            throw new InvalidDataException("The signed-in Google account identity is absent.");
        if (!string.Equals(permissionId, _connection.PermissionId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "This authorization belongs to a different Google account than the selected connection. " +
                "No folder was read.");
    }

    private async Task<JsonElement> GetFolderMetadataAsync(string id, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("files/" + Uri.EscapeDataString(id) +
            "?fields=id,name,mimeType,parents,driveId,trashed&supportsAllDrives=" +
            (SharedDrivesEnabled ? "true" : "false"), cancellationToken);
        return document.RootElement.Clone();
    }

    // A descendant path is resolved from the provider, never from a cached display string. Every step must
    // have exactly one parent, a folder kind, the same drive id as its child and a bounded depth, and it
    // must terminate at the verified My Drive root or at one discovered shared-drive root.
    private async Task<string> FolderPathAsync(string id, string? expectedDriveId, HashSet<string> ancestors,
        CancellationToken cancellationToken)
    {
        if (_paths.TryGetValue(id, out var known))
        {
            if (!string.Equals(_pathDriveIds.GetValueOrDefault(id, string.Empty), expectedDriveId ?? string.Empty,
                    StringComparison.Ordinal))
                throw new GoogleDriveBlockedException(
                    "Google folder ancestry crosses between My Drive and a shared drive.");
            return known;
        }
        if (ancestors.Count >= MaximumAncestryDepth || !ancestors.Add(id))
            throw new GoogleDriveBlockedException("Google folder ancestry is cyclic or too deep.");
        var item = await GetFolderMetadataAsync(id, cancellationToken);
        if (string.Equals(ReadMimeType(item), GoogleDrivePilotClient.ShortcutMimeType, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "Google folder ancestry includes a shortcut. Shortcuts are never followed.");
        if (!string.Equals(ReadMimeType(item), GoogleDrivePilotClient.FolderMimeType, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException("Google folder ancestry includes an item that is not a folder.");
        if (ReadTrashed(item))
            throw new GoogleDriveBlockedException("Google folder ancestry includes a trashed item.");
        var driveId = ReadDriveId(item);
        if (driveId is not null && !SharedDrivesEnabled)
            throw new GoogleDriveBlockedException("Shared drives are not enabled for this connection.");
        if (driveId is not null && !_sharedRoots.ContainsKey(driveId))
            throw new GoogleDriveBlockedException(
                "This folder belongs to shared drive " + driveId + ", which is not one of this account's " +
                "discovered shared drives.");
        if (!string.Equals(driveId, expectedDriveId, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "Google folder ancestry crosses between My Drive and a shared drive.");
        var parents = ReadParents(item);
        if (parents.Length == 0)
            throw new GoogleDriveBlockedException("This folder has no verified parent in its drive hierarchy.");
        if (parents.Length != 1) throw new GoogleDriveBlockedException("Google folder ancestry is ambiguous.");
        var parent = await FolderPathAsync(parents[0], driveId, ancestors, cancellationToken);
        var path = parent.TrimEnd('/') + "/" + ReadName(item);
        RememberPath(id, path, driveId);
        return path;
    }

    private async Task<DropboxFolderEntry> ReadFolderAsync(JsonElement item, string? expectedDriveId,
        CancellationToken cancellationToken)
    {
        var id = ReadId(item);
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("Google Drive returned a folder with no stable id.");
        VerifyFolderMetadata(item, "This Google item");
        var driveId = ReadDriveId(item);
        if (driveId is not null && !SharedDrivesEnabled)
            throw new GoogleDriveBlockedException("Shared drives are not enabled for this connection.");
        if (driveId is not null && !_sharedRoots.ContainsKey(driveId))
            throw new GoogleDriveBlockedException(
                "This folder belongs to shared drive " + driveId + ", which is not one of this account's " +
                "discovered shared drives.");
        if (!string.Equals(driveId, expectedDriveId, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "Google returned an item outside the selected folder's exact drive corpus.");
        var name = ReadName(item);
        if (name.Length == 0) throw new InvalidDataException("Google folder has no name.");
        var parents = ReadParents(item);
        if (parents.Length != 1)
            throw new GoogleDriveBlockedException("This folder has no unambiguous accessible parent.");
        var parent = await FolderPathAsync(parents[0], driveId,
            new HashSet<string>(StringComparer.Ordinal) { id }, cancellationToken);
        var path = parent.TrimEnd('/') + "/" + name;
        RememberPath(id, path, driveId);
        return new DropboxFolderEntry(name, path, id);
    }

    private static string ReadId(JsonElement item) =>
        item.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadName(JsonElement item) =>
        item.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? ReadMimeType(JsonElement item) =>
        item.TryGetProperty("mimeType", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadDriveId(JsonElement item) =>
        item.TryGetProperty("driveId", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadTrashed(JsonElement item) =>
        item.TryGetProperty("trashed", out var value) && value.ValueKind == JsonValueKind.True;

    private static string[] ReadParents(JsonElement item)
    {
        if (!item.TryGetProperty("parents", out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray()
            .Where(parent => parent.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(parent.GetString()))
            .Select(parent => parent.GetString()!)
            .ToArray();
    }

    private static string? ReadToken(JsonElement root) =>
        root.TryGetProperty("nextPageToken", out var token) && token.ValueKind == JsonValueKind.String
            ? token.GetString()
            : null;

    private static string NormalizeFolderName(string name)
    {
        var value = name.Trim();
        if (value.Length is < 1 or > 255 || value is "." or ".." ||
            value.IndexOfAny([(char)47, (char)92, (char)0]) >= 0)
            throw new ArgumentException("Enter one valid Google Drive folder name without slashes.", nameof(name));
        return value;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/" + url);
        return await SendAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> PostJsonAsync(string url, string body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://www.googleapis.com/drive/v3/" + url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return await SendAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        GoogleDriveProviderConfiguration.ValidateConnection(_connection);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            await tokenSource.GetAccessTokenAsync(cancellationToken));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new GoogleDriveApiException(response.StatusCode, response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    "This Google account needs to be reconnected before browsing continues.",
                System.Net.HttpStatusCode.Forbidden =>
                    "Google Drive refused the folder listing. The account may lack the required permission " +
                    "or the project quota may be exhausted.",
                _ => "Google Drive could not list folders for this account."
            });
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bounded = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (bounded.Length > GoogleDrivePilotClient.MaximumJsonResponseBytes - read)
                throw new InvalidDataException("Google Drive returned an oversized folder listing.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return JsonDocument.Parse(bounded.GetBuffer().AsMemory(0, checked((int)bounded.Length)),
            new JsonDocumentOptions { MaxDepth = 32 });
    }
}
