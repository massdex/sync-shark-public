using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public enum GoogleDriveEntryKind
{
    Blob,
    Folder,
    NativeDocument,
    Shortcut,
    Unknown
}

public sealed record GoogleDriveInventoryEntry(
    string RelativePath,
    GoogleDriveEntryKind Kind,
    string FileId,
    string? Name,
    long Size,
    string? Md5,
    string? Sha256,
    string? Version,
    string? HeadRevisionId,
    string? DriveId,
    IReadOnlyList<string> Parents,
    bool Trashed,
    string? ShortcutTargetId,
    string? ShortcutTargetMimeType,
    string MimeType,
    bool CanDownload)
{
    public bool IsByteFile => Kind == GoogleDriveEntryKind.Blob;
    // Drive versions are per-object counters, not globally unique revisions. Bind the stable
    // file id as well so a same-name replacement with the same counter cannot inherit authority.
    public string Revision => JsonSerializer.Serialize(new[] { FileId, Version });

    public PilotRemoteEntry ToRemoteEntry() => new(RelativePath,
        Kind == GoogleDriveEntryKind.Folder ? PilotEntryKind.Directory : PilotEntryKind.File,
        FileId, Revision, Size, Sha256 ?? Md5, TransferBlockedReason: Kind switch
        {
            GoogleDriveEntryKind.NativeDocument => "This Google-native document stays in Google Drive. Open it in the browser; it is not a downloadable byte file and is not replaced by a local file.",
            GoogleDriveEntryKind.Shortcut => "This Google Drive shortcut stays in the cloud. Open the shortcut in the browser; Sync Shark does not follow or overwrite its target.",
            GoogleDriveEntryKind.Unknown => "Google returned an unknown item type. It remains unchanged until its type can be verified.",
            _ => null
        });
}

public sealed record GoogleDriveInventory(
    IReadOnlyList<GoogleDriveInventoryEntry> Entries,
    IReadOnlyList<GoogleDrivePathAmbiguity> Ambiguities,
    bool CorpusComplete)
{
    public bool HasAmbiguity => Ambiguities.Count > 0;
}

// Provider-scoped Google Drive change cursor binding. The corpus is part of the binding so a user-corpus
// token can never be consumed as a shared-drive token (or the reverse).
public sealed record GoogleDriveChangeCursor(string CorpusKind, string CorpusId, string PageToken)
{
    public string IdentityJson => JsonSerializer.Serialize(new { corpusKind = CorpusKind, corpusId = CorpusId });
}

