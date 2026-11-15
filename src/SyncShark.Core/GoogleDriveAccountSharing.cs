using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// Drive sharing is an ACL on a stable file ID, not a separately revocable URL token.
// Reference: developers.google.com/workspace/drive/api/guides/manage-sharing
// No mutation is replayed. The preflight fingerprint is a stale-read guard, NOT provider CAS;
// the per-file gate serializes this client's calls only. Readback detects conflicting outcomes.
public sealed class GoogleDriveAccountSharingClient : IAccountSharingClient, IAccountSharingExactPermissionLookup
{
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private const int PageSize = 25;
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumErrorBodyBytes = 64 * 1024;
    private const string FileFields = "id,name,mimeType,version,driveId,webViewLink,resourceKey,parents,trashed,inheritedPermissionsDisabled,capabilities(canShare,canDownload)";
    private const string PermissionFields = "id,type,role,domain,allowFileDiscovery,expirationTime,view,permissionDetails(permissionType,inherited,inheritedFrom,role)";
    private readonly HttpClient _http;
    private readonly IGoogleDriveAccessTokenSource _tokens;
    private readonly GoogleDriveConnectionBinding _connection;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _namespaceGate = new(1, 1);
    private string[] _namespaces;
    private bool _namespacesDiscovered;

    public GoogleDriveAccountSharingClient(HttpClient http, IGoogleDriveAccessTokenSource tokens,
        GoogleDriveConnectionBinding connection)
    {
        _http = http; _tokens = tokens; _connection = connection.Normalize();
        GoogleDriveProviderConfiguration.ValidateConnection(_connection);
        _namespaces = [_connection.PermissionId];
    }

    public string ConnectionId => _connection.ConnectionId;
    // First namespace is the account's existing PermissionId; additional entries are exact Drive IDs.
    // Shared drives appear only after explicit SharedDrivesEnabled and successful bounded discovery.
    public IReadOnlyList<string> Namespaces => _namespaces.ToArray();

    public async Task DiscoverNamespacesAsync(CancellationToken cancellationToken = default)
    {
        if (!_connection.SharedDrivesEnabled || _namespacesDiscovered) return;
        await _namespaceGate.WaitAsync(cancellationToken);
        try
        {
            if (_namespacesDiscovered) return;
            var ids = new HashSet<string>(StringComparer.Ordinal) { _connection.PermissionId };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? next = null;
            for (var page = 0; page < 100; page++)
            {
                using var json = await CallAsync(HttpMethod.Get, "drives?" + Query(new()
                { ["pageSize"] = "100", ["fields"] = "nextPageToken,drives(id)", ["pageToken"] = next }), null, false, cancellationToken);
                foreach (var drive in Array(json.RootElement, "drives"))
                {
                    var id = Required(drive, "id"); ValidateId(id);
                    if (id == _connection.PermissionId) throw new InvalidDataException("Google returned an ambiguous corpus identity.");
                    ids.Add(id);
                }
                next = String(json.RootElement, "nextPageToken");
                if (next is null)
                {
                    _namespaces = new[] { _connection.PermissionId }.Concat(ids.Where(id => id != _connection.PermissionId).Order(StringComparer.Ordinal)).ToArray();
                    _namespacesDiscovered = true;
                    return;
                }
                CheckNext(next, seen);
            }
            throw new InvalidDataException("Google shared-drive discovery exceeded its bounded page limit; no complete corpus list is available.");
        }
        finally { _namespaceGate.Release(); }
    }

