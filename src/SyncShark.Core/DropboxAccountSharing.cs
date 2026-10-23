using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

public sealed record AccountSharingCursor(string ConnectionId, string NamespaceId, string Collection, string Value)
{
    public override string ToString() => "Account-bound continuation";
}
// W1: ItemId and Message stay positional and unchanged so every existing two-argument construction
// and the Dropbox behaviour remain valid. Code, Stage and DetailCode are optional, privacy-safe
// classifications: a bounded machine-readable cause, the stage that failed, and an allowlisted
// validation detail. They never carry URLs, identities, keys, tokens, names, paths or raw error text.
public sealed record AccountSharingIssue(string? ItemId, string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Stage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DetailCode = null);
public sealed record AccountSharingPage<T>(IReadOnlyList<T> Items, AccountSharingCursor? Next,
    IReadOnlyList<AccountSharingIssue>? Issues = null, bool Incomplete = false);

// Shared presentation state, independent of WPF so the actual page accumulator can be exercised.
public sealed class AccountSharingIssueSummary
{
    private readonly Dictionary<(string ConnectionId, string Category), (string AccountLabel, int Count)> _counts = [];
    private readonly HashSet<string> _legacy = new(StringComparer.Ordinal);
    private readonly List<string> _guidance = [];
    private bool _incomplete;

    public IReadOnlyList<string> Lines => _counts.Select(pair =>
        pair.Value.AccountLabel + ": " + pair.Key.Category + " (" + pair.Value.Count +
        (pair.Value.Count == 1 ? " item)" : " items)"))
        .Concat(_guidance)
        .Concat(_legacy).Concat(_incomplete ? new[] { "Available links are shown; some items could not be inspected." } : [])
        .ToArray();

    public void Clear() { _counts.Clear(); _legacy.Clear(); _guidance.Clear(); _incomplete = false; }

    public void AddPage(string connectionId, string accountLabel, IReadOnlyList<AccountSharingIssue>? issues, bool incomplete)
    {
        _incomplete |= incomplete;
        foreach (var issue in issues ?? [])
        {
            var category = issue.Code switch
            {
                "sign-in-needed" => "Sign-in needed",
                // F6: a permission denial on an item the account can still see is normally the owner's
                // sharing setting, not this account's sign-in, so it gets its own wording and a
                // recovery instruction instead of a reauthentication hint.
                "access-restricted" when issue.DetailCode == GoogleDriveSharingReason.SharingDetailsUnavailable
                    => "Sharing details unavailable to this account",
                "access-restricted" => "Access restricted",
                "no-longer-accessible" => "No longer accessible",
                "rate-limited" => "Rate limited",
                "google-request-failed" => "Google request failed",
                "connection-failed" => "Connection failed",
                "invalid-response" => "Invalid Google response",
                null => null,
                _ => "Item could not be inspected"
            };
            if (category is null) { _legacy.Add(accountLabel + ": " + issue.Message); continue; }
            if (issue.Code == "access-restricted" && issue.DetailCode == GoogleDriveSharingReason.SharingDetailsUnavailable)
                Guide("Open these items in Google Drive, or ask their owner to check sharing.");
            else if (issue.Code == "rate-limited")
                Guide("Google is limiting requests. Wait a little, then refresh.");
            var key = (connectionId, category);
            _counts[key] = (accountLabel, _counts.TryGetValue(key, out var prior) ? prior.Count + 1 : 1);
        }
    }

    // One concise instruction per cause, shown once however many items, pages or accounts report it.
    private void Guide(string line) { if (!_guidance.Contains(line, StringComparer.Ordinal)) _guidance.Add(line); }
}
public sealed record AccountSharedLink(string ConnectionId, string NamespaceId, string Url, string? ItemId,
    string Name, string? Path, string ItemKind, string Audience, string Access, DateTimeOffset? Expires,
    bool CanRevoke, bool PasswordRequired, bool CanSetPassword, bool CanRemovePassword,
    bool CanSetExpiry, bool CanRemoveExpiry, bool AllowDownload, bool CanAllowDownload, bool CanDisallowDownload,
    IReadOnlyList<string> AllowedAudiences, string Provider = SyncProviders.Dropbox,
    string? PermissionId = null, string? RevisionFingerprint = null, bool? IsInherited = null,
    bool CanEdit = true, string? ResourceKey = null, string? Domain = null,
    IReadOnlyList<string>? AllowedRoles = null, string? ManagementNote = null, bool DownloadSettingKnown = true);