public sealed partial class GoogleDrivePilotClient : ISyncRemoteStore, ISyncRemoteChangeFeed,
    ISyncResumableRemoteStore, ISyncRemoteRangeStore, ISyncTransferResourceEndpoint, ISyncUploadSessionPolicySource
{
    // Shared-core integration gap, stated once so the product can surface it and the parent can assign
    // the fix: the engine's reviewed content identity is Dropbox's 4 MiB block hash, while Google Drive
    // exposes whole-file SHA-256/MD5. An effect whose reviewed identity cannot be tied to the bytes that
    // would be written is refused instead of being written and called safe.
    // Existing-item writes require a reviewed stable identity/version and the actual provider's
    // strong conditional guard. Create-only calls never overwrite existing content.
    public const string DriveUpdateUnsupported =
        "This Google Drive update lacks an enabled conditional-write capability or a reviewed " +
        "version of the existing file. It was refused before writing. Scan again and review the " +
        "current file; a create-only upload cannot replace an existing item.";

    // Retained refusal for explicitly disabled capabilities; no pre-read/post-read substitute.
    public const string DriveDestructiveUnsupported =
        "Sync Shark has not verified an atomic guard for this Google Drive effect. The effect was " +
        "refused rather than mutating an object whose reviewed revision cannot be protected against a " +
        "concurrent change.";
    public const string ProviderDigestIntegrationGap =
        "The reviewed content identity for this file is not the whole-file SHA-256 of the bytes to be " +
        "uploaded. Google Drive reports whole-file SHA-256/MD5, while the shared engine currently derives " +
        "local content identity with the Dropbox 4 MiB block scheme. The write was refused rather than " +
        "sending content whose identity cannot be confirmed.";

    public const string FolderMimeType = "application/vnd.google-apps.folder";
    public const string ShortcutMimeType = "application/vnd.google-apps.shortcut";
    public const string NativeDocumentMimePrefix = "application/vnd.google-apps.";
    public const int MaximumPageSize = 1000;
    public const int MaximumJsonResponseBytes = 16 * 1024 * 1024;
    public const int MaximumInventoryEntries = 500_000;
    // Per-folder page ceiling for the recursive inventory: a listing that keeps returning fresh
    // tokens must not be followed without bound.
    public const int MaximumInventoryPagesPerFolder = 10_000;
    public const int ResumableChunkMultiple = 256 * 1024;
    public const long DefaultResumableThresholdBytes = 5L * 1024 * 1024;
    private const int MaximumCursorBytes = 16 * 1024;
    private const string ApiRoot = "https://www.googleapis.com/drive/v3/";
    private const string UploadRoot = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FileFields =
        "id,name,mimeType,parents,trashed,size,modifiedTime,md5Checksum,sha256Checksum,version," +
        "headRevisionId,driveId,shortcutDetails,ownedByMe,capabilities(canDownload,canEdit,canShare)";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly IGoogleDriveAccessTokenSource _tokenSource;
    private readonly GoogleDriveConnectionBinding _connection;
    private readonly string _rootFolderId;
    private readonly string _rootPath;
    private readonly SyncTransferRateLimiter _transferLimiter;
    private readonly long _resumableThreshold;
    private readonly int _resumableChunkSize;
    private readonly GoogleDriveCapabilities _capabilities;
    private string? _sharedDriveId;
    private readonly bool _allowSharedDrives;
    private long _providerCallCount;
    string ISyncTransferResourceEndpoint.TransferResourceKey => TransferResourceKey;
    private string TransferResourceKey => "google_drive:" + _connection.ConnectionId;
    private Task<HttpResponseMessage> SendResourceRequestAsync(HttpRequestMessage request,
        HttpCompletionOption option, CancellationToken token) =>
        SyncProviderRequestPacing.SendAsync(_transferLimiter,
            new(TransferResourceKey, request.Method == HttpMethod.Get || request.Method == HttpMethod.Head
                ? SyncProviderOperation.Read : SyncProviderOperation.Write),
            true, _httpClient, request, option, token);
    // Publish only a fully constructed snapshot. Readers never share a mutable
    // dictionary with concurrent inventories. These path hints are advisory;
    // fresh provider evidence, not this cache, remains effect authority.
    private sealed record InventorySnapshot(GoogleDriveInventory Inventory,
        FrozenDictionary<string, GoogleDriveInventoryEntry> EntriesById);
    private InventorySnapshot? _inventorySnapshot;

    public GoogleDrivePilotClient(HttpClient httpClient, IGoogleDriveAccessTokenSource tokenSource,
        GoogleDriveConnectionBinding expectedConnection, string rootFolderId, string rootPath,
        SyncTransferRateLimiter? transferLimiter = null,
        long? resumableThresholdBytes = null,
        int? resumableChunkSizeBytes = null,
        GoogleDriveCapabilities? capabilities = null,
        string? sharedDriveId = null,
        bool allowSharedDrives = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolderId);
        _httpClient = httpClient;
        _tokenSource = tokenSource;
        _connection = expectedConnection.Normalize();
        _rootFolderId = rootFolderId.Trim();
        _rootPath = PilotPath.NormalizeDropboxRoot(rootPath);
        _transferLimiter = transferLimiter ?? new SyncTransferRateLimiter(new SyncTransferSettings());
        _resumableThreshold = resumableThresholdBytes ?? DefaultResumableThresholdBytes;
        if (_resumableThreshold < ResumableChunkMultiple || _resumableThreshold > 5L * 1024 * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(resumableThresholdBytes),
                "The Drive resumable threshold must be at least one 256 KiB chunk.");
        _resumableChunkSize = resumableChunkSizeBytes ?? 8 * 1024 * 1024;
        if (_resumableChunkSize < ResumableChunkMultiple || _resumableChunkSize % ResumableChunkMultiple != 0)
            throw new ArgumentOutOfRangeException(nameof(resumableChunkSizeBytes),
                "Drive resumable chunk sizes must be whole multiples of 256 KiB.");
        _capabilities = (capabilities ?? GoogleDriveProviderProfiles.Capabilities())
            .WithSharedDrives(allowSharedDrives || capabilities?.SharedDrives == true);
        _sharedDriveId = string.IsNullOrWhiteSpace(sharedDriveId) ? null : sharedDriveId.Trim();
        _allowSharedDrives = _capabilities.SharedDrives;
    }

    public GoogleDriveConnectionBinding Connection => _connection;

    public GoogleDriveCapabilities Capabilities => _capabilities;

    // A replacement uses one revision-guarded streamed request. It never creates
    // a resumable session, regardless of size; unsupported guards still refuse.
    public SyncUploadSessionPolicy UploadSessionPolicy => new(_resumableThreshold, long.MaxValue);

    public string RootFolderId => _rootFolderId;

    public string RootPath => _rootPath;

    public long ProviderCallCount => Interlocked.Read(ref _providerCallCount);

    public GoogleDriveInventory? LastInventory => Volatile.Read(ref _inventorySnapshot)?.Inventory;

    // A pair bound to a shared drive must have the capability enabled and the exact drive id recorded.
    private bool SharedDrives => _sharedDriveId is not null && _capabilities.SharedDrives;

    public async Task<GoogleDriveAccountIdentity> VerifyAccountAsync(
        CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            "about?fields=user(permissionId,emailAddress,displayName),storageQuota(limit,usage,usageInDrive)," +
            "canCreateDrives,importFormats,exportFormats",
            cancellationToken);
        var root = document.RootElement;
        var user = root.TryGetProperty("user", out var userElement) ? userElement : default;
        var permissionId = user.ValueKind == JsonValueKind.Object
            ? user.TryGetProperty("permissionId", out var permission) ? permission.GetString() : null
            : null;
        if (string.IsNullOrWhiteSpace(permissionId))
            throw new InvalidDataException("The signed-in Google account identity is absent.");
        if (!string.IsNullOrWhiteSpace(_connection.PermissionId) &&
            !string.Equals(permissionId, _connection.PermissionId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "This authorization belongs to a different Google account than the stored connection. " +
                "Existing pairs remain bound to their retained connection and were not reassigned.");
        var email = user.TryGetProperty("emailAddress", out var emailElement) ? emailElement.GetString() : null;
        var display = user.TryGetProperty("displayName", out var displayElement) ? displayElement.GetString() : null;
        var quotaJson = root.TryGetProperty("storageQuota", out var quota) && quota.ValueKind == JsonValueKind.Object
            ? quota.GetRawText()
            : string.Empty;
        return new GoogleDriveAccountIdentity(permissionId!, email ?? string.Empty, display ?? string.Empty,
            quotaJson);
    }

    public async Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default)
    {
        GoogleDriveProviderConfiguration.ValidateConnection(_connection);
        var root = await GetFileAsync(_rootFolderId, cancellationToken);
        if (!string.Equals(root.MimeType, FolderMimeType, StringComparison.Ordinal))
            throw new InvalidDataException("The bound Google Drive item is not a folder.");
        if (root.Trashed)
            throw new InvalidDataException("The bound Google Drive folder is in the trash.");
        if (root.Kind == GoogleDriveEntryKind.Shortcut)
            throw new InvalidDataException("The bound Google Drive item is a shortcut, not a folder.");
        if (!string.Equals(root.FileId, _rootFolderId, StringComparison.Ordinal))
            throw new InvalidDataException("The bound Google Drive folder identity changed.");
        // A shared drive root is only accepted when the connection explicitly enables shared drives.
        // The drive id is discovered from the authoritative folder resource, never guessed from a path.
        if (!string.IsNullOrWhiteSpace(root.DriveId))
        {
            if (!_allowSharedDrives)
                throw new GoogleDriveBlockedException(
                    "This folder lives in shared drive " + root.DriveId +
                    ", but shared drives are not enabled for this connection. Shared drives are an " +
                    "explicit capability; the pair was not silently treated as My Drive.");
            if (_sharedDriveId is not null && !string.Equals(_sharedDriveId, root.DriveId, StringComparison.Ordinal))
                throw new GoogleDriveBlockedException("The bound folder moved to a different shared drive. Rebind explicitly before syncing.");
            _sharedDriveId = root.DriveId;
        }
        else if (_sharedDriveId is not null)
        {
            throw new GoogleDriveBlockedException(
                "This pair is bound to shared drive " + _sharedDriveId +
                " but the selected folder is in My Drive.");
        }
        VerifyCorpusMembership(root);
    }

    private void VerifyCorpusMembership(GoogleDriveInventoryEntry entry)
    {
        if (_sharedDriveId is null)
        {
            if (!string.IsNullOrWhiteSpace(entry.DriveId))
                throw new GoogleDriveBlockedException(
                    "This pair is bound to My Drive, but the item belongs to shared drive " + entry.DriveId +
                    ". Shared drives need an explicit account-scoped root before any transfer.");
            return;
        }
        if (!_capabilities.SharedDrives)
            throw new GoogleDriveBlockedException(
                "Shared drive support is not enabled for this connection.");
        if (!string.Equals(entry.DriveId, _sharedDriveId, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The item is outside the shared drive bound to this pair. No cross-drive move or copy is attempted.");
    }

    // Fresh, component-bound lookup. Never use the last planning inventory as
    // effect/absence authority, and never enumerate unrelated descendant trees.
    public async Task<PilotRemoteEntry> ReadEntryAsync(string relativePath, string providerObjectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerObjectId);
        var entry = await FindEntryAtPathAsync(relativePath, cancellationToken);
        if (entry is null || !string.Equals(entry.Id, providerObjectId, StringComparison.Ordinal))
            throw new IOException("The selected Google Drive endpoint did not return the retained exact item at " +
                PilotPath.NormalizeRelative(relativePath) + ". Scan again; a same-name replacement is not the reviewed object.");
        return entry;
    }

    public async Task<PilotRemoteEntry?> FindEntryAtPathAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        var path = PilotPath.NormalizeRelative(relativePath);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 128)
            throw new GoogleDriveBlockedException("An exact Drive lookup needs a bounded non-root path.");
        await VerifyRootIdentityAsync(cancellationToken);
        var parentId = _rootFolderId;
        var ancestors = new List<(GoogleDriveInventoryEntry Entry, string ParentId)>();
        var actualParts = new List<string>();
        GoogleDriveInventoryEntry? selected = null;
        for (var index = 0; index < parts.Length; index++)
        {
            var candidates = await ListChildrenByNameAsync(parentId, parts[index], cancellationToken,
                canonicalMatch: true);
            if (candidates.Count > 1)
                throw new GoogleDriveBlockedException("The exact Drive path is ambiguous at '" + parts[index] +
                    "'. Duplicate, case-equivalent or Unicode-equivalent names are not chosen or treated as absent.");
            if (candidates.Count == 0) { selected = null; break; }
            var listed = candidates[0];
            var current = await GetFileAsync(listed.FileId, cancellationToken);
            RequireExactLookupMembership(current, listed, parentId);
            actualParts.Add(current.Name!);
            if (index == parts.Length - 1) { selected = current; break; }
            if (current.Kind != GoogleDriveEntryKind.Folder)
                throw new GoogleDriveBlockedException("A Drive path component is not an ordinary folder; no descendant absence can be inferred.");
            ancestors.Add((current, parentId));
            parentId = current.FileId;
        }
        // A parent moved/renamed while navigating cannot redirect absence or
        // current-file authority. Rebind the complete selected ancestry and root.
        foreach (var (ancestor, parent) in ancestors)
        {
            var current = await GetFileAsync(ancestor.FileId, cancellationToken);
            RequireExactLookupMembership(current, ancestor, parent);
            if (current.Kind != GoogleDriveEntryKind.Folder)
                throw new GoogleDriveBlockedException("A Drive lookup ancestor changed type; scan again.");
        }
        await VerifyRootIdentityAsync(cancellationToken);
        return selected is null ? null :
            (selected with { RelativePath = PilotPath.NormalizeRelative(string.Join('/', actualParts)) }).ToRemoteEntry();
    }

    private void RequireExactLookupMembership(GoogleDriveInventoryEntry current,
        GoogleDriveInventoryEntry expected, string parentId)
    {
        VerifyCorpusMembership(current);
        if (string.IsNullOrWhiteSpace(current.FileId) || current.FileId != expected.FileId || current.Trashed ||
            string.IsNullOrEmpty(current.Name) || string.IsNullOrEmpty(expected.Name) ||
            CanonicalChildName(current.Name) != CanonicalChildName(expected.Name) ||
            current.Kind != expected.Kind || current.Parents.Count != 1 || current.Parents[0] != parentId)
            throw new GoogleDriveBlockedException("The Drive lookup object changed identity, name, type or parent; no cached or out-of-root authority is accepted.");
    }

    private static string CanonicalChildName(string name) =>
        name.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    // Full recursive inventory. Duplicate sibling names, case collisions and Windows-invalid names are
    // retained as explicit ambiguities; ListEntriesAsync refuses rather than choosing one silently.
    public Task<GoogleDriveInventory> ListInventoryAsync(CancellationToken cancellationToken = default) =>
        ListInventoryCoreAsync(null, cancellationToken);

    private async Task<GoogleDriveInventory> ListInventoryCoreAsync(
        Func<SyncInventoryProgress, Task>? onProgress, CancellationToken cancellationToken)
    {
        await VerifyRootIdentityAsync(cancellationToken);
        var entries = new List<GoogleDriveInventoryEntry>();
        var pending = new Queue<(string FolderId, string RelativePath)>();
        pending.Enqueue((_rootFolderId, string.Empty));
        var visited = new HashSet<string>(StringComparer.Ordinal) { _rootFolderId };
        var complete = true;
        while (pending.Count > 0)
        {
            var (folderId, folderPath) = pending.Dequeue();
            string? pageToken = null;
            var pageNumber = 0;
            var pageCalls = 0;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                // Count this request before it is sent and independently of progress reporting: a
                // caller that supplies no callback must still be bounded per folder.
                if (++pageCalls > MaximumInventoryPagesPerFolder)
                    throw new GoogleDriveBlockedException(
                        "The Google Drive folder listing exceeded the bounded limit of " +
                        MaximumInventoryPagesPerFolder.ToString("N0", CultureInfo.InvariantCulture) +
                        " pages for one folder, so no complete inventory is available.");
                if (onProgress is not null)
                    await onProgress(new("Reading Google Drive Inventory", folderPath.Length == 0 ? "/" : folderPath,
                        entries.Count, $"Requesting Google Drive folder listing — page {++pageNumber:N0}; waiting for Google's response."));
                var query = Uri.EscapeDataString("'" + folderId + "' in parents and trashed = false");
                var url = "files?q=" + query + "&pageSize=" + MaximumPageSize.ToString(CultureInfo.InvariantCulture) +
                          "&fields=nextPageToken,incompleteSearch,files(" + FileFields + ")" +
                          "&supportsAllDrives=" + (SharedDrives ? "true" : "false") +
                          "&includeItemsFromAllDrives=" + (SharedDrives ? "true" : "false");
                if (pageToken is not null)
                    url += "&pageToken=" + Uri.EscapeDataString(RequireOpaqueCursor(pageToken));
                using var document = await GetJsonAsync(url, cancellationToken);
                var root = document.RootElement;
                if (root.TryGetProperty("incompleteSearch", out var incomplete) &&
                    incomplete.ValueKind == JsonValueKind.True)
                    complete = false;
                if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    foreach (var item in files.EnumerateArray())
                    {
                        var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                        if (string.IsNullOrEmpty(name)) continue;
                        var relative = folderPath.Length == 0
                            ? PilotPath.NormalizeRelative(name)
                            : PilotPath.NormalizeRelative(folderPath + "/" + name);
                        var entry = ReadInventoryEntry(item, relative);
                        entries.Add(entry);
                        if (entries.Count > MaximumInventoryEntries)
                            throw new GoogleDriveBlockedException(
                                "The Google Drive inventory exceeded the bounded limit of " +
                                MaximumInventoryEntries.ToString("N0", CultureInfo.InvariantCulture) + " entries.");
                        if (entry.Kind == GoogleDriveEntryKind.Folder && visited.Add(entry.FileId))
                            pending.Enqueue((entry.FileId, relative));
                    }
                pageToken = root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
                // A repeated token is a cycle rather than progress, and the token is never echoed.
                if (!string.IsNullOrEmpty(pageToken) && !seenTokens.Add(pageToken))
                    throw new GoogleDriveBlockedException(
                        "The Google Drive folder listing repeated a page token, so this folder's " +
                        "enumeration is a cycle and no complete inventory is available.");
                if (onProgress is not null)
                    await onProgress(new("Reading Google Drive Inventory", folderPath.Length == 0 ? "/" : folderPath,
                        entries.Count, $"Read Google Drive folder listing page {pageNumber:N0}; {pending.Count:N0} folders remain to inspect."));
            } while (!string.IsNullOrEmpty(pageToken));
        }
        var ambiguities = DetectAmbiguities(entries);
        var inventory = new GoogleDriveInventory(Array.AsReadOnly(entries.ToArray()),
            Array.AsReadOnly(ambiguities.ToArray()), complete);
        // A multiply-parented ID may have several paths. Do not choose one as
        // the change hint; the next complete observation resolves its ambiguity.
        var byId = entries.GroupBy(entry => entry.FileId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToFrozenDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        Volatile.Write(ref _inventorySnapshot, new InventorySnapshot(inventory, byId));
        return inventory;
    }

    // The engine consumes a flat entry list. Ambiguity is a hard, actionable blocker: the ids are kept
    // on the exception and no item is silently chosen, renamed or dropped.
    public Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(
        CancellationToken cancellationToken = default) => ListEntriesCoreAsync(null, cancellationToken);

    public Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(
        Func<SyncInventoryProgress, Task> onProgress, CancellationToken cancellationToken = default) =>
        ListEntriesCoreAsync(onProgress, cancellationToken);

    private async Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesCoreAsync(
        Func<SyncInventoryProgress, Task>? onProgress, CancellationToken cancellationToken)
    {
        var inventory = await ListInventoryCoreAsync(onProgress, cancellationToken);
        if (inventory.Ambiguities.Count > 0)
            throw new GoogleDriveBlockedException(BuildAmbiguityMessage(inventory.Ambiguities),
                inventory.Ambiguities);
        if (!inventory.CorpusComplete)
            throw new GoogleDriveBlockedException(
                "Google Drive reported an incomplete search for this folder. An incomplete inventory is " +
                "never treated as proof that an item is absent, so no synchronisation decision is taken.");
        return inventory.Entries.Select(item => item.ToRemoteEntry()).ToArray();
    }

    public static string BuildAmbiguityMessage(IReadOnlyList<GoogleDrivePathAmbiguity> ambiguities)
    {
        var sample = ambiguities.Take(3).Select(item =>
            item.RelativePath + " (" + item.Reason + ": " + string.Join(", ", item.FileIds) + ")");
        return "Google Drive returned " + ambiguities.Count.ToString(CultureInfo.InvariantCulture) +
               " ambiguous paths in this folder (" + string.Join("; ", sample) +
               "). Items are kept by identity; choose which object each path refers to before syncing.";
    }

    private static IReadOnlyList<GoogleDrivePathAmbiguity> DetectAmbiguities(
        IReadOnlyList<GoogleDriveInventoryEntry> entries)
    {
        var ambiguities = new List<GoogleDrivePathAmbiguity>();
        foreach (var group in entries.GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var distinct = group.GroupBy(item => item.FileId, StringComparer.Ordinal).Select(item => item.First())
                .ToArray();
            if (distinct.Length > 1)
                ambiguities.Add(new GoogleDrivePathAmbiguity(group.Key, "duplicate-name", 
                    distinct.Select(item => item.FileId).ToArray(),
                    distinct.Select(item => item.Name ?? item.RelativePath).ToArray()));
            else if (!IsWindowsRepresentablePath(group.Key))
                ambiguities.Add(new GoogleDrivePathAmbiguity(group.Key, "windows-invalid-name",
                    distinct.Select(item => item.FileId).ToArray(),
                    distinct.Select(item => item.Name ?? group.Key).ToArray()));
        }
        return ambiguities.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsWindowsRepresentablePath(string relativePath)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8",
            "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length == 0 || segment.Length > 255) return false;
            if (segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0) return false;
            if (segment.Any(character => character < ' ')) return false;
            if (segment.EndsWith(' ') || segment.EndsWith('.')) return false;
            var stem = segment;
            var dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem[..dot];
            if (reserved.Contains(stem)) return false;
        }
        return true;
    }

    private GoogleDriveInventoryEntry ReadInventoryEntry(JsonElement item, string relativePath)
    {
        var fileId = item.GetProperty("id").GetString()
                     ?? throw new InvalidDataException("A Google Drive item has no id.");
        var mimeType = item.TryGetProperty("mimeType", out var mime) ? mime.GetString() ?? string.Empty : string.Empty;
        var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        var trashed = item.TryGetProperty("trashed", out var trashedElement) &&
                      trashedElement.ValueKind == JsonValueKind.True;
        var size = item.TryGetProperty("size", out var sizeElement) &&
                   long.TryParse(sizeElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                       out var parsedSize)
            ? parsedSize
            : 0;
        var md5 = item.TryGetProperty("md5Checksum", out var md5Element) ? md5Element.GetString() : null;
        var sha256 = item.TryGetProperty("sha256Checksum", out var shaElement) ? shaElement.GetString() : null;
        var version = item.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
        var headRevision = item.TryGetProperty("headRevisionId", out var headElement) ? headElement.GetString() : null;
        var driveId = item.TryGetProperty("driveId", out var driveElement) ? driveElement.GetString() : null;
        var parents = item.TryGetProperty("parents", out var parentsElement) &&
                      parentsElement.ValueKind == JsonValueKind.Array
            ? parentsElement.EnumerateArray().Select(value => value.GetString() ?? string.Empty)
                .Where(value => value.Length > 0).ToArray()
            : [];
        var canDownload = true;
        if (item.TryGetProperty("capabilities", out var capabilities) &&
            capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty("canDownload", out var canDownloadElement) &&
            canDownloadElement.ValueKind == JsonValueKind.False)
            canDownload = false;
        string? shortcutTargetId = null;
        string? shortcutTargetMime = null;
        var kind = ClassifyMimeType(mimeType);
        if (item.TryGetProperty("shortcutDetails", out var shortcut) && shortcut.ValueKind == JsonValueKind.Object)
        {
            kind = GoogleDriveEntryKind.Shortcut;
            shortcutTargetId = shortcut.TryGetProperty("targetId", out var target) ? target.GetString() : null;
            shortcutTargetMime = shortcut.TryGetProperty("targetMimeType", out var targetMime)
                ? targetMime.GetString()
                : null;
        }
        return new GoogleDriveInventoryEntry(relativePath, kind, fileId, name, size, md5, sha256, version,
            headRevision, driveId, parents, trashed, shortcutTargetId, shortcutTargetMime, mimeType, canDownload);
    }

    public static GoogleDriveEntryKind ClassifyMimeType(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType)) return GoogleDriveEntryKind.Unknown;
        if (string.Equals(mimeType, FolderMimeType, StringComparison.Ordinal)) return GoogleDriveEntryKind.Folder;
        if (string.Equals(mimeType, ShortcutMimeType, StringComparison.Ordinal)) return GoogleDriveEntryKind.Shortcut;
        if (mimeType.StartsWith(NativeDocumentMimePrefix, StringComparison.Ordinal))
            return GoogleDriveEntryKind.NativeDocument;
        return GoogleDriveEntryKind.Blob;
    }

    internal async Task<GoogleDriveInventoryEntry> GetFileAsync(string fileId,
        CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            "files/" + Uri.EscapeDataString(fileId) + "?fields=" + FileFields +
            "&supportsAllDrives=" + (SharedDrives ? "true" : "false"), cancellationToken);
        return ReadInventoryEntry(document.RootElement, string.Empty);
    }

    // Corrected method: the documented bootstrap is GET changes/startPageToken (not POST), optionally
    // scoped to one shared drive with driveId.
    public async Task<GoogleDriveChangeCursor> GetLatestCursorAsync(
        CancellationToken cancellationToken = default)
    {
        await VerifyRootIdentityAsync(cancellationToken);
        var url = "changes/startPageToken?fields=startPageToken";
        if (_sharedDriveId is not null)
            url += "&driveId=" + Uri.EscapeDataString(_sharedDriveId) +
                   "&supportsAllDrives=" + (SharedDrives ? "true" : "false");
        using var document = await GetJsonAsync(url, cancellationToken);
        var token = document.RootElement.TryGetProperty("startPageToken", out var value)
            ? value.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidDataException("Google Drive returned no start page token.");
        return new GoogleDriveChangeCursor(_sharedDriveId is null ? "user" : "drive",
            _sharedDriveId ?? _connection.PermissionId, RequireOpaqueCursor(token!));
    }

    async Task<string> ISyncRemoteChangeFeed.GetLatestCursorAsync(CancellationToken cancellationToken) =>
        (await GetLatestCursorAsync(cancellationToken)).PageToken;

    public async Task<SyncRemoteChangePage> ContinueChangesAsync(string cursor,
        CancellationToken cancellationToken = default)
    {
        var inventory = Volatile.Read(ref _inventorySnapshot);
        cursor = RequireOpaqueCursor(cursor);
        var url = "changes?pageToken=" + Uri.EscapeDataString(cursor) +
                  "&includeRemoved=true&pageSize=" + MaximumPageSize.ToString(CultureInfo.InvariantCulture) +
                  "&fields=nextPageToken,newStartPageToken,changes(fileId,removed,time,changeType," +
                  "file(" + FileFields + "))";
        if (_sharedDriveId is null)
            url += "&restrictToMyDrive=true";
        else
            url += "&driveId=" + Uri.EscapeDataString(_sharedDriveId) +
                   "&supportsAllDrives=" + (SharedDrives ? "true" : "false") +
                   "&includeItemsFromAllDrives=true";
        using var document = await GetJsonAsync(url, cancellationToken);
        var root = document.RootElement;
        var next = root.TryGetProperty("nextPageToken", out var nextElement) ? nextElement.GetString() : null;
        var newStart = root.TryGetProperty("newStartPageToken", out var startElement)
            ? startElement.GetString()
            : null;
        var events = new List<SyncRemoteChangeEntry>();
        // This cache lives for ONE page, not an inventory or an account-wide scan.
        // A missing/inaccessible ancestor is unknown, never proof outside the root.
        var ancestors = new Dictionary<string, GoogleDriveInventoryEntry?>(StringComparer.Ordinal);
        string? corpusRoot = _sharedDriveId;
        var corpusRootAttempted = corpusRoot is not null;
        if (root.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
            foreach (var change in changes.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object)
                {
                    events.Add(new SyncRemoteChangeEntry(null, "google-drive-changed", "{\"malformedChange\":true}"));
                    continue;
                }
                var fileId = change.TryGetProperty("fileId", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString() : null;
                var removed = change.TryGetProperty("removed", out var removedElement) &&
                              removedElement.ValueKind == JsonValueKind.True;
                string? relative = null;
                GoogleDriveInventoryEntry? entry = null;
                var prior = fileId is not null && inventory is not null &&
                    inventory.EntriesById.TryGetValue(fileId, out var known) ? known : null;
                var outside = false;
                if (!removed && change.TryGetProperty("file", out var file) &&
                    file.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        entry = ReadChangeAncestryEntry(file);
                        if (entry.FileId == fileId && !entry.Trashed && entry.Kind != GoogleDriveEntryKind.Unknown)
                        {
                            if (!corpusRootAttempted)
                            {
                                corpusRootAttempted = true;
                                try
                                {
                                    var accountRoot = await GetFileAsync("root", cancellationToken);
                                    if (accountRoot.Kind == GoogleDriveEntryKind.Folder && !accountRoot.Trashed)
                                        corpusRoot = accountRoot.FileId;
                                }
                                catch (Exception error) when (IsChangeAncestryUnavailable(error)) { }
                            }
                            (outside, relative) = await ResolveChangeRootAsync(entry, corpusRoot, ancestors,
                                cancellationToken);
                        }
                    }
                    catch (Exception error) when (IsChangeAncestryUnavailable(error)) { entry = null; }
                }
                // A known item moved out still invalidates its OLD in-root path.
                // Removed/trashed/unknown inputs retain an advisory reconciliation hint.
                if (outside && prior is null) continue;
                if (outside || removed || entry?.Trashed == true) relative = prior?.RelativePath;
                var kind = removed
                    ? "google-drive-removed"
                    : entry is null ? "google-drive-changed" : "google-drive-" + entry.Kind.ToString().ToLowerInvariant();
                var evidence = JsonSerializer.Serialize(new
                {
                    fileId,
                    removed,
                    relativePath = relative,
                    kind,
                    trashed = entry?.Trashed ?? false,
                    version = entry?.Version,
                    headRevisionId = entry?.HeadRevisionId,
                    mimeType = entry?.MimeType
                });
                events.Add(new SyncRemoteChangeEntry(relative, kind, evidence));
                if (prior is not null && relative is not null && prior.RelativePath != relative)
                    events.Add(new SyncRemoteChangeEntry(prior.RelativePath, "google-drive-changed",
                        JsonSerializer.Serialize(new { fileId, relativePath = prior.RelativePath,
                            kind = "google-drive-changed", priorPath = true })));
            }
        var pageCursor = !string.IsNullOrEmpty(next) ? RequireOpaqueCursor(next!)
            : !string.IsNullOrEmpty(newStart) ? RequireOpaqueCursor(newStart!)
            : cursor;
        return new SyncRemoteChangePage(pageCursor, !string.IsNullOrEmpty(next), events);
    }

    private static bool IsChangeAncestryUnavailable(Exception error) => error is
        GoogleDriveApiException or HttpRequestException or JsonException or InvalidDataException or
        InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException;

    private GoogleDriveInventoryEntry ReadChangeAncestryEntry(JsonElement file)
    {
        if (file.TryGetProperty("parents", out var parents) &&
            (parents.ValueKind != JsonValueKind.Array || parents.EnumerateArray().Any(parent =>
                parent.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(parent.GetString()))))
            throw new InvalidDataException("The change's ancestry is malformed, not proven outside the root.");
        if (file.TryGetProperty("trashed", out var trashed) &&
            trashed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("The change's trash state is not known.");
        return ReadInventoryEntry(file, string.Empty);
    }

    private async Task<(bool Outside, string? Relative)> ResolveChangeRootAsync(GoogleDriveInventoryEntry item,
        string? corpusRoot, Dictionary<string, GoogleDriveInventoryEntry?> ancestors, CancellationToken token)
    {
        const int maximumDepth = 128;
        const int maximumParentReads = 2048;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        for (var depth = 0; depth < maximumDepth; depth++)
        {
            if (item.Trashed || !visited.Add(item.FileId)) return (false, null);
            if (item.FileId == _rootFolderId)
                return (false, names.Count == 0 ? null : PilotPath.NormalizeRelative(string.Join('/', names.AsEnumerable().Reverse())));
            if (corpusRoot is not null && item.FileId == corpusRoot) return (true, null);
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Contains('/') || item.Name.Contains('\\') ||
                item.Parents.Count != 1 || string.IsNullOrWhiteSpace(item.Parents[0])) return (false, null);
            names.Add(PilotPath.NormalizeRelative(item.Name));
            var parentId = item.Parents[0];
            if (parentId == _rootFolderId)
                return (false, PilotPath.NormalizeRelative(string.Join('/', names.AsEnumerable().Reverse())));
            if (corpusRoot is not null && parentId == corpusRoot) return (true, null);
            if (!ancestors.TryGetValue(parentId, out var parent))
            {
                if (ancestors.Count >= maximumParentReads) return (false, null);
                try
                {
                    using var metadata = await GetJsonAsync("files/" + Uri.EscapeDataString(parentId) +
                        "?fields=" + FileFields + "&supportsAllDrives=" + (SharedDrives ? "true" : "false"), token);
                    parent = ReadChangeAncestryEntry(metadata.RootElement);
                    if (parent.FileId != parentId || parent.Kind != GoogleDriveEntryKind.Folder) parent = null;
                }
                catch (Exception error) when (IsChangeAncestryUnavailable(error)) { parent = null; }
                ancestors.Add(parentId, parent);
            }
            if (parent is null) return (false, null);
            item = parent;
        }
        return (false, null);
    }

    public static IReadOnlyList<GoogleDriveInventoryEntry> FilterByteFiles(GoogleDriveInventory inventory) =>
        inventory.Entries.Where(item => item.IsByteFile).ToArray();

    public static IReadOnlyList<GoogleDriveInventoryEntry> FilterCloudNative(GoogleDriveInventory inventory) =>
        inventory.Entries.Where(item => item.Kind == GoogleDriveEntryKind.NativeDocument ||
                                        item.Kind == GoogleDriveEntryKind.Shortcut).ToArray();

    private string RequireOpaqueCursor(string cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        if (Encoding.UTF8.GetByteCount(cursor) > MaximumCursorBytes)
            throw new InvalidDataException(
                "Google Drive returned a page token larger than the bounded limit.");
        return cursor;
    }

    private string ToRelative(string? drivePath)
    {
        if (string.IsNullOrEmpty(drivePath)) return string.Empty;
        var normalized = PilotPath.NormalizeDropboxRoot(drivePath);
        if (_rootPath.Length == 0 || _rootPath == "/") return PilotPath.NormalizeRelative(normalized.TrimStart('/'));
        if (!normalized.StartsWith(_rootPath + "/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalized, _rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Google Drive returned an item outside the bound folder.");
        return string.Equals(normalized, _rootPath, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : PilotPath.NormalizeRelative(normalized[(_rootPath.Length + 1)..]);
    }

    private async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string uri,
        CancellationToken cancellationToken)
    {
        var token = await _tokenSource.GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("The Google Drive access token is unavailable.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        var uri = relativeUrl.StartsWith("https://", StringComparison.Ordinal)
            ? relativeUrl
            : ApiRoot + relativeUrl;
        for (var attempt = 0; ; attempt++)
        {
            using var request = await AuthorizedAsync(HttpMethod.Get, uri, cancellationToken);
            Interlocked.Increment(ref _providerCallCount);
            using var response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (TryRetryable(response, attempt,
                await SyncProviderRequestPacing.IsGoogleQuotaAsync(response, cancellationToken), out var delay))
            {
                if (delay > TimeSpan.FromMinutes(5))
                    throw new HttpRequestException($"Google Drive requires {delay.TotalSeconds:N0} seconds of backoff; this read is deferred. No earlier request will be sent.", null, response.StatusCode);
                await SyncByteProgressStream.ReportProcessAsync("Waiting to retry Google Drive read", _rootPath,
                    $"Google Drive returned HTTP {(int)response.StatusCode}. Read-only retry {attempt + 1} after {delay.TotalSeconds:N1} seconds; no write is being replayed.");
                await Task.Delay(delay, cancellationToken);
                continue;
            }
            ThrowIfFailed(response);
            return await ReadBoundedJsonAsync(response, cancellationToken);
        }
    }

    private async Task<JsonDocument> SendEffectJsonAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        Interlocked.Increment(ref _providerCallCount);
        try
        {
            response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new GoogleDriveEffectUncertainException(
                "The Google Drive effect acknowledgement was interrupted after the request was sent, so " +
                "whether it was accepted is unknown.", exception);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests)
                    throw new GoogleDriveEffectUncertainException(
                        "Google Drive returned HTTP " + (int)response.StatusCode +
                        " after the effect request was sent and did not prove whether it was accepted.");
                ThrowIfFailed(response);
            }
            return await ReadBoundedJsonAsync(response, cancellationToken);
        }
    }

    private static bool TryRetryable(HttpResponseMessage response, int attempt, bool quota403, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (attempt >= 6) return false;
        var retryable = response.StatusCode is HttpStatusCode.TooManyRequests ||
                        (int)response.StatusCode >= 500 ||
                        quota403;
        if (!retryable) return false;
        // Google documents both 403 userRateLimitExceeded and 429 for rate pressure, and does not
        // guarantee Retry-After. Use the server hint when present and a truncated exponential backoff
        // with jitter otherwise.
        var retryAfter = response.Headers.RetryAfter;
        var hinted = retryAfter?.Delta ??
                     (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        var backoff = TimeSpan.FromMilliseconds(Math.Min(64_000,
            500 * Math.Pow(2, attempt) + Random.Shared.Next(0, 250)));
        delay = hinted is { } value && value > TimeSpan.Zero
            ? value
            : backoff;
        if (delay < TimeSpan.FromMilliseconds(100)) delay = TimeSpan.FromMilliseconds(100);
        return true;
    }

    private static void ThrowIfFailed(HttpResponseMessage response)
    {
        ThrowIfFailedCore(response);
    }

    private async Task CopyThrottledAsync(Stream source, Stream destination,
        CancellationToken cancellationToken)
    {
        var restore = source.CanSeek ? source.Position : 0;
        if (source.CanSeek) source.Position = 0;
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await _transferLimiter.ThrottleAsync(SyncTransferDirection.Upload, read, cancellationToken);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (source.CanSeek) source.Position = restore;
    }

    // Whole-file SHA-256 of the exact bytes about to be sent. This is the digest scheme Google Drive
    // reports for binary files, so it can be compared with the provider-declared checksum.
    private static async Task<string> ComputeSourceDigestAsync(Stream source,
        CancellationToken cancellationToken)
    {
        if (!source.CanSeek)
            throw new IOException("The Google Drive upload source must be seekable to confirm its identity.");
        var restore = source.Position;
        source.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        source.Position = restore;
        return ToHexLower(hash.GetHashAndReset());
    }

    private static void ThrowIfFailedCore(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new GoogleDriveBlockedException("Google rejected the stale version guard. The concurrent edit was not overwritten; scan again.");
        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "This Google account needs to be reconnected before syncing continues.",
            HttpStatusCode.Forbidden =>
                "Google Drive refused the request. The account may lack the required permission or the " +
                "project or user quota may be exhausted.",
            HttpStatusCode.NotFound =>
                "The Google Drive item does not exist or the account cannot read it. Deletion and lost " +
                "access are not distinguished by this response.",
            HttpStatusCode.TooManyRequests =>
                "Google Drive is rate limiting requests. Wait briefly, then retry.",
            HttpStatusCode.Conflict =>
                "Google Drive rejected the change because the item changed or the request conflicted.",
            _ => "Google Drive could not complete this request."
        };
        throw new GoogleDriveApiException(response.StatusCode, reason);
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumJsonResponseBytes)
            throw new InvalidDataException("Google Drive returned an oversized JSON response.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bounded = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (bounded.Length > MaximumJsonResponseBytes - read)
                throw new InvalidDataException("Google Drive returned an oversized JSON response.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (bounded.Length == 0) return JsonDocument.Parse("{}");
        return JsonDocument.Parse(bounded.GetBuffer().AsMemory(0, checked((int)bounded.Length)),
            new JsonDocumentOptions { MaxDepth = 64 });
    }

    internal static string ToHexLower(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(value).ToLowerInvariant();

    internal static string ComputeSha256Hex(ReadOnlySpan<byte> content) => ToHexLower(SHA256.HashData(content));

    internal static string ComputeMd5Hex(ReadOnlySpan<byte> content) => ToHexLower(MD5.HashData(content));

    internal static bool HexDigestMatches(string? declared, string observed)
    {
        if (string.IsNullOrWhiteSpace(declared)) return false;
        var hex = declared.Trim().ToLowerInvariant();
        if (hex.Length is not (32 or 64)) return false;
        if (!hex.All(character => Uri.IsHexDigit(character))) return false;
        return string.Equals(hex, observed, StringComparison.Ordinal);
    }
}

public sealed record GoogleDriveAccountIdentity(string PermissionId, string Email, string DisplayName,
    string StorageQuotaJson);

// Transfer and effect surface. Every mutating path is identity-bound: it re-reads the exact Drive object
// it reviewed, refuses an update whose reviewed version no longer matches, and never falls back to a
// same-name object. Google Drive documents no atomic conditional write for file content, so an unsafe
// case fails closed with an actionable reason instead of being written and called safe.
public sealed partial class GoogleDrivePilotClient
{
    private async Task<GoogleDriveInventoryEntry> RequireReviewedEntryAsync(PilotRemoteEntry expected,
        bool requireDigest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expected.Id))
            throw new InvalidDataException("The Google Drive item identity is absent.");
        var current = await GetFileAsync(expected.Id, cancellationToken);
        if (current.Kind == GoogleDriveEntryKind.Shortcut)
            throw new GoogleDriveBlockedException(
                "This item is a Google Drive shortcut. Shortcuts are not followed by default, so no " +
                "transfer or mutation is performed on the target.");
        if (current.Kind == GoogleDriveEntryKind.NativeDocument)
            throw new GoogleDriveBlockedException(
                "This item is a Google-native document with no byte content. It is never treated as a " +
                "downloadable file; export is an explicit one-way action instead.");
        if (!string.Equals(current.FileId, expected.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The Google Drive item identity changed after inventory.");
        if (expected.Kind == PilotEntryKind.Directory)
        {
            if (current.Kind != GoogleDriveEntryKind.Folder)
                throw new GoogleDriveBlockedException("The reviewed item is no longer a folder.");
        }
        if (string.IsNullOrWhiteSpace(expected.Revision) ||
            !string.Equals(current.Revision, expected.Revision, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The Google Drive file changed after it was reviewed (expected version " +
                expected.Revision + ", found " + (current.Version ?? "unknown") +
                "). The effect was refused rather than overwriting newer content.");
        if (requireDigest && current.Md5 is null && current.Sha256 is null && current.Size > 0)
            throw new GoogleDriveBlockedException(
                "Google Drive did not provide a checksum for this binary file, so its content cannot be " +
                "verified against the reviewed version.");
        return current;
    }

    // Read-only probe: is there already a non-folder item at this path? Used to refuse an existing-item
    // update before any write, including parent-folder creation. A missing or ambiguous parent returns
    // false here and is decided by the create path.
    private async Task<bool> HasExistingContentSiblingAsync(string relativePath,
        CancellationToken cancellationToken)
    {
        var normalized = PilotPath.NormalizeRelative(relativePath);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        var parentId = _rootFolderId;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var matches = await ListChildrenByNameAsync(parentId, segments[index], cancellationToken);
            var folders = matches.Where(item => item.Kind == GoogleDriveEntryKind.Folder).ToArray();
            if (folders.Length != 1) return false;
            parentId = folders[0].FileId;
        }
        var existing = await ListChildrenByNameAsync(parentId, segments[^1], cancellationToken);
        return existing.Any(item => item.Kind != GoogleDriveEntryKind.Folder);
    }
    private async Task<(string ParentId, string Name)> ResolveParentAsync(string relativePath,
        bool createMissing, CancellationToken cancellationToken)
    {
        var normalized = PilotPath.NormalizeRelative(relativePath);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new InvalidDataException("A Google Drive upload needs a file name.");
        var parentId = _rootFolderId;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var child = segments[index];
            var matches = await ListChildrenByNameAsync(parentId, child, cancellationToken);
            var folders = matches.Where(item => item.Kind == GoogleDriveEntryKind.Folder).ToArray();
            if (folders.Length > 1)
                throw new GoogleDriveBlockedException(
                    "Google Drive has more than one folder named '" + child + "' in this position. " +
                    "Choose which folder this pair uses before syncing.");
            if (folders.Length == 1)
            {
                parentId = folders[0].FileId;
                continue;
            }
            if (matches.Count > 0)
                throw new GoogleDriveBlockedException(
                    "'" + child + "' exists in Google Drive as a file, not a folder. The pair cannot " +
                    "create a folder with the same name.");
            if (!createMissing)
                throw new GoogleDriveBlockedException(
                    "The Google Drive folder '" + child + "' does not exist and creating it was not allowed.");
            var created = await CreateFolderAsync(parentId, child, cancellationToken);
            parentId = created.FileId;
        }
        return (parentId, segments[^1]);
    }

    private async Task<IReadOnlyList<GoogleDriveInventoryEntry>> ListChildrenByNameAsync(string parentId,
        string name, CancellationToken cancellationToken, bool canonicalMatch = false)
    {
        var escapedName = name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        var escapedParent = parentId.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        var query = Uri.EscapeDataString(
            "'" + escapedParent + "' in parents" +
            (canonicalMatch ? "" : " and name = '" + escapedName + "'") + " and trashed = false");
        var results = new List<GoogleDriveInventoryEntry>();
        string? pageToken = null;
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            using var document = await GetJsonAsync(
                "files?q=" + query + "&pageSize=1000&fields=nextPageToken,incompleteSearch,files(" + FileFields + ")" +
                "&supportsAllDrives=" + (SharedDrives ? "true" : "false") +
                "&includeItemsFromAllDrives=" + (SharedDrives ? "true" : "false") +
                (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken)), cancellationToken);
            var root = document.RootElement;
            if (root.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
                throw new GoogleDriveBlockedException("Google returned an incomplete name lookup; no absent item can be inferred.");
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Google returned an invalid name lookup.");
            foreach (var item in files.EnumerateArray())
            {
                var entry = ReadInventoryEntry(item, string.Empty);
                if (canonicalMatch)
                {
                    // Drive's documented equality filter does not establish
                    // Windows/NFC equivalence. Inspect this parent's children,
                    // not the entire root, before inferring canonical absence.
                    VerifyCorpusMembership(entry);
                    if (string.IsNullOrWhiteSpace(entry.FileId) || string.IsNullOrEmpty(entry.Name) || entry.Trashed ||
                        entry.Parents.Count != 1 || entry.Parents[0] != parentId)
                        throw new InvalidDataException("Google returned invalid or foreign parent-lookup metadata; absence is not proven.");
                    if (CanonicalChildName(entry.Name) != CanonicalChildName(name)) continue;
                }
                results.Add(entry with { RelativePath = PilotPath.NormalizeRelative(canonicalMatch ? entry.Name! : name) });
            }
            pageToken = root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (pageToken is not null && (tokens.Count >= 100 || !tokens.Add(pageToken)))
                throw new GoogleDriveBlockedException("Google name lookup could not finish safely; nothing was changed.");
        } while (pageToken is not null);
        return results;
    }

    public async Task<GoogleDriveInventoryEntry> CreateFolderAsync(string parentId, string name,
        CancellationToken cancellationToken = default)
    {
        var existing = await ListChildrenByNameAsync(parentId, name, cancellationToken);
        var folders = existing.Where(item => item.Kind == GoogleDriveEntryKind.Folder).ToArray();
        if (folders.Length > 1)
            throw new GoogleDriveBlockedException(
                "Google Drive already contains more than one folder named '" + name + "' here.");
        if (folders.Length == 1) return folders[0];
        if (existing.Count > 0)
            throw new GoogleDriveBlockedException(
                "'" + name + "' already exists here as a file, so a folder was not created.");
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["mimeType"] = FolderMimeType,
            ["parents"] = new[] { parentId }
        }, JsonOptions);
        using var request = await AuthorizedAsync(HttpMethod.Post,
            ApiRoot + "files?fields=" + FileFields +
            "&supportsAllDrives=" + (SharedDrives ? "true" : "false"), cancellationToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var document = await SendEffectJsonAsync(request, cancellationToken);
        var created = ReadInventoryEntry(document.RootElement, PilotPath.NormalizeRelative(name));
        if (created.Kind != GoogleDriveEntryKind.Folder)
            throw new GoogleDriveEffectUncertainException(
                "Google Drive accepted the folder create but returned a non-folder object.");
        var readback = await GetFileAsync(created.FileId, cancellationToken);
        if (readback.Kind != GoogleDriveEntryKind.Folder ||
            !string.Equals(readback.FileId, created.FileId, StringComparison.Ordinal))
            throw new GoogleDriveEffectUncertainException(
                "The Google Drive folder create readback does not match its acknowledgement.");
        VerifyCorpusMembership(readback);
        return readback;
    }

    public async Task<PilotRemoteEntry> CreateDirectoryAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        var (parentId, name) = await ResolveParentAsync(relativePath, createMissing: true,
            cancellationToken);
        return (await CreateFolderAsync(parentId, name, cancellationToken)).ToRemoteEntry();
    }

    public async Task<PilotUploadResult> UploadCreateOnlyAsync(string relativePath, Stream source,
        string expectedContentHash, CancellationToken cancellationToken = default) =>
        await UploadAsync(relativePath, source, expectedContentHash, null, cancellationToken);

    public async Task<PilotUploadResult> UploadAsync(string relativePath, Stream source,
        string expectedContentHash, string? expectedRemoteRevision,
        CancellationToken cancellationToken = default)
    {
        if (!source.CanSeek) throw new IOException("The Google Drive upload source must be seekable.");
        var normalized = PilotPath.NormalizeRelative(relativePath);
        if (!string.IsNullOrEmpty(expectedRemoteRevision) && !_capabilities.AtomicConditionalWrites)
            throw new GoogleDriveBlockedException(DriveUpdateUnsupported);
        if (string.IsNullOrEmpty(expectedRemoteRevision) && await HasExistingContentSiblingAsync(normalized, cancellationToken))
            throw new GoogleDriveBlockedException(DriveUpdateUnsupported);
        var (parentId, name) = await ResolveParentAsync(normalized, createMissing: string.IsNullOrEmpty(expectedRemoteRevision), cancellationToken);
        var existing = await ListChildrenByNameAsync(parentId, name, cancellationToken);
        var contentFiles = existing.Where(item => item.Kind != GoogleDriveEntryKind.Folder).ToArray();
        GoogleDriveInventoryEntry? target = null;
        if (contentFiles.Length > 1)
            throw new GoogleDriveBlockedException(
                "Google Drive has more than one item named '" + name + "' in this folder. The upload was " +
                "refused so no existing content is overwritten.");
        if (contentFiles.Length == 1) target = contentFiles[0];
        if (target is not null && target.Kind == GoogleDriveEntryKind.NativeDocument)
            throw new GoogleDriveBlockedException(
                "A Google-native document already uses this name. Syncing over it is refused; export is a " +
                "separate explicit action.");
        if (target is not null && target.Kind == GoogleDriveEntryKind.Shortcut)
            throw new GoogleDriveBlockedException(
                "A Google Drive shortcut already uses this name. Shortcuts are not overwritten.");
        if (string.IsNullOrEmpty(expectedRemoteRevision))
        {
            if (target is not null)
                throw new GoogleDriveBlockedException(
                    "Google Drive already contains '" + normalized + "' (" + target.FileId +
                    "). Create-only upload refused instead of replacing it.");
        }
        else
        {
            if (target is null)
                throw new GoogleDriveBlockedException(
                    "The reviewed Google Drive file is no longer present, so the update was refused.");
            if (!string.Equals(target.Revision, expectedRemoteRevision, StringComparison.Ordinal))
                throw new GoogleDriveBlockedException(
                    "The Google Drive file changed after review (expected version " +
                    expectedRemoteRevision + ", found " + (target.Version ?? "unknown") +
                    "). Atomic conditional writes are not documented, so the update was refused.");
        }
        VerifyCorpusMembership(target ?? new GoogleDriveInventoryEntry(normalized,
            GoogleDriveEntryKind.Blob, _rootFolderId, name, 0, null, null, null, null, _sharedDriveId, [],
            false, null, null, "application/octet-stream", true));
        source.Position = 0;
        // Fail closed before any write when the reviewed content identity cannot be tied to these bytes.
        var sourceSha256 = await ComputeSourceDigestAsync(source, cancellationToken);
        if (!string.Equals(expectedContentHash, sourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new GoogleDriveBlockedException(ProviderDigestIntegrationGap);
        source.Position = 0;
        if (target is not null)
            return await UploadConditionalAsync(target, source, expectedContentHash, cancellationToken);
        var result = source.Length <= _resumableThreshold
            ? await UploadSingleRequestAsync(normalized, parentId, name, target, source,
                expectedContentHash, cancellationToken)
            : await UploadResumableCoreAsync(parentId, name, target, source, expectedContentHash, null,
                static (_, _) => Task.CompletedTask, cancellationToken);
        if (!string.Equals(result.ContentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
            throw new GoogleDriveEffectUncertainException(
                "The uploaded Google Drive content digest does not match the intended content.");
        return result;
    }

    private async Task<PilotUploadResult> UploadSingleRequestAsync(string normalizedPath, string parentId,
        string name, GoogleDriveInventoryEntry? target, Stream source, string expectedContentHash,
        CancellationToken cancellationToken)
    {
        var url = target is null
            ? UploadRoot + "?uploadType=multipart&fields=" + FileFields +
              "&supportsAllDrives=" + (SharedDrives ? "true" : "false")
            : UploadRoot + "/" + Uri.EscapeDataString(target.FileId) + "?uploadType=media&fields=" +
              FileFields + "&supportsAllDrives=" + (SharedDrives ? "true" : "false");
        using var request = await AuthorizedAsync(target is null ? HttpMethod.Post : HttpMethod.Patch, url,
            cancellationToken);
        if (target is null)
        {
            var metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["parents"] = new[] { parentId }
            }, JsonOptions);
            var boundary = "syncshark-" + Guid.NewGuid().ToString("N");
            var body = new MemoryStream();
            var header = "--" + boundary + Environment.NewLine +
                         "Content-Type: application/json; charset=UTF-8" + Environment.NewLine +
                         Environment.NewLine + metadata + Environment.NewLine +
                         "--" + boundary + Environment.NewLine +
                         "Content-Type: application/octet-stream" + Environment.NewLine +
                         Environment.NewLine;
            var headerBytes = Encoding.UTF8.GetBytes(header);
            await body.WriteAsync(headerBytes, cancellationToken);
            await CopyThrottledAsync(source, body, cancellationToken);
            var trailer = Encoding.UTF8.GetBytes(Environment.NewLine + "--" + boundary + "--");
            await body.WriteAsync(trailer, cancellationToken);
            body.Position = 0;
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("multipart/related");
            content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", boundary));
            request.Content = content;
        }
        else
        {
            request.Content = new StreamContent(new ThrottledLeaveOpenReadStream(source, _transferLimiter));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        using var document = await SendEffectJsonAsync(request, cancellationToken);
        var created = ReadInventoryEntry(document.RootElement, normalizedPath);
        if (target is not null && !string.Equals(created.FileId, target.FileId, StringComparison.Ordinal))
            throw new GoogleDriveEffectUncertainException(
                "Google Drive accepted the update but returned a different object identity.");
        return await VerifyUploadedContentAsync(created, expectedContentHash, source.Length, target,
            cancellationToken);
    }

    private async Task<PilotUploadResult> VerifyUploadedContentAsync(GoogleDriveInventoryEntry declared,
        string expectedContentHash, long expectedSize, GoogleDriveInventoryEntry? target,
        CancellationToken cancellationToken)
    {
        var readback = await GetFileAsync(declared.FileId, cancellationToken);
        if (readback.Size != expectedSize)
            throw new GoogleDriveEffectUncertainException(
                "Google Drive reported a different size than the content that was sent (" +
                readback.Size.ToString(CultureInfo.InvariantCulture) + " versus " +
                expectedSize.ToString(CultureInfo.InvariantCulture) + ").");
        var digest = readback.Sha256 ?? readback.Md5;
        if (digest is null)
            throw new GoogleDriveBlockedException(
                "Google Drive returned no checksum for the uploaded content, so the write cannot be " +
                "confirmed as the intended bytes.");
        if (!string.Equals(digest, expectedContentHash, StringComparison.OrdinalIgnoreCase))
            throw new GoogleDriveEffectUncertainException(
                "Google Drive reported a checksum that differs from the intended content.");
        if (target is not null && string.Equals(readback.Version, target.Version, StringComparison.Ordinal))
            throw new GoogleDriveEffectUncertainException(
                "The Google Drive version did not change after the reviewed update was sent.");
        return new PilotUploadResult(readback.FileId, readback.Revision, readback.Size, digest);
    }

    public async Task<PilotUploadResult> UploadResumableAsync(string relativePath, Stream source,
        string expectedContentHash, string? expectedRemoteRevision,
        SyncUploadSessionCheckpoint? checkpoint,
        Func<SyncUploadSessionCheckpoint, CancellationToken, Task> persistCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistCheckpoint);
        // A restored session is external input, not an authorization destination.
        // Reject it before account/root queries can obtain a bearer credential.
        if (checkpoint is not null) RequireResumableSessionUri(checkpoint.SessionId);
        if (!source.CanSeek) throw new IOException("The Google Drive upload source must be seekable.");
        var normalized = PilotPath.NormalizeRelative(relativePath);
        if (!string.IsNullOrEmpty(expectedRemoteRevision) && _capabilities.AtomicConditionalWrites)
        {
            if (checkpoint is not null)
                throw new GoogleDriveBlockedException("A retained create session cannot be used to update an existing file.");
            // A resumable initiation guard alone cannot prove protection at final commit.
            // Existing-file updates use one guarded streamed request, with no blind retry.
            return await UploadAsync(relativePath, source, expectedContentHash, expectedRemoteRevision, cancellationToken);
        }
        if (!string.IsNullOrEmpty(expectedRemoteRevision))
            throw new GoogleDriveBlockedException(DriveUpdateUnsupported);
        if (checkpoint is not null)
        {
            // A completed create may now occupy its own name. Query this exact
            // retained session first; never mistake its successful write for an
            // unrelated create collision or start another upload.
            var retainedTarget = await ResolveParentAsync(normalized, createMissing: false, cancellationToken);
            source.Position = 0;
            return await UploadResumableCoreAsync(retainedTarget.ParentId, retainedTarget.Name, null,
                source, expectedContentHash, checkpoint, persistCheckpoint, cancellationToken);
        }
        if (await HasExistingContentSiblingAsync(normalized, cancellationToken))
            throw new GoogleDriveBlockedException(DriveUpdateUnsupported);
        var (parentId, name) = await ResolveParentAsync(normalized, createMissing: true, cancellationToken);
        var existing = await ListChildrenByNameAsync(parentId, name, cancellationToken);
        var contentFiles = existing.Where(item => item.Kind != GoogleDriveEntryKind.Folder).ToArray();
        if (contentFiles.Length > 1)
            throw new GoogleDriveBlockedException(
                "Google Drive has more than one item named '" + name + "' in this folder.");
        var target = contentFiles.SingleOrDefault();
        if (string.IsNullOrEmpty(expectedRemoteRevision))
        {
            if (target is not null)
                throw new GoogleDriveBlockedException(
                    "Google Drive already contains '" + normalized + "'. Create-only upload refused.");
        }
        else if (target is null || !string.Equals(target.Version, expectedRemoteRevision, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The Google Drive file changed after review. The resumable update was refused because " +
                "Google Drive does not document an atomic conditional write.");
        // This explicit entry point must produce a real checkpoint even when an
        // admitted target policy chooses a session below the automatic threshold.
        // UploadAsync still selects ordinary small creates; guarded updates above
        // deliberately use their distinct non-session policy.
        source.Position = 0;
        return await UploadResumableCoreAsync(parentId, name, target, source, expectedContentHash, checkpoint,
            persistCheckpoint, cancellationToken);
    }

    private async Task<PilotUploadResult> UploadResumableCoreAsync(string parentId, string name,
        GoogleDriveInventoryEntry? target, Stream source, string expectedContentHash,
        SyncUploadSessionCheckpoint? checkpoint,
        Func<SyncUploadSessionCheckpoint, CancellationToken, Task> persistCheckpoint,
        CancellationToken cancellationToken)
    {
        var session = checkpoint?.SessionId;
        var offset = checkpoint?.ConfirmedOffset ?? 0;
        if (offset < 0 || offset > source.Length)
            throw new InvalidDataException("The retained Google Drive upload checkpoint is outside the source.");
        if (session is null)
        {
            var metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["parents"] = target is null ? new[] { parentId } : null
            }, JsonOptions);
            var startUrl = target is null
                ? UploadRoot + "?uploadType=resumable&fields=" + FileFields
                : ApiRoot + "files/" + Uri.EscapeDataString(target.FileId) + "?uploadType=resumable&fields=" +
                  FileFields;
            using var startRequest = await AuthorizedAsync(target is null ? HttpMethod.Post : HttpMethod.Patch,
                startUrl, cancellationToken);
            startRequest.Content = new StringContent(metadata, Encoding.UTF8, "application/json");
            startRequest.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
            startRequest.Headers.TryAddWithoutValidation("X-Upload-Content-Length",
                source.Length.ToString(CultureInfo.InvariantCulture));
            HttpResponseMessage response;
            Interlocked.Increment(ref _providerCallCount);
            try
            {
                response = await SendResourceRequestAsync(startRequest, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                throw new GoogleDriveEffectUncertainException(
                    "The Google Drive resumable session start was interrupted after it was sent.", exception);
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    ThrowIfFailed(response);
                    throw new GoogleDriveEffectUncertainException(
                        "Google Drive did not accept the resumable session start.");
                }
                session = response.Headers.Location?.ToString()
                          ?? throw new GoogleDriveEffectUncertainException(
                              "Google Drive did not return a resumable session URI.");
                RequireResumableSessionUri(session);
            }
            offset = 0;
            await persistCheckpoint(new SyncUploadSessionCheckpoint(session, 0), cancellationToken);
        }
        else
        {
            var status = await QueryResumableOffsetAsync(session, source.Length, name, cancellationToken);
            offset = status.Offset;
            if (status.Completed is not null)
            {
                var completed = await VerifyResumableCompletionAsync(status.Completed, parentId, name,
                    expectedContentHash, source.Length, target, cancellationToken);
                await persistCheckpoint(new SyncUploadSessionCheckpoint(session, source.Length), cancellationToken);
                return completed;
            }
            // An incomplete create session must not append toward a name now
            // occupied by another object. Complete sessions are handled above.
            if (target is null && (await ListChildrenByNameAsync(parentId, name, cancellationToken)).Count > 0)
                throw new GoogleDriveBlockedException(
                    "The retained create session is incomplete and its destination name is occupied. Verify the destination before retrying; no content was sent.");
            await persistCheckpoint(new SyncUploadSessionCheckpoint(session, offset), cancellationToken);
        }
        // The retained or newly created session is now authoritative. Every later acknowledgement must
        // advance this session, and starting a different session consumes one bounded restart.
        var uploadProgress = new SyncUploadProgressBudget();
        try
        {
            uploadProgress.BeginSession(session, offset);
        }
        catch (IOException error)
        {
            throw new GoogleDriveEffectUncertainException(
                "The Google Drive upload exceeded its bounded allowance of new resumable sessions. " +
                "Reconcile the retained session before retrying.", error);
        }
        var buffer = new byte[_resumableChunkSize];
        while (offset < source.Length)
        {
            var count = checked((int)Math.Min(buffer.Length, source.Length - offset));
            source.Position = offset;
            var read = 0;
            while (read < count)
            {
                var value = await source.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
                if (value == 0) break;
                await _transferLimiter.ThrottleAsync(SyncTransferDirection.Upload, value, cancellationToken);
                read += value;
            }
            if (read == 0) throw new EndOfStreamException("The upload source ended before its retained length.");
            var end = offset + read - 1;
            using var chunkRequest = await AuthorizedAsync(HttpMethod.Put, session, cancellationToken);
            chunkRequest.Content = new ByteArrayContent(buffer, 0, read);
            chunkRequest.Content.Headers.ContentLength = read;
            chunkRequest.Content.Headers.TryAddWithoutValidation("Content-Range",
                "bytes " + offset.ToString(CultureInfo.InvariantCulture) + "-" +
                end.ToString(CultureInfo.InvariantCulture) + "/" +
                source.Length.ToString(CultureInfo.InvariantCulture));
            Interlocked.Increment(ref _providerCallCount);
            HttpResponseMessage chunkResponse;
            try
            {
                chunkResponse = await SendResourceRequestAsync(chunkRequest,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                throw new GoogleDriveEffectUncertainException(
                    "A Google Drive resumable chunk response was interrupted after transmission. Reconcile " +
                    "the server offset before retrying.", exception);
            }
            using (chunkResponse)
            {
                if ((int)chunkResponse.StatusCode == 308)
                {
                    // Never assume the server accepted every byte that was sent.
                    // The server's own acknowledgement is authoritative, including a short or
                    // regressing offset, so the next chunk starts exactly where the server says it is.
                    offset = ReadResumableRangeHeader(chunkResponse, source.Length);
                    await persistCheckpoint(new SyncUploadSessionCheckpoint(session, offset), cancellationToken);
                    ObserveUploadProgress(uploadProgress, offset);
                    continue;
                }
                if (chunkResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    throw new GoogleDriveEffectUncertainException(
                        "The Google Drive resumable session is no longer usable. Start a new session and " +
                        "reconcile the destination before retrying.");
                if (!chunkResponse.IsSuccessStatusCode)
                {
                    ThrowIfFailed(chunkResponse);
                    throw new GoogleDriveEffectUncertainException(
                        "Google Drive did not confirm the resumable chunk.");
                }
                using var document = await ReadBoundedJsonAsync(chunkResponse, cancellationToken);
                var created = ReadInventoryEntry(document.RootElement, name);
                var completed = await VerifyResumableCompletionAsync(created, parentId, name,
                    expectedContentHash, source.Length, target, cancellationToken);
                await persistCheckpoint(new SyncUploadSessionCheckpoint(session, source.Length), cancellationToken);
                return completed;
            }
        }
        throw new GoogleDriveEffectUncertainException(
            "The Google Drive resumable upload reached the end of the source without a completion response.");
    }

    private async Task<(long Offset, GoogleDriveInventoryEntry? Completed)> QueryResumableOffsetAsync(
        string session, long totalLength, string name,
        CancellationToken cancellationToken)
    {
        RequireResumableSessionUri(session);
        using var request = await AuthorizedAsync(HttpMethod.Put, session, cancellationToken);
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentLength = 0;
        request.Content.Headers.TryAddWithoutValidation("Content-Range",
            "bytes */" + totalLength.ToString(CultureInfo.InvariantCulture));
        Interlocked.Increment(ref _providerCallCount);
        using var response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if ((int)response.StatusCode == 308)
            return (ReadResumableRangeHeader(response, totalLength), null);
        if (response.IsSuccessStatusCode)
        {
            using var document = await ReadBoundedJsonAsync(response, cancellationToken);
            return (totalLength, ReadInventoryEntry(document.RootElement, name));
        }
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new GoogleDriveEffectUncertainException(
                "The retained Google Drive resumable session is no longer usable; the upload must restart.");
        ThrowIfFailed(response);
        throw new GoogleDriveEffectUncertainException(
            "Google Drive did not confirm the retained resumable session's acknowledged offset.");
    }

    private async Task<PilotUploadResult> VerifyResumableCompletionAsync(GoogleDriveInventoryEntry declared,
        string parentId, string name, string expectedHash, long expectedSize,
        GoogleDriveInventoryEntry? target, CancellationToken cancellationToken)
    {
        if (!declared.IsByteFile || declared.Trashed || declared.Name != name ||
            !declared.Parents.Contains(parentId, StringComparer.Ordinal))
            throw new GoogleDriveEffectUncertainException(
                "The completed session returned an object outside the intended file and folder. Verify before retry; no upload was replayed.");
        var readback = await GetFileAsync(declared.FileId, cancellationToken);
        var siblings = await ListChildrenByNameAsync(parentId, name, cancellationToken);
        if (!readback.IsByteFile || readback.Trashed || readback.Name != name ||
            !readback.Parents.Contains(parentId, StringComparer.Ordinal) ||
            siblings.Count != 1 || siblings[0].FileId != declared.FileId)
            throw new GoogleDriveEffectUncertainException(
                "The completed session's destination identity or folder membership changed. Verify before retry; no upload was replayed.");
        return await VerifyUploadedContentAsync(readback, expectedHash, expectedSize, target, cancellationToken);
    }

    private static void RequireResumableSessionUri(string session)
    {
        // Drive v3's documented Location is the Google HTTPS /upload/drive/v3/files
        // endpoint (with the opaque upload_id query). Never use suffix matching,
        // echo the session/query, or apply this Google rule to Dropbox's opaque IDs.
        const string path = "/upload/drive/v3/files";
        if (!Uri.TryCreate(session, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.IdnHost, "www.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.AbsolutePath == path || uri.AbsolutePath.StartsWith(path + "/", StringComparison.Ordinal)))
            throw new GoogleDriveBlockedException(
                "The Google Drive upload session does not match the supported HTTPS endpoint. " +
                "No session content or status request was authorized or sent.");
    }

    // Google's 308 acknowledgement is the only authoritative progress signal for a resumable
    // session. Drive omits the header when it has received nothing, so an absent header means offset
    // zero; anything else must be exactly one ASCII "bytes=0-<last>" range whose successor still fits
    // the source. A refusal never echoes the header value and never mentions the session URI.
    private static long ReadResumableRangeHeader(HttpResponseMessage response, long totalLength)
    {
        static GoogleDriveEffectUncertainException Reject(string detail) =>
            new("Google Drive returned an upload range acknowledgement that " + detail +
                ", so the acknowledged offset is not proven. Reconcile the retained session before retrying.");
        var ranges = response.Headers.TryGetValues("Range", out var values)
            ? values.ToArray()
            : Array.Empty<string>();
        if (ranges.Length == 0) return 0;
        if (ranges.Length != 1) throw Reject("was sent more than once");
        var header = ranges[0];
        if (string.IsNullOrWhiteSpace(header)) throw Reject("is empty");
        const string unit = "bytes=";
        if (!header.StartsWith(unit, StringComparison.Ordinal)) throw Reject("does not use the bytes unit");
        var range = header[unit.Length..];
        var separator = range.IndexOf('-');
        if (separator != 1 || range[0] != '0' || range.Contains(',') ||
            range.IndexOf('-', separator + 1) >= 0)
            throw Reject("is not a single range that starts at zero");
        if (!long.TryParse(range[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture,
                out var last))
            throw Reject("is malformed");
        // The acknowledged last byte must leave room for its successor inside the source; comparing
        // the last byte index this way cannot overflow.
        if (last >= totalLength) throw Reject("is outside the uploaded content");
        return last + 1;
    }

    // The progress budget is a local guard, so its refusal is reported as the same provider-specific
    // uncertain outcome as any other unconfirmed resumable state, and no further content is sent.
    private static void ObserveUploadProgress(SyncUploadProgressBudget uploadProgress, long acknowledgedOffset)
    {
        try
        {
            uploadProgress.Observe(acknowledgedOffset);
        }
        catch (IOException error)
        {
            throw new GoogleDriveEffectUncertainException(
                "The Google Drive upload session stopped acknowledging forward progress. Reconcile the " +
                "retained session before retrying; no further content was sent.", error);
        }
    }

    public async Task<SyncDownloadResult> DownloadAsync(PilotRemoteEntry expected, Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (!destination.CanWrite || !destination.CanSeek)
            throw new IOException("The Google Drive download destination must be writable and seekable.");
        var reviewed = await RequireReviewedEntryAsync(expected, requireDigest: false, cancellationToken);
        await VerifyRootIdentityAsync(cancellationToken);
        await RequireDownloadMembershipAsync(expected, reviewed, cancellationToken);
        await DownloadMediaAsync(reviewed, destination, null, cancellationToken);
        var after = await GetFileAsync(reviewed.FileId, cancellationToken);
        // Drive's object version also advances for metadata-only changes observed immediately
        // after a write. DownloadMediaAsync has already checked every byte against reviewed SHA-256.
        // Permit only an unchanged binary revision and identity when the broad version advances;
        // writes still require the exact original version and the server's conditional guard.
        var sameContentAndLocation = after.FileId == reviewed.FileId && after.Kind == GoogleDriveEntryKind.Blob &&
            after.MimeType == reviewed.MimeType && after.Name == reviewed.Name && after.DriveId == reviewed.DriveId &&
            after.Parents.ToHashSet(StringComparer.Ordinal).SetEquals(reviewed.Parents) &&
            after.Size == reviewed.Size && reviewed.Sha256 is not null && after.Sha256 == reviewed.Sha256 &&
            !after.Trashed && after.CanDownload;
        var sameVersionOrBinaryRevision = after.Version == reviewed.Version ||
            !string.IsNullOrEmpty(reviewed.HeadRevisionId) && after.HeadRevisionId == reviewed.HeadRevisionId;
        if (!sameContentAndLocation || !sameVersionOrBinaryRevision)
            throw new GoogleDriveBlockedException(
                "The Google Drive file changed or was trashed during the download, so the local copy was " +
                "not accepted.");
        await VerifyRootIdentityAsync(cancellationToken);
        await RequireDownloadMembershipAsync(expected, after, cancellationToken);
        return new SyncDownloadResult(reviewed.FileId, reviewed.Revision, reviewed.Size,
            reviewed.Sha256 ?? reviewed.Md5 ?? string.Empty);
    }

    private async Task RequireDownloadMembershipAsync(PilotRemoteEntry expected,
        GoogleDriveInventoryEntry current, CancellationToken cancellationToken)
    {
        var (parent, name) = await ResolveParentAsync(expected.RelativePath, createMissing: false, cancellationToken);
        VerifyCorpusMembership(current);
        if (current.Kind != GoogleDriveEntryKind.Blob || current.Trashed || current.Name != name ||
            current.Parents.Count != 1 || current.Parents[0] != parent || current.Size != expected.Size ||
            string.IsNullOrWhiteSpace(expected.ContentHash) ||
            !string.Equals(current.Sha256, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new GoogleDriveBlockedException("The reviewed download is no longer at its exact selected-folder path and content identity.");
    }

    public async Task<SyncDownloadResult> DownloadRecoveryAsync(SyncRemoteRecoveryRootBinding binding,
        SyncArchiveResult expected, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!destination.CanWrite || !destination.CanSeek)
            throw new IOException("The Recovery download destination must be writable and seekable.");
        var name = NormalizeRetentionName(expected.RecoveryPath);
        var before = await VerifyArchiveAsync();
        await DownloadMediaAsync(before, destination, null, cancellationToken);
        await VerifyArchiveAsync();
        return new SyncDownloadResult(expected.FileId, expected.Revision, expected.Size, expected.ContentHash);

        async Task<GoogleDriveInventoryEntry> VerifyArchiveAsync()
        {
            await VerifyRecoveryRootAsync(binding, cancellationToken);
            await VerifyAccountAsync(cancellationToken);
            var member = await GetFileAsync(expected.FileId, cancellationToken);
            VerifyCorpusMembership(member);
            if (member.Kind != GoogleDriveEntryKind.Blob || member.Trashed || !member.CanDownload ||
                member.FileId != expected.FileId || member.Revision != expected.Revision || member.Name != name ||
                member.Parents.Count != 1 || member.Parents[0] != binding.RootFolderId ||
                member.Size != expected.Size || string.IsNullOrWhiteSpace(member.Sha256) ||
                !string.Equals(member.Sha256, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The exact retained Google Drive recovery copy changed or is unavailable.");
            return member;
        }
    }

    private async Task DownloadMediaAsync(GoogleDriveInventoryEntry reviewed, Stream destination,
        (long Offset, long Length)? range, CancellationToken cancellationToken)
    {
        if (!reviewed.CanDownload)
            throw new GoogleDriveBlockedException(
                "Google Drive reports that this file cannot be downloaded by this account.");
        var url = ApiRoot + "files/" + Uri.EscapeDataString(reviewed.FileId) + "?alt=media&supportsAllDrives=" +
                  (SharedDrives ? "true" : "false");
        if (range is { } window)
            url += "&acknowledgeAbuse=false";
        using var request = await AuthorizedAsync(HttpMethod.Get, url, cancellationToken);
        if (range is { } requested)
            request.Headers.Range = new RangeHeaderValue(requested.Offset, requested.Offset + requested.Length - 1);
        Interlocked.Increment(ref _providerCallCount);
        using var response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (range is not null && response.StatusCode != HttpStatusCode.PartialContent)
            throw new GoogleDriveBlockedException(
                "Google Drive did not honour the requested byte range for this file.");
        ThrowIfFailed(response);
        destination.Position = 0;
        if (range is null) destination.SetLength(0);
        using var hashing = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long written = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (range is null && written + read > reviewed.Size)
                    throw new IOException("The Google Drive download exceeded its reviewed length.");
                await _transferLimiter.ThrottleAsync(SyncTransferDirection.Download, read, cancellationToken);
                hashing.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
            }
        }
        if (range is null)
        {
            if (written != reviewed.Size)
                throw new IOException("The Google Drive download length differs from the reviewed size.");
            var observed = ToHexLower(hashing.GetHashAndReset());
            if (reviewed.Sha256 is null)
                throw new GoogleDriveBlockedException(
                    "Google Drive declared no SHA-256 for this binary file, so the downloaded bytes could " +
                    "not be verified against a provider checksum.");
            if (!HexDigestMatches(reviewed.Sha256, observed))
                throw new IOException("The Google Drive download bytes differ from the declared SHA-256.");
            destination.Position = 0;
        }
    }

    private readonly SyncVerifiedRangeDownload _verifiedRangeDownloads = new();

    internal string? VerifiedRangeStagingRootForTest
    {
        get => _verifiedRangeDownloads.StagingRootForTest;
        set => _verifiedRangeDownloads.StagingRootForTest = value;
    }

    public Task<SyncRangeDownloadResult> DownloadRangeAsync(PilotRemoteEntry expected, long offset,
        long length, Stream destination, CancellationToken cancellationToken = default)
    {
        // Media ranges are not revision-pinned by a documented conditional contract. The real full
        // download verifies the provider digest and its post-read metadata before any native delivery.
        return _verifiedRangeDownloads.DownloadAsync(expected, offset, length, destination,
            DownloadAsync, cancellationToken);
    }

    public async Task<PilotRemoteEntry> MoveAsync(PilotRemoteEntry expected, string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        var reviewed = await RequireReviewedEntryAsync(expected, requireDigest: false, cancellationToken);
        if (!_capabilities.AtomicConditionalWrites)
            throw new GoogleDriveBlockedException(DriveDestructiveUnsupported);
        var destination = PilotPath.NormalizeRelative(destinationRelativePath);
        if (string.Equals(destination, expected.RelativePath, StringComparison.Ordinal))
            throw new InvalidDataException("The Google Drive move destination must differ from the source.");
        if (reviewed.Kind == GoogleDriveEntryKind.Folder && destination.StartsWith(
                expected.RelativePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A folder cannot be moved inside itself.");
        var (parentId, name) = await ResolveParentAsync(destination, createMissing: true, cancellationToken);
        var occupied = await ListChildrenByNameAsync(parentId, name, cancellationToken);
        if (occupied.Any(item => !string.Equals(item.FileId, reviewed.FileId, StringComparison.Ordinal)))
            throw new GoogleDriveBlockedException(
                "The Google Drive destination '" + destination + "' is already occupied.");
        var readback = await PatchConditionalAsync(reviewed, new { title = name }, parentId, cancellationToken);
        if (readback.Trashed || readback.Name != name || readback.Parents.Count != 1 ||
            !readback.Parents.Contains(parentId, StringComparer.Ordinal))
            throw new GoogleDriveEffectUncertainException(
                "The Google Drive move readback does not show the intended name and parent.");
        return (readback with { RelativePath = destination }).ToRemoteEntry();
    }

    // Trash is the default and the only destructive effect this lane implements. Permanent deletion is
    // deliberately unavailable so a transient inventory or permission failure can never destroy content.
    public async Task DeleteAsync(PilotRemoteEntry expected, CancellationToken cancellationToken = default)
    {
        var reviewed = await RequireReviewedEntryAsync(expected, requireDigest: false, cancellationToken);
        if (reviewed.Trashed) return;
        if (!_capabilities.AtomicConditionalWrites)
            throw new GoogleDriveBlockedException(DriveDestructiveUnsupported);
        // A folder ETag does not establish an atomic inventory of its descendants.
        // Refuse subtree trash rather than hiding an unreviewed concurrent child.
        if (reviewed.Kind == GoogleDriveEntryKind.Folder)
            throw new GoogleDriveBlockedException("Google folder deletion is not enabled: newly added children cannot be protected by the folder's version guard. No folder was trashed.");
        var trashed = await PatchConditionalAsync(reviewed, new { labels = new { trashed = true } }, null,
            cancellationToken);
        if (!trashed.Trashed)
            throw new GoogleDriveEffectUncertainException(
                "Google Drive acknowledged the trash request but did not report the item as trashed.");
    }

    public Task DeletePermanentlyAsync(PilotRemoteEntry expected, CancellationToken cancellationToken = default) =>
        throw new GoogleDriveBlockedException(
            "Permanent deletion is not implemented for Google Drive in this build. Items are moved to the " +
            "Drive trash, where the owner can recover them for 30 days.");
}

// Recovery, revision retention and control-namespace surface. Server-side copy is used where Drive
// supports it; a historical revision has no server-side copy, so it must be read and re-uploaded.
public sealed partial class GoogleDrivePilotClient
{
    public const long MaximumRevisionRetentionBytes = 256L * 1024 * 1024;

    public async Task VerifyRecoveryRootAsync(SyncRemoteRecoveryRootBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        RequireRecoveryRootNamespace(binding.RootNamespaceId);
        var folder = await GetFileAsync(binding.RootFolderId, cancellationToken);
        if (folder.FileId != binding.RootFolderId || folder.Kind != GoogleDriveEntryKind.Folder || folder.Trashed)
            throw new IOException(
                "The dedicated Google Drive recovery folder is missing, trashed, or no longer a folder.");
        VerifyCorpusMembership(folder);
    }

    /// <summary>
    /// The recovery binding's namespace has to identify this connection's own bound account, and it is refused
    /// before any provider request when it does not. Two persisted spellings name that one account and are
    /// therefore both accepted: the provider-native permission id, which every retained endpoint, root and
    /// recovery identity carries for Google Drive, and the legacy derived account key persisted by recovery
    /// rows written before that native namespace was retained. The derived spelling is only honoured while
    /// this connection's own key really is the derivation of its own permission id, so a blank namespace,
    /// another account's native or derived namespace, or an inconsistent connection derivation can never
    /// satisfy the check. The stable folder id, kind, trash state and corpus membership are verified
    /// afterwards, unchanged.
    /// </summary>
    private void RequireRecoveryRootNamespace(string? rootNamespaceId)
    {
        var consistentDerivation = string.Equals(_connection.AccountKey,
            GoogleDriveConnectionBinding.CreateAccountKey(_connection.PermissionId), StringComparison.Ordinal);
        var ownAccount = string.Equals(rootNamespaceId, _connection.PermissionId, StringComparison.Ordinal) ||
                         string.Equals(rootNamespaceId, _connection.AccountKey, StringComparison.Ordinal);
        if (!consistentDerivation || !ownAccount)
            throw new InvalidOperationException(
                "The Google Drive recovery folder is bound to a different account.");
    }

    public async Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected,
        SyncRemoteRecoveryRootBinding binding, string deterministicName,
        CancellationToken cancellationToken = default)
    {
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        var name = NormalizeRetentionName(deterministicName);
        return await CopyServerSideAsync(expected, binding.RootFolderId, name,
            name, cancellationToken);
    }

    public async Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected, string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        var normalized = PilotPath.NormalizeRelative(recoveryPath);
        var (parentId, name) = await ResolveParentAsync(normalized, createMissing: true, cancellationToken);
        return await CopyServerSideAsync(expected, parentId, name, normalized, cancellationToken);
    }

    private async Task<SyncArchiveResult> CopyServerSideAsync(PilotRemoteEntry expected, string parentId,
        string name, string recoveryLabel, CancellationToken cancellationToken)
    {
        var reviewed = await RequireReviewedEntryAsync(expected, requireDigest: false, cancellationToken);
        if (reviewed.Kind != GoogleDriveEntryKind.Blob)
            throw new GoogleDriveBlockedException(
                "Only binary Google Drive files can be retained as a byte copy. Google-native documents " +
                "have no byte content; export is a separate explicit action.");
        if (reviewed.Size != expected.Size || string.IsNullOrWhiteSpace(expected.ContentHash) ||
            !string.Equals(reviewed.Sha256, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new GoogleDriveBlockedException("The recovery source no longer matches the reviewed size and SHA-256 checksum.");
        var existing = await ListChildrenByNameAsync(parentId, name, cancellationToken);
        if (existing.Count > 0)
            throw new GoogleDriveBlockedException(
                "The Google Drive recovery destination already contains '" + name +
                "'. Nothing was overwritten.");
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["parents"] = new[] { parentId }
        }, JsonOptions);
        using var request = await AuthorizedAsync(HttpMethod.Post,
            ApiRoot + "files/" + Uri.EscapeDataString(reviewed.FileId) + "/copy?fields=" + FileFields +
            "&supportsAllDrives=" + (SharedDrives ? "true" : "false"), cancellationToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var document = await SendEffectJsonAsync(request, cancellationToken);
        var copied = ReadInventoryEntry(document.RootElement, PilotPath.NormalizeRelative(name));
        var readback = await GetFileAsync(copied.FileId, cancellationToken);
        VerifyCorpusMembership(readback);
        if (readback.FileId != copied.FileId || readback.Kind != GoogleDriveEntryKind.Blob || readback.Trashed || readback.Size != reviewed.Size ||
            !readback.Parents.Contains(parentId, StringComparer.Ordinal) ||
            !string.Equals(readback.Name, name, StringComparison.Ordinal))
            throw new GoogleDriveEffectUncertainException(
                "The Google Drive recovery copy does not match the reviewed source size.");
        if (reviewed.Sha256 is not null &&
            !string.Equals(readback.Sha256, reviewed.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new GoogleDriveEffectUncertainException(
                "The Google Drive recovery copy checksum differs from the reviewed source.");
        return new SyncArchiveResult(readback.FileId, readback.Revision, recoveryLabel,
            readback.Size, readback.Sha256!);
    }

    public async Task<SyncArchiveResult?> GetRecoveryAsync(SyncRemoteRecoveryRootBinding binding,
        string deterministicName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        var name = NormalizeRetentionName(deterministicName);
        return await FindRecoveryAsync(binding.RootFolderId, name, cancellationToken);
    }

    public async Task<SyncArchiveResult?> GetRecoveryAsync(string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        var normalized = PilotPath.NormalizeRelative(recoveryPath);
        var (parentId, name) = await ResolveParentAsync(normalized, createMissing: false, cancellationToken);
        var result = await FindRecoveryAsync(parentId, name, cancellationToken);
        return result is null ? null : result with { RecoveryPath = normalized };
    }

    private async Task<SyncArchiveResult?> FindRecoveryAsync(string parentId, string name,
        CancellationToken cancellationToken)
    {
        var matches = await ListChildrenByNameAsync(parentId, name, cancellationToken);
        if (matches.Count == 0) return null;
        if (matches.Count > 1)
            throw new GoogleDriveBlockedException(
                "More than one Google Drive recovery copy uses the name '" + name + "'.");
        var match = matches[0];
        VerifyCorpusMembership(match);
        if (match.Kind != GoogleDriveEntryKind.Blob || match.Trashed || match.Name != name ||
            !match.Parents.Contains(parentId, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(match.Sha256))
            throw new GoogleDriveBlockedException(
                "The Google Drive recovery copy is not a binary file.");
        return new SyncArchiveResult(match.FileId, match.Revision, name, match.Size,
            match.Sha256);
    }

    public async Task<SyncArchiveResult> CopyRevisionToRecoveryAsync(PilotRemoteEntry expected,
        SyncRemoteRecoveryRootBinding binding, string deterministicName,
        CancellationToken cancellationToken = default)
    {
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        var reviewed = await RequireReviewedEntryAsync(expected, requireDigest: true, cancellationToken);
        if (reviewed.Kind != GoogleDriveEntryKind.Blob)
            throw new GoogleDriveBlockedException(
                "Google Drive cannot retain a historical byte copy of a native document.");
        if (string.IsNullOrWhiteSpace(reviewed.HeadRevisionId))
            throw new GoogleDriveBlockedException(
                "Google Drive reported no head revision for this file, so a historical revision cannot be " +
                "resolved. Revisions are only addressable for binary files.");
        if (reviewed.Size > MaximumRevisionRetentionBytes)
            throw new GoogleDriveBlockedException(
                "The historical revision exceeds the bounded retention size of " +
                (MaximumRevisionRetentionBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) +
                " MiB for this build.");
        var name = NormalizeRetentionName(deterministicName);
        var revision = (await ListRevisionsAsync(expected.RelativePath, cancellationToken))
            .SingleOrDefault(row => row.RevisionId == reviewed.HeadRevisionId && row.Size == expected.Size)
            ?? throw new GoogleDriveBlockedException("The reviewed file's exact head revision is unavailable. No recovery copy was written.");
        return await WithRevisionBytesAsync(revision, async (bytes, sha256) =>
        {
            if (!string.Equals(sha256, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The retained revision does not match the reviewed file content.");
            await VerifyRecoveryRootAsync(binding, cancellationToken);
            if ((await ListChildrenByNameAsync(binding.RootFolderId, name, cancellationToken)).Count != 0)
                throw new GoogleDriveBlockedException("The recovery destination already contains the selected name.");
            var upload = await UploadSingleRequestAsync(name, binding.RootFolderId, name, null, bytes,
                sha256, cancellationToken);
            if (upload.Size != expected.Size || !string.Equals(upload.ContentHash, sha256, StringComparison.OrdinalIgnoreCase))
                throw new GoogleDriveEffectUncertainException("The historical recovery copy failed exact readback.");
            return new SyncArchiveResult(upload.FileId, upload.Revision, name, upload.Size, upload.ContentHash);
        }, cancellationToken);
    }

    public async Task VerifyControlNamespaceAsync(SyncRemoteControlNamespaceBinding binding,
        string installationId, string namespaceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceId);
        var root = await GetFileAsync(binding.RootFolderId, cancellationToken);
        if (root.Kind != GoogleDriveEntryKind.Folder || root.Trashed)
            throw new IOException("The Google Drive control folder moved, changed identity, or is trashed.");
        var markerName = Path.GetFileName(binding.MarkerPath);
        var matches = await ListChildrenByNameAsync(binding.RootFolderId, markerName, cancellationToken);
        var marker = matches.SingleOrDefault(item => string.Equals(item.FileId, binding.MarkerFileId,
            StringComparison.Ordinal));
        if (marker is null)
            throw new IOException("The Google Drive ownership record is missing from its folder.");
        if (!string.Equals(marker.Revision, binding.MarkerRevision, StringComparison.Ordinal) ||
            !string.Equals(marker.Sha256 ?? marker.Md5, binding.MarkerContentHash,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Google Drive ownership record changed identity or content.");
        using var bytes = new MemoryStream();
        await DownloadMediaAsync(marker, bytes, null, cancellationToken);
        bytes.Position = 0;
        using var document = await JsonDocument.ParseAsync(bytes, cancellationToken: cancellationToken);
        var recordedInstallation = document.RootElement.TryGetProperty("installationId", out var installElement)
            ? installElement.GetString()
            : null;
        var recordedNamespace = document.RootElement.TryGetProperty("namespaceId", out var namespaceElement)
            ? namespaceElement.GetString()
            : null;
        if (!string.Equals(recordedInstallation, installationId, StringComparison.Ordinal) ||
            !string.Equals(recordedNamespace, namespaceId, StringComparison.Ordinal))
            throw new IOException("The Google Drive ownership record belongs to a different installation.");
    }

    public async Task<PilotUploadResult> CopyCreateOnlyAsync(PilotRemoteEntry expected,
        string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        var normalized = PilotPath.NormalizeRelative(destinationRelativePath);
        var (parentId, name) = await ResolveParentAsync(normalized, createMissing: true, cancellationToken);
        var archive = await CopyServerSideAsync(expected, parentId, name, normalized, cancellationToken);
        return new PilotUploadResult(archive.FileId, archive.Revision, archive.Size, archive.ContentHash);
    }

    public async Task DeleteRecoveryAsync(SyncRemoteRecoveryRootBinding binding, SyncArchiveResult expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(expected);
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        var current = await GetFileAsync(expected.FileId, cancellationToken);
        var expectedPath = NormalizeRetentionName(expected.RecoveryPath);
        if (current.Kind != GoogleDriveEntryKind.Blob || current.Trashed ||
            !string.Equals(current.Revision, expected.Revision, StringComparison.Ordinal) ||
            current.Size != expected.Size || !string.Equals(current.Sha256, expected.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            throw new GoogleDriveBlockedException(
                "The selected Google Drive recovery copy changed, so it was not removed.");
        var parentMatches = expectedPath.Length == 0
            ? []
            : await ListChildrenByNameAsync(binding.RootFolderId, Path.GetFileName(expectedPath),
                cancellationToken);
        if (expectedPath.Length > 0 && parentMatches.All(item =>
                !string.Equals(item.FileId, expected.FileId, StringComparison.Ordinal)))
            throw new GoogleDriveBlockedException(
                "The selected Google Drive recovery copy is no longer in the bound recovery folder.");
        await DeleteAsync(new PilotRemoteEntry(expectedPath, PilotEntryKind.File, expected.FileId,
            expected.Revision, expected.Size, expected.ContentHash), cancellationToken);
    }

    public async Task<PilotRemoteEntry> CopyRecoveryToPathAsync(SyncRemoteRecoveryRootBinding binding,
        SyncArchiveResult expected, string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        await VerifyRootIdentityAsync(cancellationToken);
        var current = await GetRecoveryAsync(binding, expected.RecoveryPath, cancellationToken);
        if (current is null || current.FileId != expected.FileId || current.Revision != expected.Revision ||
            current.Size != expected.Size || !string.Equals(current.ContentHash, expected.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            throw new GoogleDriveBlockedException("The selected Google Drive recovery copy changed or moved.");
        var destination = PilotPath.NormalizeRelative(destinationRelativePath);
        var (parentId, name) = await ResolveParentAsync(destination, createMissing: true, cancellationToken);
        var copy = await CopyServerSideAsync(new PilotRemoteEntry(expected.RecoveryPath, PilotEntryKind.File,
            expected.FileId, expected.Revision, expected.Size, expected.ContentHash), parentId, name,
            destination, cancellationToken);
        return new PilotRemoteEntry(destination, PilotEntryKind.File, copy.FileId, copy.Revision, copy.Size,
            copy.ContentHash);
    }

    private static string NormalizeRetentionName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 16 or > 96 ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                                   character is not '-' and not '_' and not '.'))
            throw new InvalidDataException("The deterministic retention name is not accepted.");
        return value;
    }
}