    public async Task<AccountSharingPage<AccountSharedLink>> ListLinksAsync(string namespaceId,
        AccountSharingCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        var continuation = CheckCursor(namespaceId, "links", cursor);
        // `shared` is an output field, not a supported files.list search term. Visibility is documented:
        // developers.google.com/workspace/drive/api/guides/ref-search-terms
        using var json = await ListFilesAsync(namespaceId, "trashed = false and visibility != 'limited'", continuation, cancellationToken);
        var links = new List<AccountSharedLink>(); var issues = new List<AccountSharingIssue>();
        if (Bool(json.RootElement, "incompleteSearch")) issues.Add(new(null, "Google returned an incomplete search. Refresh this corpus; the displayed links are not a complete inventory."));
        foreach (var entry in Array(json.RootElement, "files", PageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? id = null;
            var stage = "file-metadata";
            try
            {
                id = Required(entry, "id");
                var file = ReadFile(entry, namespaceId);
                stage = "permissions";
                var snapshot = await ReadSnapshotAsync(file, namespaceId, cancellationToken);
                stage = "link-projection";
                foreach (var permission in snapshot.Permissions.Where(IsLinkPermission)) links.Add(ToLink(snapshot, permission, namespaceId));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or GoogleDriveApiException or JsonException)
            {
                // Keep successful records and the provider continuation. Never disguise a forbidden
                // ACL, malformed metadata or an incomplete ACL page as "no links".
                // W2/W3: keep a bounded, privacy-safe cause. The exception type, the provider status
                // and an allowlisted validation detail are recorded; no provider text, URL, identity,
                // key, token, name or path is ever copied into the issue.
                var (code, detail) = ClassifyIssue(error);
                issues.Add(new(id, "This item's Google link permissions could not be inspected. Refresh or inspect it in Google Drive.",
                    code, stage, detail));
            }
        }
        return new(links, Next(json.RootElement, namespaceId, "links", continuation), issues, issues.Count > 0);
    }

    // W2: one stable code per failure family, from the exception type and the Google status code only.
    private static (string Code, string? DetailCode) ClassifyIssue(Exception error) => error switch
    {
        GoogleDriveApiException api => ClassifyStatus(api),
        JsonException => ("invalid-response", "malformed-json"),
        InvalidDataException invalid => ("invalid-response", ValidationDetail(invalid.Message)),
        IOException => ("connection-failed", null),
        _ => ("google-request-failed", null)
    };

    // F5: the finite provider reason decides the family, never a guess from the status alone. Google
    // answers HTTP 403 both for quota and for access, so a recognised rate/quota reason is
    // rate-limited even on 403; insufficientFilePermissions is the one reason meaning this account may
    // not read the item's sharing details; any other allowlisted 403 reason stays access-restricted
    // with its exact finite detail. An unclassified 403 is a plain Google request failure and is never
    // reported as denied access. 401, 404 and 429 keep their existing behaviour.
    private static (string Code, string? DetailCode) ClassifyStatus(GoogleDriveApiException api)
    {
        var status = api.StatusCode is { } value ? (int)value : 0;
        if (status == 403)
        {
            var reason = api.ProviderReason;
            if (reason is not null && GoogleDriveSharingReason.RateLimited.Contains(reason)) return ("rate-limited", reason);
            if (reason is not null) return ("access-restricted", reason);
            return ("google-request-failed", null);
        }
        return status switch
        {
            401 => ("sign-in-needed", null),
            404 => ("no-longer-accessible", null),
            429 => ("rate-limited", null),
            _ => ("google-request-failed", null)
        };
    }

    // W3: a finite allowlist of messages authored in this adapter. Anything unrecognised, including
    // any future or provider-influenced text, collapses to the generic invalid-response detail.
    private static string ValidationDetail(string message) => message switch
    {
        "Google returned incomplete sharing metadata." => "missing-metadata",
        "Google returned an invalid sharing collection." => "invalid-metadata",
        "This is not a supported Google Drive web link." => "unsupported-web-link",
        "Google's web link identifies another item." => "item-web-link-mismatch",
        "Google returned inconsistent resource keys." => "resource-key-mismatch",
        "Google returned an incomplete permission collection." => "incomplete-permission-collection",
        "Google returned another file identity." => "item-identity-mismatch",
        "Google returned an ambiguous corpus identity." => "corpus-identity-ambiguous",
        "This Google file is outside the selected corpus." => "item-outside-corpus",
        "This Google item is in the trash." => "item-trashed",
        "Invalid Google parent." => "invalid-metadata",
        "Google returned repeated permission identities; refresh this item." => "repeated-permission-identity",
        "The Google permission inventory exceeded its bounded page limit; no change is allowed from this partial result." => "permission-inventory-truncated",
        "Google returned an invalid permission identity." => "invalid-permission-identity",
        "Google returned a repeated or invalid continuation." => "invalid-continuation",
        "Use an exact Google file, permission or resource identity, not a local or inferred path." => "invalid-identity",
        "The selected Google permission is not a link audience." => "not-a-link-permission",
        "Google returned a domain permission without a domain." => "invalid-metadata",
        "Google did not return a web link for this item." => "unsupported-web-link",
        _ => "invalid-response"
    };

    public async Task<AccountSharingItem> GetItemAsync(string namespaceId, string path,
        CancellationToken cancellationToken = default)
        => await GetItemByIdAsync(namespaceId, ResolveFileId(namespaceId, path), null, cancellationToken);

    public async Task<AccountSharingItem> GetItemByIdAsync(string namespaceId, string fileId, string? resourceKey,
        CancellationToken cancellationToken = default)
    {
        CheckNamespace(namespaceId);
        ValidateId(fileId);
        var file = await GetFileAsync(namespaceId, fileId, resourceKey, cancellationToken);
        return ToItem(file, namespaceId);
    }

    public async Task<IReadOnlyList<AccountSharedLink>> GetItemLinksAsync(AccountSharingItem item,
        CancellationToken cancellationToken = default)
    {
        CheckIdentity(item.ConnectionId, item.NamespaceId, item.Provider); ValidateId(item.Id);
        var snapshot = await ReadSnapshotAsync(item.NamespaceId, item.Id, item.ResourceKey, cancellationToken);
        if (item.RevisionFingerprint != FileFingerprint(snapshot.File)) throw Stale();
        return snapshot.Permissions.Where(IsLinkPermission).Select(permission => ToLink(snapshot, permission, item.NamespaceId)).ToArray();
    }

    public async Task<AccountSharingPage<AccountSharingItem>> ListItemsAsync(string namespaceId, string path,
        AccountSharingCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        CheckNamespace(namespaceId);
        var folderId = ResolveFileId(namespaceId, path); var collection = "items:" + folderId;
        var continuation = CheckCursor(namespaceId, collection, cursor);
        var folder = await GetFileAsync(namespaceId, folderId, null, cancellationToken);
        if (folder.MimeType != GoogleDrivePilotClient.FolderMimeType) throw new InvalidOperationException("Choose a Google Drive folder.");
        using var json = await ListFilesAsync(namespaceId, "trashed = false and '" + folder.Id + "' in parents", continuation, cancellationToken);
        var items = Array(json.RootElement, "files", PageSize).Select(item => ToItem(ReadFile(item, namespaceId), namespaceId)).ToArray();
        var incomplete = Bool(json.RootElement, "incompleteSearch");
        return new(items, Next(json.RootElement, namespaceId, collection, continuation),
            incomplete ? [new(null, "Google returned an incomplete folder listing.")] : null, incomplete);
    }

    public Task<AccountSharedLink> CreateLinkAsync(AccountSharingItem item, string audience,
        CancellationToken cancellationToken = default) => CreateLinkAsync(item, audience, "reader", cancellationToken);

    public async Task<AccountSharedLink> CreateLinkAsync(AccountSharingItem item, string audience, string role,
        CancellationToken cancellationToken = default)
    {
        CheckIdentity(item.ConnectionId, item.NamespaceId, item.Provider); ValidateId(item.Id); ValidateRole(role);
        var target = ParseAudience(audience);
        if (string.IsNullOrWhiteSpace(item.RevisionFingerprint)) throw new InvalidOperationException("Choose the Google item again before sharing.");
        var gate = _fileGates.GetOrAdd(item.Id, _ => new(1, 1)); await gate.WaitAsync(cancellationToken);
        try
        {
            var before = await ReadSnapshotAsync(item.NamespaceId, item.Id, item.ResourceKey, cancellationToken);
            if (FileFingerprint(before.File) != item.RevisionFingerprint) throw Stale();
            RequireCanShare(before.File);
            var existing = before.Permissions.Where(p => p.Type == target.Type && p.Domain == target.Domain).ToArray();
            if (existing.Length > 0)
            {
                if (existing.Length != 1) throw new InvalidOperationException("Several Google permissions match. Select an exact permission from the links list.");
                var link = ToLink(before, existing[0], item.NamespaceId);
                if (!link.CanEdit) throw new InvalidOperationException("The matching Google link is inherited or read-only. Manage its original location in Google Drive.");
                if (link.Access != role) throw new InvalidOperationException("This Google link already exists with another role. Refresh it and explicitly change that role.");
                return link;
            }
            var body = new Dictionary<string, object?> { ["type"] = target.Type, ["role"] = role, ["allowFileDiscovery"] = false };
            if (target.Domain is not null) body["domain"] = target.Domain;
            // No user/group permissions and no notification parameter: Google forbids
            // sendNotificationEmail for anyone/domain requests; these requests send no invitation.
            using var result = await CallAsync(HttpMethod.Post, PermissionRoute(item.Id) + "?" + Query(new()
                { ["fields"] = PermissionFields, ["supportsAllDrives"] = SharedFlag }), body, true, cancellationToken, before.File);
            try
            {
                var created = ReadPermission(result.RootElement);
                if (created.Type != target.Type || created.Domain != target.Domain || created.Role != role || before.Permissions.Any(p => p.Id == created.Id))
                    throw new InvalidDataException("Google did not confirm a new matching direct permission.");
                var after = await ReadSnapshotAsync(item.NamespaceId, item.Id, item.ResourceKey, cancellationToken);
                var confirmed = ExactPermission(after, created.Id);
                if (confirmed.Type != target.Type || confirmed.Domain != target.Domain || confirmed.Role != role || confirmed.Discoverable != false)
                    throw new InvalidDataException("Google did not confirm the requested link settings.");
                VerifyUnrelated(before, after, created.Id);
                var link = ToLink(after, confirmed, item.NamespaceId);
                if (link.IsInherited != false) throw new InvalidDataException("Google has not confirmed direct permission ownership.");
                return link;
            }
            catch (Exception error) when (IsReadbackFailure(error)) { throw Uncertain(); }
        }
        finally { gate.Release(); }
    }

    public async Task<AccountSharedLink> RefreshLinkAsync(AccountSharedLink expected,
        CancellationToken cancellationToken = default)
    {
        CheckLinkIdentity(expected);
        var snapshot = await ReadSnapshotAsync(expected.NamespaceId, expected.ItemId!, expected.ResourceKey, cancellationToken);
        return ToLink(snapshot, ExactPermission(snapshot, expected.PermissionId!), expected.NamespaceId);
    }

    public Task<AccountSharedLink> GetLinkAsync(string namespaceId, string exactUrl,
        CancellationToken cancellationToken = default)
        => GetLinkPermissionAsync(namespaceId, exactUrl, null, cancellationToken);

    public async Task<AccountSharedLink> GetLinkPermissionAsync(string namespaceId, string exactUrl,
        string? permissionId, CancellationToken cancellationToken = default)
    {
        CheckNamespace(namespaceId); var (id, key) = ParseUrl(exactUrl);
        if (permissionId is not null) ValidatePermissionId(permissionId);
        var snapshot = await ReadSnapshotAsync(namespaceId, id, key, cancellationToken);
        var links = snapshot.Permissions.Where(p => IsLinkPermission(p) &&
            (permissionId is null || p.Id == permissionId)).Select(p => ToLink(snapshot, p, namespaceId)).ToArray();
        if (links.Length != 1) throw new InvalidOperationException("This Google URL does not identify exactly one link permission. Select the exact file and permission from the links list.");
        if (links[0].Url != exactUrl) throw new InvalidOperationException("Use the current provider-returned Google link from the links list.");
        return links[0];
    }

    public async Task<AccountSharedLink> ChangeLinkAsync(AccountSharedLink expected, SharedLinkChange change,
        CancellationToken cancellationToken = default)
    {
        CheckLinkIdentity(expected);
        if (change.ChangeExpiry || change.Expires is not null || change.RequirePassword is not null || change.Password is not null || change.AllowDownload is not null)
            throw new NotSupportedException("Google link permissions do not support these Dropbox password, expiry or download settings.");
        if (change.Role is not null) ValidateRole(change.Role);
        if (change.Audience is not null && ParseAudience(change.Audience) != ParseAudience(expected.Audience))
            throw new NotSupportedException("Changing a Google permission's audience is not a role edit. Create or revoke the exact audience permission separately.");
        var gate = _fileGates.GetOrAdd(expected.ItemId!, _ => new(1, 1)); await gate.WaitAsync(cancellationToken);
        try
        {
            var before = await CheckedSnapshotAsync(expected, cancellationToken);
            var permission = ExactPermission(before, expected.PermissionId!);
            if (change.Role is null || change.Role == permission.Role) return ToLink(before, permission, expected.NamespaceId);
            using var result = await CallAsync(HttpMethod.Patch, PermissionRoute(expected.ItemId!, expected.PermissionId) + "?" + Query(new()
                { ["fields"] = PermissionFields, ["supportsAllDrives"] = SharedFlag }), new { role = change.Role }, true, cancellationToken, before.File);
            try
            {
                if (Required(result.RootElement, "id") != expected.PermissionId) throw new InvalidDataException("Permission identity changed.");
                var after = await ReadSnapshotAsync(expected.NamespaceId, expected.ItemId!, expected.ResourceKey, cancellationToken);
                var confirmed = ExactPermission(after, expected.PermissionId!);
                if (confirmed.Role != change.Role || confirmed.Type != permission.Type || confirmed.Domain != permission.Domain ||
                    confirmed.Discoverable != permission.Discoverable || confirmed.Expiry != permission.Expiry || confirmed.View != permission.View)
                    throw new InvalidDataException("Google did not confirm the requested permission settings.");
                VerifyUnrelated(before, after, expected.PermissionId!);
                var link = ToLink(after, confirmed, expected.NamespaceId);
                if (link.IsInherited != false) throw new InvalidDataException("The permission became inherited.");
                return link;
            }
            catch (Exception error) when (IsReadbackFailure(error)) { throw Uncertain(); }
        }
        finally { gate.Release(); }
    }

    public async Task RevokeLinkAsync(AccountSharedLink expected, CancellationToken cancellationToken = default)
    {
        CheckLinkIdentity(expected);
        var gate = _fileGates.GetOrAdd(expected.ItemId!, _ => new(1, 1)); await gate.WaitAsync(cancellationToken);
        try
        {
            var before = await CheckedSnapshotAsync(expected, cancellationToken);
            using var result = await CallAsync(HttpMethod.Delete, PermissionRoute(expected.ItemId!, expected.PermissionId) + "?supportsAllDrives=" + SharedFlag,
                null, true, cancellationToken, before.File);
            try
            {
                var after = await ReadSnapshotAsync(expected.NamespaceId, expected.ItemId!, expected.ResourceKey, cancellationToken);
                if (after.Permissions.Any(p => p.Id == expected.PermissionId)) throw new InvalidDataException("The permission remains present.");
                VerifyUnrelated(before, after, expected.PermissionId!);
            }
            catch (Exception error) when (IsReadbackFailure(error)) { throw Uncertain(); }
        }
        finally { gate.Release(); }
    }

    public Task<AccountSharingPage<AccountFileRequest>> ListRequestsAsync(string namespaceId, AccountSharingCursor? cursor = null, CancellationToken cancellationToken = default) => throw RequestsUnsupported();
    public Task<AccountFileRequest> GetRequestAsync(string namespaceId, string id, CancellationToken cancellationToken = default) => throw RequestsUnsupported();
    public Task<AccountFileRequest> SaveRequestAsync(string namespaceId, FileRequestChange change, AccountFileRequest? expected = null, CancellationToken cancellationToken = default) => throw RequestsUnsupported();
    public Task<AccountFileRequest> SetRequestOpenAsync(AccountFileRequest expected, bool open, CancellationToken cancellationToken = default) => throw RequestsUnsupported();
    private static NotSupportedException RequestsUnsupported() => new("Google Drive does not provide this Dropbox file-request action. No request or collection service was created.");

    private async Task<Snapshot> CheckedSnapshotAsync(AccountSharedLink expected, CancellationToken token)
    {
        var current = await ReadSnapshotAsync(expected.NamespaceId, expected.ItemId!, expected.ResourceKey, token);
        var link = ToLink(current, ExactPermission(current, expected.PermissionId!), expected.NamespaceId);
        if (string.IsNullOrWhiteSpace(expected.RevisionFingerprint) || link.RevisionFingerprint != expected.RevisionFingerprint || link.Url != expected.Url)
            throw Stale();
        if (!link.CanEdit || !link.CanRevoke) throw new InvalidOperationException("This Google permission is inherited, has unverified ownership, or cannot be managed by this account.");
        return current;
    }

    private async Task<Snapshot> ReadSnapshotAsync(string ns, string id, string? key, CancellationToken token) =>
        await ReadSnapshotAsync(await GetFileAsync(ns, id, key, token), ns, token);

    private async Task<Snapshot> ReadSnapshotAsync(DriveFile file, string ns, CancellationToken token)
    {
        var permissions = await ReadPermissionsAsync(file.Id, file, token);
        var inherited = new Dictionary<string, bool?>(StringComparer.Ordinal);
        var parentEvidence = new List<string>();
        IReadOnlyList<Permission>? parentPermissions = null;
        if (file.DriveId is null && !file.InheritanceDisabled && file.Parents.Length > 0 && permissions.Any(p => IsLinkPermission(p) && p.Details.Length == 0))
        {
            // Older My Drive responses may omit permissionDetails. A permission visible on a
            // parent is not safe to edit at this child. Missing parent evidence remains read-only.
            try
            {
                var collected = new List<Permission>();
                foreach (var parent in file.Parents)
                {
                    var p = await ReadPermissionsAsync(parent, null, token);
                    collected.AddRange(p); parentEvidence.Add(parent + ":" + PermissionsFingerprint(p));
                }
                parentPermissions = collected;
            }
            catch (Exception error) when (error is GoogleDriveApiException or InvalidDataException)
            { parentEvidence.Add("parent-permissions-unverified"); }
        }
        foreach (var permission in permissions.Where(IsLinkPermission))
        {
            bool? state = null;
            if (permission.Details.Any(d => d.Inherited == true)) state = true;
            else if (permission.Details.Length > 0 && permission.Details.All(d => d.Inherited == false && d.Type == "file")) state = false;
            else if (file.DriveId is null && permission.Details.Length == 0)
            {
                if (file.InheritanceDisabled || file.Parents.Length == 0) state = false;
                else if (parentPermissions is not null) state = parentPermissions.Any(p => p.Id == permission.Id || p.Type == permission.Type && p.Domain == permission.Domain);
            }
            inherited.Add(permission.Id, state);
        }
        return new(file, permissions, inherited, parentEvidence.Order(StringComparer.Ordinal).ToArray());
    }

    private async Task<IReadOnlyList<Permission>> ReadPermissionsAsync(string fileId, DriveFile? file, CancellationToken token)
    {
        var permissions = new Dictionary<string, Permission>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        for (var page = 0; page < 20; page++)
        {
            using var json = await CallAsync(HttpMethod.Get, PermissionRoute(fileId) + "?" + Query(new()
                { ["pageSize"] = "100", ["fields"] = "nextPageToken,permissions(" + PermissionFields + ")", ["pageToken"] = next, ["supportsAllDrives"] = SharedFlag }), null, false, token, file);
            foreach (var item in Array(json.RootElement, "permissions"))
            {
                var permission = ReadPermission(item);
                if (!permissions.TryAdd(permission.Id, permission)) throw new InvalidDataException("Google returned repeated permission identities; refresh this item.");
            }
            next = String(json.RootElement, "nextPageToken");
            if (next is null) return permissions.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
            CheckNext(next, seen);
        }
        throw new InvalidDataException("The Google permission inventory exceeded its bounded page limit; no change is allowed from this partial result.");
    }

    private Task<JsonDocument> ListFilesAsync(string ns, string q, string? next, CancellationToken token)
    {
        CheckNamespace(ns); var shared = ns != _connection.PermissionId;
        return CallAsync(HttpMethod.Get, "files?" + Query(new()
        {
            ["q"] = q, ["spaces"] = "drive", ["pageSize"] = PageSize.ToString(CultureInfo.InvariantCulture),
            ["fields"] = "nextPageToken,incompleteSearch,files(" + FileFields + ")", ["pageToken"] = next,
            ["corpora"] = shared ? "drive" : "user", ["driveId"] = shared ? ns : null,
            ["supportsAllDrives"] = SharedFlag, ["includeItemsFromAllDrives"] = shared ? "true" : "false"
        }), null, false, token);
    }

    private async Task<DriveFile> GetFileAsync(string ns, string id, string? key, CancellationToken token)
    {
        CheckNamespace(ns); ValidateId(id);
        using var json = await CallAsync(HttpMethod.Get, "files/" + Escape(id) + "?" + Query(new()
            { ["fields"] = FileFields, ["supportsAllDrives"] = SharedFlag }), null, false, token, resourceIdentity: key is null ? null : (id, key));
        var file = ReadFile(json.RootElement, ns);
        if (id != "root" && file.Id != id) throw new InvalidDataException("Google returned another file identity.");
        return file;
    }

    private DriveFile ReadFile(JsonElement item, string ns)
    {
        var id = Required(item, "id"); ValidateId(id); var drive = String(item, "driveId");
        if ((ns == _connection.PermissionId && drive is not null) || (ns != _connection.PermissionId && drive != ns))
            throw new InvalidDataException("This Google file is outside the selected corpus.");
        if (Bool(item, "trashed")) throw new InvalidDataException("This Google item is in the trash.");
        var parents = item.TryGetProperty("parents", out var parentArray)
            ? parentArray.EnumerateArray().Select(p => p.GetString() ?? throw new InvalidDataException("Invalid Google parent.")).Order(StringComparer.Ordinal).ToArray() : [];
        if (parents.Length > 2) throw new InvalidDataException("This Google item has an unsupported parent layout.");
        foreach (var parent in parents) ValidateId(parent);
        var caps = item.TryGetProperty("capabilities", out var c) ? c : default;
        var resourceKey = String(item, "resourceKey"); if (resourceKey is not null) ValidateId(resourceKey);
        var mime = Required(item, "mimeType");
        var web = NormalizeNativeEditorLink(String(item, "webViewLink"), mime, id);
        if (web is not null)
        {
            var parsed = ParseUrl(web);
            if (parsed.Id != id) throw new InvalidDataException("Google's web link identifies another item.");
            if (resourceKey is not null && parsed.Key is not null && parsed.Key != resourceKey) throw new InvalidDataException("Google returned inconsistent resource keys.");
            resourceKey ??= parsed.Key;
            if (resourceKey is not null && parsed.Key is null) web += (web.Contains('?') ? "&" : "?") + "resourcekey=" + Escape(resourceKey);
        }
        return new(id, Required(item, "name"), mime, Required(item, "version"), drive, web, resourceKey,
            Bool(caps, "canShare"), Bool(caps, "canDownload"), parents, Bool(item, "inheritedPermissionsDisabled"));
    }

    // F2: Google returns a native-editor webViewLink on the editor's own host for exactly these two
    // Apps MIME types. That address is neither of the two link addresses this product supports, so the
    // exact host/MIME pair is rewritten to the file-ID-bound Drive link the rest of the product already
    // uses, and the existing id and resource-key consistency checks then run on it unchanged. The
    // editor path, query and fragment are opaque: nothing is read from them, kept, logged, navigated
    // or accepted as direct CLI input, and no permission row is ever synthesised from a web link.
    // A missing link, any other host, a mismatched MIME, a non-default port, embedded user
    // information, a fragment or an oversized address is returned unchanged so the existing refusal
    // still applies.
    private static string? NormalizeNativeEditorLink(string? web, string mime, string id)
    {
        if (web is null || web.Length > 8192 || !NativeEditorHosts.TryGetValue(mime, out var host)) return web;
        if (!Uri.TryCreate(web, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != host ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return web;
        return "https://drive.google.com/open?id=" + Escape(id);
    }

    // The only two editor hosts observed for the Site and Map Apps MIME types. This is a rewrite of two
    // exact provider variants, never an expansion of the accepted public link hosts.
    private static readonly Dictionary<string, string> NativeEditorHosts = new(StringComparer.Ordinal)
    {
        ["application/vnd.google-apps.site"] = "sites.google.com",
        ["application/vnd.google-apps.map"] = "www.google.com"
    };

    private AccountSharingItem ToItem(DriveFile file, string ns) => new(ConnectionId, ns, file.Id, file.Id, file.Name,
        Kind(file.MimeType), SyncProviders.GoogleDrive, FileFingerprint(file), file.WebUrl, file.ResourceKey);

    private AccountSharedLink ToLink(Snapshot snapshot, Permission permission, string ns)
    {
        if (!IsLinkPermission(permission)) throw new InvalidDataException("The selected Google permission is not a link audience.");
        var file = snapshot.File; var inheritance = snapshot.Inherited.GetValueOrDefault(permission.Id);
        var manageable = file.CanShare && inheritance == false && (permission.Role is "reader" or "commenter" or "writer") && permission.View is null;
        var audience = permission.Type == "anyone" ? "public" : "domain:" + permission.Domain;
        if (permission.Type == "domain" && string.IsNullOrWhiteSpace(permission.Domain)) throw new InvalidDataException("Google returned a domain permission without a domain.");
        var note = inheritance == true ? "Inherited access: manage the original parent or shared-drive permission in Google Drive."
            : inheritance is null ? "Permission ownership could not be verified; this link is read-only."
            : !file.CanShare ? "This Google account cannot manage sharing for this item."
            : !manageable ? "This permission role or view is read-only in Sync Shark."
            : "Revoking removes only this direct permission. Other members or inherited access can still open the same URL.";
        return new(ConnectionId, ns, file.WebUrl ?? throw new InvalidDataException("Google did not return a web link for this item."), file.Id,
            file.Name, null, Kind(file.MimeType), audience, permission.Role, ParseDate(permission.Expiry), manageable,
            false, false, false, false, false, file.CanDownload, false, false, [audience], SyncProviders.GoogleDrive,
            permission.Id, Fingerprint(snapshot, permission.Id, ns), inheritance, manageable, file.ResourceKey, permission.Domain,
            manageable ? ["reader", "commenter", "writer"] : [], note, DownloadSettingKnown: false);
    }

    private static Permission ReadPermission(JsonElement item)
    {
        var id = Required(item, "id"); ValidatePermissionId(id);
        var details = item.TryGetProperty("permissionDetails", out var array) ? array.EnumerateArray().Select(d => new Detail(
            String(d, "permissionType"), OptionalBool(d, "inherited"), String(d, "inheritedFrom"), String(d, "role")))
            .OrderBy(d => JsonSerializer.Serialize(d), StringComparer.Ordinal).ToArray() : [];
        return new(id, Required(item, "type"), Required(item, "role"), String(item, "domain"), OptionalBool(item, "allowFileDiscovery"),
            String(item, "expirationTime"), String(item, "view"), details);
    }

    private async Task<JsonDocument> CallAsync(HttpMethod method, string route, object? body, bool effect, CancellationToken token,
        DriveFile? file = null, (string Id, string Key)? resourceIdentity = null)
    {
        GoogleDriveProviderConfiguration.ValidateConnection(_connection); token.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, Api + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(token));
        (string Id, string Key)? identity = resourceIdentity;
        if (identity is null && file?.ResourceKey is { } key) identity = (file.Id, key);
        if (identity is { } resource)
        {
            ValidateId(resource.Id); ValidateId(resource.Key);
            request.Headers.Add("X-Goog-Drive-Resource-Keys", resource.Id + "/" + resource.Key);
        }
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                // F4: effect uncertainty is settled first, before any error body is inspected. Only this
                // sharing adapter reads a finite provider reason, and only from its bounded error body.
                if (effect && ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)) throw Uncertain();
                throw new GoogleDriveApiException(response.StatusCode, response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Reconnect this Google account to restore access.",
                    HttpStatusCode.Forbidden => "Google rejected this operation. This account or its organization may restrict sharing.",
                    HttpStatusCode.NotFound => "This Google item or permission is no longer accessible. Refresh the list.",
                    HttpStatusCode.TooManyRequests => "Google is limiting requests. Wait before refreshing; no automatic mutation retry was made.",
                    _ => "Google rejected this request. Refresh the selected item before trying again."
                }, await ReadProviderReasonAsync(response.Content, token));
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Google returned an oversized sharing response.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream(); var buffer = new byte[16384];
            for (var read = await stream.ReadAsync(buffer, token); read > 0; read = await stream.ReadAsync(buffer, token))
            {
                if (bytes.Length + read > MaximumResponseBytes) throw new InvalidDataException("Google returned an oversized sharing response.");
                bytes.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(bytes.Length == 0 ? "null"u8.ToArray() : bytes.ToArray());
        }
        catch (Exception error) when (effect && error is not (GoogleDriveApiException or GoogleDriveEffectUncertainException) && IsReadbackFailure(error))
        { throw Uncertain(); }
        catch (HttpRequestException error) when (error is not GoogleDriveApiException)
        { throw new IOException("Google sharing could not be reached. Refresh when the connection is available."); }
    }