public sealed record AccountFileRequest(string ConnectionId, string NamespaceId, string Id, string Url,
    string Title, string? Destination, string Description, DateTimeOffset Created, bool IsOpen, long FileCount,
    DateTimeOffset? Deadline, string? LateUploads);
public sealed record AccountSharingItem(string ConnectionId, string NamespaceId, string Id, string Path,
    string Name, string Kind, string Provider = SyncProviders.Dropbox, string? RevisionFingerprint = null,
    string? WebUrl = null, string? ResourceKey = null);

public sealed class SharedLinkChange
{
    public string? Audience { get; init; }
    public bool ChangeExpiry { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public bool? RequirePassword { get; init; }
    public string? Password { get; init; }
    public bool? AllowDownload { get; init; }
    public string? Role { get; init; }
    public override string ToString() => "Shared-link settings change (private input omitted)";
}
public sealed record FileRequestChange(string Title, string Destination, string Description, bool IsOpen,
    bool ChangeDeadline = false, DateTimeOffset? Deadline = null, string? LateUploads = null);

public interface IAccountSharingClient
{
    string ConnectionId { get; }
    IReadOnlyList<string> Namespaces { get; }
    Task<AccountSharingPage<AccountSharedLink>> ListLinksAsync(string namespaceId, AccountSharingCursor? cursor = null, CancellationToken cancellationToken = default);
    Task<AccountSharingPage<AccountFileRequest>> ListRequestsAsync(string namespaceId, AccountSharingCursor? cursor = null, CancellationToken cancellationToken = default);
    Task<AccountSharingItem> GetItemAsync(string namespaceId, string path, CancellationToken cancellationToken = default);
    Task<AccountSharingPage<AccountSharingItem>> ListItemsAsync(string namespaceId, string path, AccountSharingCursor? cursor = null, CancellationToken cancellationToken = default);
    Task<AccountSharedLink> CreateLinkAsync(AccountSharingItem item, string audience, CancellationToken cancellationToken = default);
    Task<AccountSharedLink> RefreshLinkAsync(AccountSharedLink expected, CancellationToken cancellationToken = default);
    Task<AccountSharedLink> GetLinkAsync(string namespaceId, string exactUrl, CancellationToken cancellationToken = default);
    Task<AccountSharedLink> ChangeLinkAsync(AccountSharedLink expected, SharedLinkChange change, CancellationToken cancellationToken = default);
    Task RevokeLinkAsync(AccountSharedLink expected, CancellationToken cancellationToken = default);
    Task<AccountFileRequest> GetRequestAsync(string namespaceId, string id, CancellationToken cancellationToken = default);
    Task<AccountFileRequest> SaveRequestAsync(string namespaceId, FileRequestChange change, AccountFileRequest? expected = null, CancellationToken cancellationToken = default);
    Task<AccountFileRequest> SetRequestOpenAsync(AccountFileRequest expected, bool open, CancellationToken cancellationToken = default);
}

// Full account management, independent of folder-pair selection. Every cursor and actionable
// snapshot carries its account and namespace; a different account can never consume it.
public sealed class DropboxAccountSharingClient(HttpClient http, IDropboxAccessTokenSource tokens,
    DropboxConnectionBinding connection) : IAccountSharingClient
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public string ConnectionId => connection.ConnectionId;
    public IReadOnlyList<string> Namespaces => new[] { connection.HomeNamespaceId, connection.RootNamespaceId }.Distinct(StringComparer.Ordinal).ToArray();

