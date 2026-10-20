using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

namespace Massdex.SyncShark.V2;

/// <summary>An in-memory Dropbox account under the root "/Pilot" (folder id id:pilot-folder) for the v2 checks.</summary>
internal sealed partial class DropboxWire : HttpMessageHandler
{
    internal const string RootId = "id:pilot-folder";

    private sealed record Node(string Relative, string Id, string Rev, byte[] Bytes, bool Folder,
        string? ClientModified);

    private sealed record ChangePage(object[] Entries, bool HasMore, string Next);

    private readonly Dictionary<string, Node> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node> _history = new(StringComparer.Ordinal);
    public string? LastDownloadRevision { get; private set; }
    public string? LastUploadExpectedRevision { get; private set; }
    public void RetainCurrentRevision(string path) => _history[_items[path].Rev] = _items[path];
    private readonly Dictionary<string, byte[]> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChangePage> _changePages = new(StringComparer.Ordinal);
    private readonly List<string> _deleted = new();
    private readonly Dictionary<string, object[]> _deletedPages = new(StringComparer.Ordinal);
    private readonly object _revisionLock = new();
    private int _revisionsInFlight;
    private int _maxConcurrentRevisions;
    private readonly Dictionary<string, Node> _outside = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<HttpStatusCode>> _scripted = new(StringComparer.Ordinal);
    private long _ids;
    private long _revs;

    /// <summary>The root folder's displayed path; set to another path to simulate a moved root.</summary>
    public string RootPath { get; set; } = "/Pilot";

    /// <summary>The commit paths of the most recent finish_batch_v2 request, in entry order.</summary>
    public List<string> LastUploadCommitPaths { get; private set; } = [];

    /// <summary>When true, the root moves as the next finish_batch_v2 completes, after its entries are applied.</summary>
    public bool MoveRootOnFinishBatch { get; set; }

    /// <summary>Overrides every upload success entry's returned path, simulating a result outside the root.</summary>
    public string? UploadSuccessPathOverride { get; set; }

    /// <summary>Overrides a download response's Dropbox-API-Result fields (null keeps the live metadata).</summary>
    public string? DownloadMetadataIdOverride { get; set; }
    public string? DownloadMetadataRevisionOverride { get; set; }
    public string? DownloadMetadataHashOverride { get; set; }
    public long? DownloadMetadataSizeOverride { get; set; }

    /// <summary>When set, the download body is these bytes instead of the node's exact revision bytes.</summary>
    public byte[]? DownloadBodyOverride { get; set; }

    /// <summary>The cursor returned by get_latest_cursor.</summary>
    public string LatestCursor { get; set; } = "cursor-latest";
    public string? RevisionDeletedTime { get; set; } = "2026-10-12T08:00:00Z";
    public object? RevisionResponseOverride { get; set; }
    public string? RevisionErrorTag { get; set; }
    /// <summary>When set, revision history returns one revision carrying this content hash.</summary>
    public string? RevisionContentHash { get; set; }

    /// <summary>When positive, the deleted-item listing is split into pages of this many entries.</summary>
    public int DeletedPageSize { get; set; }

    /// <summary>The limit sent with the most recent include_deleted list_folder request.</summary>
    public int? LastDeletedListingLimit { get; private set; }

    /// <summary>The highest number of list_revisions requests ever in flight at once.</summary>
    public int MaxConcurrentRevisions => Volatile.Read(ref _maxConcurrentRevisions);

    /// <summary>Optional latency hook awaited by each list_revisions request.</summary>
    public Func<CancellationToken, Task>? OnRevisionAsync { get; set; }

    /// <summary>Every endpoint absolute path requested, in order.</summary>
    public List<string> Calls { get; } = new();
    public Dictionary<string, string> NameWriteErrors { get; } = new(StringComparer.Ordinal);
    public List<string> AttemptedWritePaths { get; } = [];
    public bool UploadRefusalUsesReason { get; set; }

    /// <summary>Every list_folder scope, to distinguish subtree pruning from result filtering.</summary>
    public List<(string Path, bool Recursive)> ListRequests { get; } = new();
    public Action? AfterListingCaptured { get; set; }

    /// <summary>Every change/list continuation cursor, including requests whose scripted response fails.</summary>
    public List<string> ContinueCursors { get; } = new();

    /// <summary>Runs after a continuation response is captured, before the caller receives it.</summary>
    public Action<string>? AfterContinueResponse { get; set; }