    // F4: one bounded, in-memory read of a non-success body, made only by this sharing adapter. A
    // missing, oversized, unreadable, malformed, unrecognised or ambiguous reason becomes null, which
    // keeps the existing safe status and message rather than inventing success or a cause. Cancellation
    // still propagates; no retry and no additional provider request is made.
    private static async Task<string?> ReadProviderReasonAsync(HttpContent? content, CancellationToken token)
    {
        if (content is null) return null;
        try
        {
            await using var stream = await content.ReadAsStreamAsync(token);
            using var body = new MemoryStream(); var buffer = new byte[4096];
            for (var read = await stream.ReadAsync(buffer, token); read > 0; read = await stream.ReadAsync(buffer, token))
            {
                if (body.Length + read > MaximumErrorBodyBytes) return null;
                body.Write(buffer, 0, read);
            }
            return GoogleDriveSharingReason.FromBody(body.ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
        { return null; }
    }

    private void CheckLinkIdentity(AccountSharedLink link)
    {
        CheckIdentity(link.ConnectionId, link.NamespaceId, link.Provider);
        ValidateId(link.ItemId ?? ""); ValidatePermissionId(link.PermissionId ?? "");
        var (id, key) = ParseUrl(link.Url);
        if (id != link.ItemId || key != link.ResourceKey) throw new InvalidOperationException("The Google link identity changed. Refresh the selected item.");
    }
    private void CheckIdentity(string connectionId, string ns, string provider)
    {
        if (connectionId != ConnectionId || provider != SyncProviders.GoogleDrive) throw new InvalidOperationException("This item belongs to another provider or account. No change was sent.");
        CheckNamespace(ns);
    }
    private void CheckNamespace(string ns)
    { if (!_namespaces.Contains(ns, StringComparer.Ordinal)) throw new InvalidOperationException("This Google corpus is not enabled and verified for the selected account."); }
    private string? CheckCursor(string ns, string collection, AccountSharingCursor? cursor)
    {
        CheckNamespace(ns); if (cursor is null) return null;
        if (cursor.ConnectionId != ConnectionId || cursor.NamespaceId != ns || cursor.Collection != collection || cursor.Value.Length > 131072)
            throw new InvalidOperationException("This continuation belongs to another Google account, corpus or list.");
        try
        {
            var payload = JsonSerializer.Deserialize<Continuation>(Convert.FromBase64String(cursor.Value));
            if (payload is null || payload.ConnectionId != ConnectionId || payload.NamespaceId != ns || payload.Collection != collection || string.IsNullOrWhiteSpace(payload.Token) || payload.Token.Length > 65536)
                throw new InvalidDataException("The Google continuation binding is invalid.");
            return payload.Token;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new InvalidDataException("The Google continuation is invalid. Refresh the selected list."); }
    }
    private AccountSharingCursor? Next(JsonElement page, string ns, string collection, string? previous)
    {
        var next = String(page, "nextPageToken"); if (next is null) return null;
        if (next == previous || next.Length > 65536 || next.Length == 0) throw new InvalidDataException("Google returned an invalid continuation.");
        return new(ConnectionId, ns, collection, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Continuation(ConnectionId, ns, collection, next))));
    }