    public async Task<AccountSharingPage<AccountSharedLink>> ListLinksAsync(string namespaceId,
        AccountSharingCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        CheckCursor(namespaceId, "links", cursor);
        using var json = await CallAsync("sharing/list_shared_links", namespaceId,
            cursor is null ? new { } : new { cursor = cursor.Value }, false, cancellationToken);
        var data = json.RootElement;
        return new(data.GetProperty("links").EnumerateArray().Select(item => ReadLink(item, namespaceId)).ToArray(),
            Next(data, namespaceId, "links"));
    }

    public async Task<AccountSharingPage<AccountFileRequest>> ListRequestsAsync(string namespaceId,
        AccountSharingCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        RequireFileRequests(); CheckCursor(namespaceId, "requests", cursor);
        using var json = await CallAsync(cursor is null ? "file_requests/list_v2" : "file_requests/list/continue",
            namespaceId, cursor is null ? new { limit = 100 } : new { cursor = cursor.Value }, false, cancellationToken);
        var data = json.RootElement;
        return new(data.GetProperty("file_requests").EnumerateArray().Select(item => ReadRequest(item, namespaceId)).ToArray(),
            Next(data, namespaceId, "requests"));
    }

    public async Task<AccountSharingItem> GetItemAsync(string namespaceId, string path,
        CancellationToken cancellationToken = default)
    {
        CheckNamespace(namespaceId);
        if (!path.StartsWith("id:", StringComparison.Ordinal)) path = AbsolutePath(path);
        using var json = await CallAsync("files/get_metadata", namespaceId, new { path, include_deleted = false }, false, cancellationToken);
        var item = json.RootElement; var kind = Required(item, ".tag");
        if (kind is not ("file" or "folder")) throw new InvalidDataException("Choose an existing file or folder.");
        return new(ConnectionId, namespaceId, Required(item, "id"), String(item, "path_display") ?? Required(item, "path_lower"),
            Required(item, "name"), kind);
    }

    public async Task<AccountSharingPage<AccountSharingItem>> ListItemsAsync(string namespaceId, string path,
        AccountSharingCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        var folder = AbsolutePath(path); var collection = "items:" + folder;
        CheckCursor(namespaceId, collection, cursor);
        using var json = await CallAsync(cursor is null ? "files/list_folder" : "files/list_folder/continue", namespaceId,
            cursor is null ? new { path = folder == "/" ? "" : folder, recursive = false, limit = 100 } : (object)new { cursor = cursor.Value },
            false, cancellationToken);
        var data = json.RootElement;
        return new(data.GetProperty("entries").EnumerateArray().Where(item => Tag(item, ".tag") is "file" or "folder")
            .Select(item => new AccountSharingItem(ConnectionId, namespaceId, Required(item, "id"),
                String(item, "path_display") ?? Required(item, "path_lower"), Required(item, "name"), Required(item, ".tag"))).ToArray(),
            Next(data, namespaceId, collection));
    }

    public async Task<AccountSharedLink> CreateLinkAsync(AccountSharingItem item, string audience,
        CancellationToken cancellationToken = default)
    {
        CheckIdentity(item.ConnectionId, item.NamespaceId); ValidateAudience(audience);
        var current = await GetItemAsync(item.NamespaceId, item.Id, cancellationToken);
        if (current.Id != item.Id || current.Path != item.Path) throw new IOException("This item moved or changed. Choose it again before sharing.");
        using var json = await CallAsync("sharing/create_shared_link_with_settings", item.NamespaceId,
            new { path = item.Id, settings = new { audience, access = "viewer" } }, true, cancellationToken);
        var result = ReadEffect(() => ReadLink(json.RootElement, item.NamespaceId));
        if (result.ItemId is { } id && id != item.Id) throw new DropboxEffectUncertainException("The link result did not match the selected item. Refresh before retrying.");
        return result;
    }