    /// <summary>Returns change-feed metadata for the current live file, preserving its downloadable id/revision.</summary>
    public object ChangeExistingFile(string relative) => FileMetadata(_items[relative]);

    /// <summary>The parent_rev of the most recent delete_v2 request, or null.</summary>
    public string? LastDeleteParentRev { get; private set; }

    /// <summary>The mode of the most recent files/upload request ("add" or "update").</summary>
    public string? LastUploadMode { get; private set; }

    /// <summary>Adds a live file; read it back with ObserveAsync to build a planned state.</summary>
    public void SeedFile(string relative, string content, DateTimeOffset? clientModified = null) =>
        _items[relative] = NewNode(relative, Encoding.UTF8.GetBytes(content), false,
            (clientModified ?? DateTimeOffset.UnixEpoch).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    /// <summary>Adds a live folder.</summary>
    public void SeedFolder(string relative) => _items[relative] = NewNode(relative, Array.Empty<byte>(), true, null);

    /// <summary>Moves a live item out of the root to <paramref name="namespacePath"/> (still in the account).</summary>
    public void MoveOutsideRoot(string relative, string namespacePath)
    {
        var node = _items[relative];
        _items.Remove(relative);
        _outside[node.Id] = node with { Relative = namespacePath };
    }

    /// <summary>Adds a path that Dropbox's deleted-item listing and revision history still report as deleted.</summary>
    public void SeedDeletedEntry(string relative) => _deleted.Add(relative);

    /// <summary>Scripts a change-feed page keyed by the cursor a caller passes.</summary>
    public void SeedChangePage(string cursor, object[] entries, bool hasMore, string next) =>
        _changePages[cursor] = new ChangePage(entries, hasMore, next);

    /// <summary>Overrides the next <paramref name="count"/> responses for one endpoint with an error status.</summary>
    public void Script(string endpoint, HttpStatusCode status, int count)
    {
        if (!_scripted.TryGetValue(endpoint, out var queue)) _scripted[endpoint] = queue = new Queue<HttpStatusCode>();
        for (var index = 0; index < count; index++) queue.Enqueue(status);
    }

    /// <summary>A change-feed file entry (an add/update) carrying one fresh identity.</summary>
    public object ChangeFile(string relative, string content) =>
        FileMetadata(NewNode(relative, Encoding.UTF8.GetBytes(content), false, "2026-10-01T00:00:00Z"));

    /// <summary>A change-feed folder entry (a folder re-created during the walk); it is not live in the listing.</summary>
    public object ChangeFolder(string relative) =>
        FolderMetadata(relative, "id:recreated-" + relative.Replace('/', '-'));

    /// <summary>A change-feed delete entry that preserves the given displayed (Dropbox-absolute) path.</summary>
    public object ChangeDeleted(string dropboxPath) => new Dictionary<string, object>
    {
        [".tag"] = "deleted", ["path_display"] = dropboxPath, ["name"] = Name(dropboxPath)
    };

    /// <summary>A change-feed delete entry that carries only a lowercased path, as Dropbox may return.</summary>
    public object ChangeDeletedLower(string lowerDropboxPath) => new Dictionary<string, object>
    {
        [".tag"] = "deleted", ["path_lower"] = lowerDropboxPath, ["name"] = Name(lowerDropboxPath)
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        // Concurrent v2 operations reach the wire at once; the call log must not lose or corrupt entries.
        lock (Calls) Calls.Add(path);
        if (path == "/2/files/list_folder/continue")
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            lock (ContinueCursors) ContinueCursors.Add(body.RootElement.GetProperty("cursor").GetString()!);
        }
        if (_scripted.TryGetValue(path, out var script) && script.Count > 0) return Error(script.Dequeue());
        return await HandleAsync(request, path, token);
    }

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, string path,
        CancellationToken token)
    {
        switch (path)
        {
            // The composition's account seam refreshes a saved credential before its first file request.
            case "/oauth2/token":
                return Json(new
                {
                    access_token = "fixture-access",
                    token_type = "bearer",
                    expires_in = 14400,
                    account_id = "account-id"
                });
            case "/2/users/get_current_account":
                return Json(new
                {
                    account_id = "account-id",
                    root_info = new { root_namespace_id = "root-namespace", home_namespace_id = "home-namespace" }
                });
            case "/2/files/list_folder/get_latest_cursor":
                return Json(new { cursor = LatestCursor });
            case "/2/files/list_folder":
                return await ListingAsync(request, token);
            case "/2/files/list_folder/continue":
                if (FailFeedAfterFolderDelete && WholeFolderDeletes > 0) return Error(HttpStatusCode.BadRequest);
                return Json(await ContinueAsync(request, token));
            case "/2/files/restore":
                return await RestoreAsync(request, token);
            case "/2/files/get_metadata":
            {
                using var metadataRequest = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                return await GetMetadataAsync(metadataRequest.RootElement.GetProperty("path").GetString()!, token,
                    metadataRequest.RootElement.TryGetProperty("include_deleted", out var includeDeleted) && includeDeleted.GetBoolean());
            }
            case "/2/files/delete_v2":
                return await DeleteAsync(request, token);
            case "/2/files/delete_batch":
                return await DeleteBatchAsync(request, token);
            case "/2/files/delete_batch/check":
                return await CheckDeleteBatchAsync(request, token);
            case "/2/files/move_v2":
                return await MoveAsync(request, token);
            case "/2/files/create_folder_v2":
                return CreateFolder(await PathAsync(request, token));
            case "/2/files/create_folder_batch":
                return await CreateFolderBatchAsync(request, token);
            case "/2/files/create_folder_batch/check":
                return CheckCreateFolderBatch();
            case "/2/files/upload":
                return await UploadAsync(request, token);
            case "/2/files/upload_session/start":
                return await StartSessionAsync(request, token);
            case "/2/files/upload_session/finish":
                return await FinishSessionAsync(request, token);
            case "/2/files/upload_session/finish_batch_v2":
                return await FinishBatchAsync(request, token);
            case "/2/files/download":
                return Download(request);
            case "/2/files/list_revisions":
                return await ListRevisionsAsync(await PathAsync(request, token), token);
            default:
                return Error(HttpStatusCode.NotImplemented);
        }
    }

    private async Task<HttpResponseMessage> ListingAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var root = document.RootElement;
        var includeDeleted = root.TryGetProperty("include_deleted", out var del) && del.GetBoolean();
        if (includeDeleted)
            LastDeletedListingLimit = root.TryGetProperty("limit", out var limit) ? limit.GetInt32() : null;
        var requested = root.GetProperty("path").GetString()!;
        var recursive = root.GetProperty("recursive").GetBoolean();
        lock (ListRequests) ListRequests.Add((requested, recursive));
        if (!TryResolveFolder(requested, out var baseRelative)) return NotFound();
        var prefix = baseRelative.Length == 0 ? "" : baseRelative + "/";
        var live = _items.Values.Where(node => node.Relative.Length > prefix.Length &&
            node.Relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (!recursive) live = live.Where(node => node.Relative[prefix.Length..].IndexOf('/') < 0);
        var entries = live.OrderBy(node => node.Relative, StringComparer.Ordinal)
            .Select(node => (object)Metadata(node)).ToList();
        if (includeDeleted)
        {
            // A real include_deleted listing returns live and deleted entries together; the in-scope deleted
            // entries are appended so one walk serves both the live snapshot and the deleted-path set.
            entries.AddRange(DeletedInScope(prefix, recursive));
        }
        AfterListingCaptured?.Invoke();
        if (includeDeleted && DeletedPageSize > 0 && entries.Count > DeletedPageSize)
        {
            _deletedPages.Clear();
            var pageCount = (entries.Count + DeletedPageSize - 1) / DeletedPageSize;
            for (var index = 1; index < pageCount; index++)
                _deletedPages["deleted-page-" + index] =
                    entries.Skip(index * DeletedPageSize).Take(DeletedPageSize).ToArray();
            return Json(new { entries = entries.Take(DeletedPageSize).ToArray(), cursor = "deleted-page-1", has_more = true });
        }
        return Json(Paged(entries.ToArray(), "listing-done"));
    }

    // The deleted entries under one listed folder: direct children for a non-recursive listing, the whole
    // subtree for a recursive one. A deleted entry carries its path only in path_lower/path_display.
    private IEnumerable<object> DeletedInScope(string prefix, bool recursive) =>
        _deleted.Where(relative => relative.Length > prefix.Length &&
                relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (recursive || relative[prefix.Length..].IndexOf('/') < 0))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .Select(relative => (object)new Dictionary<string, object>
            {
                [".tag"] = "deleted",
                ["path_display"] = Display(relative),
                ["path_lower"] = Display(relative).ToLowerInvariant(),
                ["name"] = Name(relative)
            });

    private async Task<object> ContinueAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var cursor = document.RootElement.GetProperty("cursor").GetString()!;
        if (cursor.StartsWith("deleted-page-", StringComparison.Ordinal) &&
            _deletedPages.TryGetValue(cursor, out var deletedPage))
        {
            var index = int.Parse(cursor["deleted-page-".Length..], CultureInfo.InvariantCulture);
            var next = "deleted-page-" + (index + 1);
            var hasMore = _deletedPages.ContainsKey(next);
            return new { entries = deletedPage, cursor = hasMore ? next : cursor + "-done", has_more = hasMore };
        }
        var response = _changePages.TryGetValue(cursor, out var page)
            ? new { entries = page.Entries, cursor = page.Next, has_more = page.HasMore }
            : new { entries = Array.Empty<object>(), cursor = cursor + "-end", has_more = false };
        AfterContinueResponse?.Invoke(cursor);
        return response;
    }

    private static async Task<string> PathAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        return document.RootElement.GetProperty("path").GetString()!;
    }

    public string? MetadataFailurePath { get; set; }

    private async Task<HttpResponseMessage> GetMetadataAsync(string path, CancellationToken token, bool includeDeleted = false)
    {
        lock (MetadataPaths) MetadataPaths.Add(path);
        if (path == MetadataFailurePath) throw new IOException("Scripted transient metadata failure.");
        // A test may hold a root-id read to prove the post-commit re-read is hard-bounded.
        if (path == RootId && ++_rootMetadataReads > RootMetadataHoldAfter && RootMetadataGate is { } gate)
            await gate.WaitAsync(token);
        if (path == RootId || string.Equals(path, RootPath, StringComparison.OrdinalIgnoreCase))
            return Json(FolderMetadata("", RootId));
        if (_outside.TryGetValue(path, out var away))
            return Json(new Dictionary<string, object>(FileMetadata(away))
            {
                ["path_display"] = "/" + away.Relative, ["path_lower"] = ("/" + away.Relative).ToLowerInvariant(),
            });
        var node = FindNode(path);
        if (node is null && includeDeleted && ToRelative(path) is { } relative &&
            _deleted.Contains(relative, StringComparer.OrdinalIgnoreCase))
            return Json(ChangeDeleted(RootPath + "/" + relative));
        return node is null ? NotFound() : Json(Metadata(node));
    }

    private Node NewNode(string relative, byte[] bytes, bool folder, string? clientModified) =>
        new(relative, "id:" + (++_ids).ToString(CultureInfo.InvariantCulture),
            "r" + (++_revs).ToString(CultureInfo.InvariantCulture), bytes, folder, clientModified);

    private bool TryResolveFolder(string path, out string baseRelative)
    {
        baseRelative = "";
        if (path == RootId || string.Equals(path, RootPath, StringComparison.OrdinalIgnoreCase)) return true;
        var byId = _items.Values.FirstOrDefault(item => item.Folder &&
            string.Equals(item.Id, path, StringComparison.Ordinal));
        if (byId is not null) { baseRelative = byId.Relative; return true; }
        var relative = ToRelative(path);
        if (relative is not null && _items.TryGetValue(relative, out var node) && node.Folder)
        {
            baseRelative = relative;
            return true;
        }
        return false;
    }

    private string? ToRelative(string path)
    {
        if (string.Equals(path, RootPath, StringComparison.OrdinalIgnoreCase)) return "";
        var idPrefix = RootId + "/";
        if (path.StartsWith(idPrefix, StringComparison.Ordinal)) return path[idPrefix.Length..];
        var displayPrefix = RootPath.TrimEnd('/') + "/";
        return path.StartsWith(displayPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[displayPrefix.Length..]
            : null;
    }

    private static object Paged(object[] entries, string cursor) =>
        new { entries, cursor, has_more = false };

    private string Display(string relative) => RootPath.TrimEnd('/') + "/" + relative;

    private static string Name(string path) => path.TrimEnd('/').Split('/').Last();

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.Conflict)
    {
        Content = new StringContent(ErrorBody("not_found"), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Conflict() => new(HttpStatusCode.Conflict)
    {
        Content = new StringContent(ErrorBody("conflict"), Encoding.UTF8, "application/json")
    };

    private static string ErrorBody(string tag) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["error"] = new Dictionary<string, object>
        {
            [".tag"] = "path",
            ["path"] = new Dictionary<string, object> { [".tag"] = tag }
        }
    });

    private static HttpResponseMessage Error(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"error_summary\":\"scripted failure\",\"error\":{\".tag\":\"other\"}}",
                Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("Retry-After", "0");
        return response;
    }
}