    public static void ValidateUrl(string url) => _ = ParseUrl(url);
    private static (string Id, string? Key) ParseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host is not ("drive.google.com" or "docs.google.com") ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || url.Length > 8192)
            throw new InvalidDataException("This is not a supported Google Drive web link.");
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2); var name = Uri.UnescapeDataString(pair[0]);
            if (!query.TryAdd(name, pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "")) throw new InvalidDataException("The Google web link has ambiguous query parameters.");
        }
        var components = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        for (var index = 0; index + 1 < components.Length; index++)
            if (components[index] is "d" or "folders") { id = Uri.UnescapeDataString(components[index + 1]); break; }
        id ??= uri.Host == "drive.google.com" && uri.AbsolutePath == "/open" ? query.GetValueOrDefault("id") : null;
        ValidateId(id ?? ""); var key = query.GetValueOrDefault("resourcekey"); if (key is not null) ValidateId(key);
        return (id!, key);
    }

    private string ResolveFileId(string ns, string value)
    {
        var id = value is "" or "/" or "root" ? ns == _connection.PermissionId ? "root" : ns : value;
        ValidateId(id); return id;
    }
    private static (string Type, string? Domain) ParseAudience(string audience)
    {
        if (audience is "public" or "anyone") return ("anyone", null);
        if (audience.StartsWith("domain:", StringComparison.Ordinal))
        {
            var domain = audience[7..].ToLowerInvariant();
            if (domain.Length is > 0 and <= 253 && domain.Contains('.') && domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
                domain.Split('.').All(label => label.Length is > 0 and <= 63 && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1])))
                return ("domain", domain);
        }
        throw new InvalidDataException("Choose Anyone with the link, or an explicit domain:example.com audience. No access was changed.");
    }
    private static string Kind(string mime) => mime == GoogleDrivePilotClient.FolderMimeType ? "folder" :
        mime == "application/vnd.google-apps.shortcut" ? "shortcut" : mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) ? "native_document" : "file";
    private static bool IsLinkPermission(Permission permission) => permission.Type is "anyone" or "domain";
    private static Permission ExactPermission(Snapshot snapshot, string id) => snapshot.Permissions.SingleOrDefault(p => p.Id == id && IsLinkPermission(p))
        ?? throw new InvalidOperationException("That Google link permission no longer exists. Refresh the list.");
    private static void RequireCanShare(DriveFile file)
    { if (!file.CanShare) throw new InvalidOperationException("Google does not allow this account to manage sharing for this item."); }
    private static void ValidateRole(string role)
    { if (role is not ("reader" or "commenter" or "writer")) throw new InvalidDataException("Choose reader, commenter or writer. Ownership and membership changes are not link actions."); }
    private static void ValidateId(string id)
    { if (id.Length is < 1 or > 512 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new InvalidDataException("Use an exact Google file, permission or resource identity, not a local or inferred path."); }
    private static void ValidatePermissionId(string id)
    { if (id.Length is < 1 or > 512 || id.Any(char.IsControl)) throw new InvalidDataException("Google returned an invalid permission identity."); }
    private static void CheckNext(string next, HashSet<string> seen)
    { if (next.Length is < 1 or > 65536 || !seen.Add(next)) throw new InvalidDataException("Google returned a repeated or invalid continuation."); }
    private string Fingerprint(Snapshot snapshot, string permissionId, string ns) => Hash(new
    { ConnectionId, NamespaceId = ns, File = FileFingerprint(snapshot.File), PermissionId = permissionId,
        Permissions = PermissionsFingerprint(snapshot.Permissions), Inherited = snapshot.Inherited.OrderBy(p => p.Key, StringComparer.Ordinal), snapshot.ParentEvidence });
    private static string FileFingerprint(DriveFile file) => Hash(file);
    private static string PermissionsFingerprint(IEnumerable<Permission> permissions) => Hash(permissions.OrderBy(p => p.Id, StringComparer.Ordinal));
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static void VerifyUnrelated(Snapshot before, Snapshot after, string changedId)
    {
        if (before.File.Id != after.File.Id || before.File.DriveId != after.File.DriveId || !before.File.Parents.SequenceEqual(after.File.Parents) ||
            PermissionsFingerprint(before.Permissions.Where(p => p.Id != changedId)) != PermissionsFingerprint(after.Permissions.Where(p => p.Id != changedId)))
            throw new InvalidDataException("Other Google sharing state changed during confirmation.");
    }
    private static InvalidOperationException Stale() => new("Google sharing or item capabilities changed since this item was shown. Refresh before making a change; no mutation was sent.");
    private static GoogleDriveEffectUncertainException Uncertain() => new("Google may have applied the sharing change, but its exact result was not confirmed. Refresh before deciding what to do; no automatic retry was made.");
    private static bool IsReadbackFailure(Exception error) => error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException;
    private string SharedFlag => _connection.SharedDrivesEnabled ? "true" : "false";
    private static string PermissionRoute(string id, string? permission = null) => "files/" + Escape(id) + "/permissions" + (permission is null ? "" : "/" + Escape(permission));
    private static string Query(Dictionary<string, string?> values) => string.Join('&', values.Where(p => p.Value is not null).Select(p => Escape(p.Key) + "=" + Escape(p.Value!)));
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string? String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Required(JsonElement item, string name) => String(item, name) is { Length: > 0 } value ? value : throw new InvalidDataException("Google returned incomplete sharing metadata.");
    private static bool Bool(JsonElement item, string name) => OptionalBool(item, name) == true;
    private static bool? OptionalBool(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static IEnumerable<JsonElement> Array(JsonElement item, string name, int maximum = 100)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Google returned an invalid sharing collection.");
        // Repeated files/drives can be omitted on an empty Google page. An omitted ACL is not
        // evidence of no permissions and must never authorize a sharing mutation.
        if (!item.TryGetProperty(name, out var value))
        {
            if (name is "files" or "drives") return [];
            throw new InvalidDataException("Google returned an incomplete permission collection.");
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum)
            throw new InvalidDataException("Google returned an invalid or oversized sharing collection.");
        return value.EnumerateArray();
    }
    private static DateTimeOffset? ParseDate(string? value) => value is null ? null : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : throw new InvalidDataException("Google returned an invalid permission expiry.");
    private sealed record DriveFile(string Id, string Name, string MimeType, string Version, string? DriveId, string? WebUrl,
        string? ResourceKey, bool CanShare, bool CanDownload, string[] Parents, bool InheritanceDisabled);
    private sealed record Detail(string? Type, bool? Inherited, string? From, string? Role);
    private sealed record Permission(string Id, string Type, string Role, string? Domain, bool? Discoverable, string? Expiry, string? View, Detail[] Details);
    private sealed record Snapshot(DriveFile File, IReadOnlyList<Permission> Permissions, IReadOnlyDictionary<string, bool?> Inherited, string[] ParentEvidence);
    private sealed record Continuation(string ConnectionId, string NamespaceId, string Collection, string Token);
}