    public async Task<AccountSharedLink> RefreshLinkAsync(AccountSharedLink expected,
        CancellationToken cancellationToken = default)
    {
        CheckIdentity(expected.ConnectionId, expected.NamespaceId); ValidateUrl(expected.Url);
        var current = await GetLinkAsync(expected.NamespaceId, expected.Url, cancellationToken);
        if (current.Url != expected.Url || expected.ItemId is { } id && current.ItemId != id)
            throw new IOException("The shared link no longer identifies the same item. Refresh the list.");
        return current;
    }

    public async Task<AccountSharedLink> GetLinkAsync(string namespaceId, string exactUrl,
        CancellationToken cancellationToken = default)
    {
        CheckIdentity(ConnectionId, namespaceId); ValidateUrl(exactUrl);
        using var json = await CallAsync("sharing/get_shared_link_metadata", namespaceId,
            new { url = exactUrl }, false, cancellationToken);
        var current = ReadLink(json.RootElement, namespaceId);
        if (current.Url != exactUrl) throw new InvalidDataException("The shared link changed. Refresh the list.");
        return current;
    }

    public async Task<AccountSharedLink> ChangeLinkAsync(AccountSharedLink expected, SharedLinkChange change,
        CancellationToken cancellationToken = default)
    {
        if (change.Role is not null) throw new NotSupportedException("Dropbox link-role changes are not supported by this action.");
        var current = await RefreshLinkAsync(expected, cancellationToken);
        var settings = new Dictionary<string, object>();
        if (change.Audience is { } audience)
        {
            ValidateAudience(audience);
            if (!current.AllowedAudiences.Contains(audience, StringComparer.Ordinal)) throw new InvalidOperationException("Dropbox does not allow this audience for this link.");
            settings["audience"] = audience;
        }
        if (change.ChangeExpiry)
        {
            if (change.Expires is { } expiry)
            {
                if (!current.CanSetExpiry || expiry <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Choose a future expiry supported by this account.");
                settings["expires"] = Timestamp(expiry);
            }
            else if (!current.CanRemoveExpiry) throw new InvalidOperationException("Dropbox does not allow removing this link's expiry.");
        }
        if (change.RequirePassword is { } require)
        {
            if (require && (!current.CanSetPassword || string.IsNullOrWhiteSpace(change.Password)) || !require && !current.CanRemovePassword)
                throw new InvalidOperationException("The requested password setting is unavailable or a password is missing.");
            settings["require_password"] = require;
            if (require) settings["link_password"] = change.Password!;
        }
        if (change.AllowDownload is { } allow)
        {
            if (allow && !current.CanAllowDownload || !allow && !current.CanDisallowDownload)
                throw new InvalidOperationException("Dropbox does not allow changing downloads for this link.");
            settings["allow_download"] = allow;
        }
        if (settings.Count == 0 && !change.ChangeExpiry) return current;
        using var json = await CallAsync("sharing/modify_shared_link_settings", current.NamespaceId,
            new { url = current.Url, settings, remove_expiration = change.ChangeExpiry && change.Expires is null }, true, cancellationToken);
        var result = ReadEffect(() => ReadLink(json.RootElement, current.NamespaceId));
        if (result.Url != current.Url || current.ItemId is { } id && result.ItemId != id)
            throw new DropboxEffectUncertainException("The result did not identify the same link. Refresh before retrying.");
        return result;
    }

    public async Task RevokeLinkAsync(AccountSharedLink expected, CancellationToken cancellationToken = default)
    {
        var current = await RefreshLinkAsync(expected, cancellationToken);
        if (!current.CanRevoke) throw new InvalidOperationException("Only an account allowed by Dropbox can revoke this link.");
        using var response = await CallAsync("sharing/revoke_shared_link", current.NamespaceId,
            new { url = current.Url }, true, cancellationToken);
    }

    public async Task<AccountFileRequest> GetRequestAsync(string namespaceId, string id,
        CancellationToken cancellationToken = default)
    {
        RequireFileRequests(); CheckNamespace(namespaceId); ValidateRequestId(id);
        using var json = await CallAsync("file_requests/get", namespaceId, new { id }, false, cancellationToken);
        var result = ReadRequest(json.RootElement, namespaceId);
        if (result.Id != id) throw new InvalidDataException("Dropbox returned a different file request.");
        return result;
    }

    public async Task<AccountFileRequest> SaveRequestAsync(string namespaceId, FileRequestChange change,
        AccountFileRequest? expected = null, CancellationToken cancellationToken = default)
    {
        RequireFileRequests(); CheckNamespace(namespaceId);
        if (string.IsNullOrWhiteSpace(change.Title) || change.Title.Trim().Length > 255 || change.Description.Length > 10000)
            throw new InvalidDataException("Enter a title up to 255 characters and a description up to 10,000 characters.");
        var destination = AbsolutePath(change.Destination);
        if (expected is not null)
        {
            CheckIdentity(expected.ConnectionId, expected.NamespaceId);
            if (expected.NamespaceId != namespaceId) throw new InvalidOperationException("The destination namespace changed. Reopen this request.");
            var current = await GetRequestAsync(namespaceId, expected.Id, cancellationToken);
            if (current.Title != expected.Title || current.Destination != expected.Destination || current.Description != expected.Description ||
                current.IsOpen != expected.IsOpen || current.Deadline != expected.Deadline || current.LateUploads != expected.LateUploads)
                throw new InvalidOperationException("This request's settings changed elsewhere. Refresh it before saving; uploaded files are untouched.");
        }
        var folder = await GetItemAsync(namespaceId, destination, cancellationToken);
        if (folder.Kind != "folder") throw new InvalidOperationException("Choose a Dropbox folder as the destination.");
        var body = new Dictionary<string, object?> { ["title"] = change.Title.Trim(), ["destination"] = destination,
            ["description"] = change.Description, ["open"] = change.IsOpen };
        object? deadline = null;
        if (change.ChangeDeadline && change.Deadline is { } date)
        {
            if (date <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Choose a future deadline.");
            if (change.LateUploads is not (null or "one_day" or "two_days" or "seven_days" or "thirty_days" or "always"))
                throw new InvalidDataException("Choose one of the supported late-upload periods.");
            var value = new Dictionary<string, object> { ["deadline"] = Timestamp(date) };
            if (change.LateUploads is not null) value["allow_late_uploads"] = change.LateUploads;
            deadline = value;
        }
        if (expected is null)
        { if (deadline is not null) body["deadline"] = deadline; }
        else
        {
            body["id"] = expected.Id;
            if (change.ChangeDeadline) body["deadline"] = new Dictionary<string, object?> { [".tag"] = "update", ["update"] = deadline };
        }
        using var json = await CallAsync(expected is null ? "file_requests/create" : "file_requests/update",
            namespaceId, body, true, cancellationToken);
        var result = ReadEffect(() => ReadRequest(json.RootElement, namespaceId));
        if (expected is not null && result.Id != expected.Id)
            throw new DropboxEffectUncertainException("The response identified a different request. Refresh before retrying.");
        return result;
    }

    public async Task<AccountFileRequest> SetRequestOpenAsync(AccountFileRequest expected, bool open,
        CancellationToken cancellationToken = default)
    {
        CheckIdentity(expected.ConnectionId, expected.NamespaceId);
        var current = await GetRequestAsync(expected.NamespaceId, expected.Id, cancellationToken);
        if (current.IsOpen == open) return current;
        using var json = await CallAsync("file_requests/update", expected.NamespaceId,
            new { id = expected.Id, open }, true, cancellationToken);
        var result = ReadEffect(() => ReadRequest(json.RootElement, expected.NamespaceId));
        if (result.Id != expected.Id || result.IsOpen != open)
            throw new DropboxEffectUncertainException("Dropbox has not confirmed the requested open/closed state. Refresh before retrying.");
        return result;
    }

    private async Task<JsonDocument> CallAsync(string route, string namespaceId, object body, bool effect, CancellationToken token)
    {
        CheckNamespace(namespaceId); DropboxProviderConfiguration.ValidateConnection(connection);
        token.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/" + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(token));
        request.Headers.Add("Dropbox-API-Path-Root", JsonSerializer.Serialize(new Dictionary<string, string> { [".tag"] = "namespace_id", ["namespace_id"] = namespaceId }));
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                if (effect && ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout))
                    throw new DropboxEffectUncertainException("Dropbox did not confirm the change. Refresh before retrying.");
                var reason = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Reconnect this account to restore access.",
                    HttpStatusCode.Forbidden => "This account needs the required permission or a supported Dropbox plan.",
                    HttpStatusCode.Conflict => "Dropbox rejected this choice. The item may have moved, the link may already exist, or an account policy may restrict this setting. Refresh before trying again.",
                    HttpStatusCode.TooManyRequests => "Dropbox is limiting requests. Wait a little, then refresh.",
                    _ => "Dropbox could not complete this request."
                };
                throw new DropboxApiException(response.StatusCode, reason);
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Dropbox returned too much data in one page.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream(); var buffer = new byte[16384];
            for (var read = await stream.ReadAsync(buffer, token); read > 0; read = await stream.ReadAsync(buffer, token))
            {
                if (bytes.Length + read > MaximumResponseBytes) throw new InvalidDataException("Dropbox returned too much data in one page.");
                bytes.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(bytes.Length == 0 ? "null"u8.ToArray() : bytes.ToArray());
        }
        catch (Exception error) when (effect && error is not (DropboxApiException or DropboxEffectUncertainException) &&
                                      error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or JsonException)
        { throw new DropboxEffectUncertainException("The change may have reached Dropbox, but its result could not be read. Refresh before retrying.", error); }
    }

    private AccountSharedLink ReadLink(JsonElement item, string ns)
    {
        var url = Required(item, "url"); ValidateUrl(url); var p = item.GetProperty("link_permissions");
        var allowed = new List<string>();
        if (p.TryGetProperty("audience_options", out var options))
            foreach (var option in options.EnumerateArray()) if (Bool(option, "allowed")) allowed.Add(Tag(option, "audience"));
        if (allowed.Count == 0 && p.TryGetProperty("visibility_policies", out var policies))
            foreach (var option in policies.EnumerateArray()) if (Bool(option, "allowed"))
            { var value = Tag(option, "policy"); if (value is "public" or "team_only") allowed.Add(value == "team_only" ? "team" : value); }
        return new(ConnectionId, ns, url, String(item, "id"), Required(item, "name"), String(item, "path_lower"),
            Required(item, ".tag"), Tag(p, "effective_audience") is { } audience && audience != "unknown" ? audience : Tag(p, "resolved_visibility"),
            Tag(p, "link_access_level"), Date(item, "expires"), Bool(p, "can_revoke"), Bool(p, "require_password"),
            Bool(p, "can_set_password"), Bool(p, "can_remove_password"), Bool(p, "can_set_expiry"), Bool(p, "can_remove_expiry"),
            Bool(p, "allow_download"), Bool(p, "can_allow_download"), Bool(p, "can_disallow_download"), allowed.Distinct().ToArray());
    }
    private static T ReadEffect<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new DropboxEffectUncertainException("Dropbox may have saved the change, but its confirmation was incomplete. Refresh before retrying.", error); }
    }
    private AccountFileRequest ReadRequest(JsonElement item, string ns)
    {
        var id = Required(item, "id"); ValidateRequestId(id); var url = Required(item, "url"); ValidateUrl(url);
        if (!new Uri(url).AbsolutePath.StartsWith("/request/", StringComparison.Ordinal)) throw new InvalidDataException("Dropbox returned an unexpected file request URL.");
        var deadline = item.TryGetProperty("deadline", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
        var count = item.GetProperty("file_count").GetInt64(); if (count < 0) throw new InvalidDataException("Dropbox returned an invalid upload count.");
        return new(ConnectionId, ns, id, url, Required(item, "title"), String(item, "destination"), String(item, "description") ?? "",
            Date(item, "created") ?? throw new InvalidDataException("The request creation date is missing."), Bool(item, "is_open"), count,
            deadline.ValueKind == JsonValueKind.Object ? Date(deadline, "deadline") : null,
            deadline.ValueKind == JsonValueKind.Object && Tag(deadline, "allow_late_uploads") is { } late && late != "unknown" ? late : null);
    }
    private AccountSharingCursor? Next(JsonElement data, string ns, string collection)
    {
        if (!Bool(data, "has_more")) return null;
        var value = Required(data, "cursor"); if (value.Length > 65536) throw new InvalidDataException("Dropbox returned an invalid continuation.");
        return new(ConnectionId, ns, collection, value);
    }
    private void CheckCursor(string ns, string collection, AccountSharingCursor? cursor)
    {
        CheckNamespace(ns);
        if (cursor is not null && (cursor.ConnectionId != ConnectionId || cursor.NamespaceId != ns || cursor.Collection != collection || string.IsNullOrEmpty(cursor.Value)))
            throw new InvalidOperationException("This page belongs to a different account or list. Refresh the selected account.");
    }
    private void CheckIdentity(string id, string ns)
    { if (id != ConnectionId) throw new InvalidOperationException("This item belongs to a different account. No change was sent."); CheckNamespace(ns); }
    private void CheckNamespace(string ns)
    { if (!Namespaces.Contains(ns, StringComparer.Ordinal)) throw new InvalidOperationException("This location is outside the selected account's namespaces."); }
    private void RequireFileRequests()
    { if (!DropboxProviderConfiguration.HasFileRequestAccess(connection.Scopes)) throw new InvalidOperationException("Enable File Requests for this account first."); }
    private static void ValidateAudience(string value)
    { if (value is not ("public" or "team" or "no_one")) throw new InvalidDataException("Choose a supported link audience."); }
    private static void ValidateRequestId(string id)
    { if (id.Length is < 1 or > 512 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new InvalidDataException("The file request identity is invalid."); }
    public static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host is not ("www.dropbox.com" or "dropbox.com") || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException("This is not a supported Dropbox link.");
    }
    private static string AbsolutePath(string path)
    { if (!path.Trim().StartsWith('/')) throw new InvalidDataException("Enter a Dropbox path starting with /, or use Browse."); return PilotPath.NormalizeDropboxRoot(path.Trim()); }
    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static string? String(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Required(JsonElement item, string name) => String(item, name) is { Length: > 0 } value ? value : throw new InvalidDataException("Dropbox returned incomplete metadata.");
    private static bool Bool(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Tag(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "unknown" : value.ValueKind == JsonValueKind.Object ? String(value, ".tag") ?? "unknown" : "unknown" : "unknown";
    private static DateTimeOffset? Date(JsonElement item, string name) => String(item, name) is { } value && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}

public sealed partial class SyncAuthorityRuntimeHost
{
    public async Task<IAccountSharingClient> CreateAccountSharingClientAsync(string connectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId); stateAuthority.VerifyAuthority();
        if (LoadGoogleDriveConnection(connectionId) is { } google)
        {
            var normalized = google.Normalize();
            if (normalized.ConnectionId != connectionId) throw new InvalidOperationException("The selected Google account changed. Refresh the account list.");
            GoogleDriveProviderConfiguration.ValidateConnection(normalized);
            var client = new GoogleDriveAccountSharingClient(httpClient, GoogleTokenSource(normalized), normalized);
            await client.DiscoverNamespacesAsync(cancellationToken);
            return client;
        }
        var connection = await RequireConnectionAsync(cancellationToken, connectionId);
        if (connection.ConnectionId != connectionId) throw new InvalidOperationException("The selected account changed. Refresh the account list.");
        return new DropboxAccountSharingClient(httpClient, AccountTokenSource(connection), connection);
    }
}
