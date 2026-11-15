using System.Net;
using System.Collections.Concurrent;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Deterministic in-memory Google Drive v3 endpoint used only as external I/O. All decisions under
// test are made by the production client, planner and runner.
internal sealed class SyntheticDriveServer : HttpMessageHandler
{
    internal sealed class Node
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public string? ParentId { get; set; }
        public string MimeType { get; set; } = "application/octet-stream";
        public string? DriveId { get; set; }
        public byte[] Content { get; set; } = [];
        public bool Trashed { get; set; }
        public int Version { get; set; } = 1;
        public string? ShortcutTargetId { get; init; }
        public bool CanDownload { get; set; } = true;

        public long Size => Content.LongLength;
        public string Sha256 => Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();
        public string Md5 => Convert.ToHexString(MD5.HashData(Content)).ToLowerInvariant();
        public string? HeadRevisionOverride { get; set; }
        public string HeadRevisionId => HeadRevisionOverride ?? "rev-" + Id + "-" + Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<byte>> _sessionContent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, string? Parent)> _sessionTargets =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _completedSessions = new(StringComparer.Ordinal);
    private readonly List<(string FileId, bool Removed)> _changes = [];
    private int _sessionCounter;
    private string _newStartToken = "start-token-1";

    internal bool IncompleteSearch { get; init; }
    // Only an explicitly configured full-Host fixture supplies the provider's canonical My Drive root
    // behind its documented "root" alias. Existing adapter fixtures keep their original lookup behaviour.
    internal string? MyDriveRootIdForTest { get; init; }
    internal bool RateLimitOnce { get; set; }
    internal bool Unauthorized { get; set; }
    internal bool InvalidPageToken { get; set; }
    internal bool CorruptDownload { get; set; }
    internal bool ShortChunkAcknowledgement { get; set; }
    internal bool SessionGone { get; set; }
    internal bool LoseSessionCompletionAfterApply { get; set; }
    internal string? SessionLocationForTest { get; set; }
    internal int RequestCalls { get; private set; }
    internal ConcurrentDictionary<string, int> ParentListCalls { get; } = new(StringComparer.Ordinal);
    internal int ForeignRequestCalls { get; private set; }
    internal int SessionChunkCalls { get; private set; }
    internal HttpMethod? StartPageTokenMethod { get; private set; }
    internal int RateLimitHits { get; private set; }
    internal int CreateFolderCalls { get; private set; }
    internal int WriteCalls { get; private set; }
    internal bool RaceBeforeConditionalWrite { get; set; }
    internal bool OmitConditionalEtag { get; set; }
    internal int ConditionalRejections { get; private set; }
    internal Action<Node>? AfterMediaRead { get; set; }
    internal string? CurrentChangeToken { get; private set; }
    internal Func<string, CancellationToken, Task>? AfterReadResponseForTest { get; set; }

    internal readonly HashSet<string> CreatedFolders = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, bool> CorruptDownloadFlag = new(StringComparer.Ordinal);

    internal Node AddFolder(string id, string name, string? parentId, string? driveId = null)
    {
        var node = new Node { Id = id, Name = name,
            ParentId = parentId == "root" && MyDriveRootIdForTest is not null ? MyDriveRootIdForTest : parentId,
            DriveId = driveId,
            MimeType = "application/vnd.google-apps.folder" };
        _nodes[id] = node;
        return node;
    }

    internal Node AddFile(string id, string name, string? parentId, byte[] content, string? driveId = null)
    {
        var node = new Node { Id = id, Name = name, ParentId = parentId, Content = content, DriveId = driveId };
        _nodes[id] = node;
        return node;
    }

    internal Node AddNativeDocument(string id, string name, string? parentId) =>
        AddTyped(id, name, parentId, "application/vnd.google-apps.document");

    internal Node AddShortcut(string id, string name, string? parentId, string targetId)
    {
        var node = new Node { Id = id, Name = name, ParentId = parentId,
            MimeType = "application/vnd.google-apps.shortcut", ShortcutTargetId = targetId };
        _nodes[id] = node;
        return node;
    }

    private Node AddTyped(string id, string name, string? parentId, string mimeType)
    {
        var node = new Node { Id = id, Name = name, ParentId = parentId, MimeType = mimeType };
        _nodes[id] = node;
        return node;
    }

    internal Node Get(string id) => _nodes[id];

    internal string IdByName(string parentId, string name) => _nodes.Values
        .Single(item => string.Equals(item.ParentId,
                            parentId == "root" && MyDriveRootIdForTest is not null ? MyDriveRootIdForTest : parentId,
                            StringComparison.Ordinal) &&
                        string.Equals(item.Name, name, StringComparison.Ordinal) && !item.Trashed).Id;

    internal byte[] BodyFor(string id) => _nodes[id].Content;

    internal void BumpVersion(string id) => _nodes[id].Version++;

    internal void RecordChange(string fileId, bool removed) => _changes.Add((fileId, removed));

    internal IReadOnlyList<string> Names(string parentId) => _nodes.Values
        .Where(item => string.Equals(item.ParentId, parentId, StringComparison.Ordinal) && !item.Trashed)
        .Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        RequestCalls++;
        if (uri.Host != "www.googleapis.com" || uri.Scheme != Uri.UriSchemeHttps)
        {
            ForeignRequestCalls++;
            throw new InvalidOperationException("The synthetic Drive transport received an unauthorized origin.");
        }
        var path = uri.AbsolutePath;
        var query = ParseQuery(uri.Query);
        if (query.ContainsKey("uploadType") && !path.StartsWith("/upload/drive/", StringComparison.Ordinal))
            throw new InvalidOperationException("Media upload used the metadata API endpoint.");

        if (Unauthorized)
            return Error(HttpStatusCode.Unauthorized, "authError");
        if (RateLimitOnce)
        {
            // Google documents 403 userRateLimitExceeded and 429 for rate pressure, with Retry-After
            // only sometimes present. This fixture returns one 429 with a hint, then succeeds.
            RateLimitOnce = false;
            RateLimitHits++;
            var throttled = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{\"error\":{\"message\":\"rateLimitExceeded\"}}", Encoding.UTF8,
                    "application/json")
            };
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromMilliseconds(50));
            return throttled;
        }
        if (path.EndsWith("/about", StringComparison.Ordinal))
            return Json("""{"user":{"permissionId":"perm-account-one","emailAddress":"perm-account-one@example.invalid","displayName":"Drive User"},"storageQuota":{"limit":"107374182400","usage":"1024"}}""");
        if (path.EndsWith("/changes/startPageToken", StringComparison.Ordinal))
        {
            StartPageTokenMethod = request.Method;
            return Json("{" + Q("startPageToken") + ":" + Q(_newStartToken) + "}");
        }
        if (path.EndsWith("/changes", StringComparison.Ordinal))
        {
            if (InvalidPageToken) return Error(HttpStatusCode.Gone, "pageToken is invalid");
            return ChangesJson(query);
        }
        if (path.Contains("/revisions/", StringComparison.Ordinal) &&
            query.TryGetValue("alt", out var revisionAlt) && revisionAlt == "media")
        {
            var revisionFileId = path.Split('/').SkipWhile(part => part != "files").Skip(1).FirstOrDefault();
            if (revisionFileId is null || !_nodes.TryGetValue(revisionFileId, out var revisionNode))
                return Error(HttpStatusCode.NotFound, "notFound");
            return Media(revisionNode.Content);
        }
        if (path.EndsWith("/files", StringComparison.Ordinal))
        {
            if (request.Method == HttpMethod.Put && query.ContainsKey("upload_id"))
                return await SessionChunkAsync(request, cancellationToken);
            if (query.TryGetValue("uploadType", out var uploadType) && uploadType == "resumable")
                return StartSession(request, cancellationToken);
            if (request.Method == HttpMethod.Post)
                return await CreateAsync(request, cancellationToken);
            var listing = await ListAsync(query, cancellationToken);
            if (AfterReadResponseForTest is { } barrier)
                await barrier(query.TryGetValue("q", out var predicate) &&
                    predicate == "'root-folder' in parents and trashed = false" ? "root-list" : "other-list",
                    cancellationToken);
            return listing;
        }
        if (path.Contains("/files/", StringComparison.Ordinal))
        {
            var remainder = path[(path.IndexOf("/files/", StringComparison.Ordinal) + 7)..];
            var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var fileId = Uri.UnescapeDataString(segments[0]);
            if (fileId == "root" && MyDriveRootIdForTest is not null) fileId = MyDriveRootIdForTest;
            if (segments.Length == 2 && segments[1] == "copy")
                return await CopyAsync(fileId, request, cancellationToken);
            if (!_nodes.TryGetValue(fileId, out var node))
                return Error(HttpStatusCode.NotFound, "notFound");
            if (path.StartsWith("/drive/v2/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return Json(JsonSerializer.Serialize(new { id = node.Id, etag = OmitConditionalEtag ? null : "\"" + node.Id + "-" + node.Version + "\"",
                    version = node.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), mimeType = node.MimeType,
                    labels = new { trashed = node.Trashed }, parents = node.ParentId is null ? Array.Empty<object>() : new object[] { new { id = node.ParentId } } }));
            if ((request.Method == HttpMethod.Put && path.StartsWith("/upload/drive/v2/", StringComparison.Ordinal)) ||
                (request.Method == HttpMethod.Patch && path.StartsWith("/drive/v2/", StringComparison.Ordinal)))
            {
                if (RaceBeforeConditionalWrite) { RaceBeforeConditionalWrite = false; node.Version++; node.Content = Encoding.UTF8.GetBytes("concurrent edit"); }
                if (request.Headers.IfMatch.SingleOrDefault()?.Tag != "\"" + node.Id + "-" + node.Version + "\"")
                { ConditionalRejections++; return Error(HttpStatusCode.PreconditionFailed, "conditionNotMet"); }
            }
            if (query.TryGetValue("alt", out var alt) && alt == "media")
            {
                if (CorruptDownload) return Media(node.Content.Concat(new byte[] { 0x7f }).ToArray());
                if (CorruptDownloadFlag.TryGetValue(fileId, out var corrupt) && corrupt)
                    return Media(node.Content.Concat(new byte[] { 0x7f }).ToArray());
                var mediaResponse = MediaRange(node.Content, request);
                AfterMediaRead?.Invoke(node);
                return mediaResponse;
            }
            if (query.TryGetValue("uploadType", out var mediaType) && mediaType == "media")
                return await UpdateContentAsync(node, request, cancellationToken);
            if (request.Method == HttpMethod.Patch)
                return await PatchAsync(node, request, query, cancellationToken);
            return Json(FileJson(node));
        }
        return Error(HttpStatusCode.NotFound, "notFound");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0) continue;
            result[Uri.UnescapeDataString(part[..index])] = Uri.UnescapeDataString(part[(index + 1)..]);
        }
        return result;
    }

    private async Task<HttpResponseMessage> ListAsync(Dictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        var filter = query.TryGetValue("q", out var value) ? value : string.Empty;
        var parentId = ExtractBetween(filter, "'", "' in parents");
        if (parentId == "root" && MyDriveRootIdForTest is not null) parentId = MyDriveRootIdForTest;
        ParentListCalls.AddOrUpdate(parentId ?? "<unscoped>", 1, (_, value) => value + 1);
        var nameFilter = ExtractBetween(filter, "name = '", "'");
        var includeTrashed = filter.Contains("trashed = true", StringComparison.OrdinalIgnoreCase);
        var matches = _nodes.Values.Where(item =>
                (parentId is null || string.Equals(item.ParentId, parentId, StringComparison.Ordinal)) &&
                (nameFilter is null || string.Equals(item.Name, nameFilter, StringComparison.Ordinal)) &&
                (includeTrashed || !item.Trashed))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var files = string.Join(',', matches.Select(FileJson));
        var incomplete = IncompleteSearch
            ? "," + Q("incompleteSearch") + ":true"
            : string.Empty;
        await Task.CompletedTask;
        return Json("{" + Q("files") + ":[" + files + "]" + incomplete + "}");
    }

    private static string? ExtractBetween(string value, string start, string end)
    {
        var from = value.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return null;
        from += start.Length;
        var to = value.IndexOf(end, from, StringComparison.Ordinal);
        if (to < 0) return null;
        return value[from..to];
    }

    private async Task<HttpResponseMessage> CreateAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var contentType = request.Content?.Headers.ContentType?.MediaType ?? "application/json";
        if (contentType == "multipart/related")
        {
            var boundary = request.Content!.Headers.ContentType!.Parameters
                .FirstOrDefault(item => item.Name == "boundary")?.Value?.Trim('"') ?? "boundary";
            var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var (metadata, content) = SplitMultipart(body, boundary);
            using var document = JsonDocument.Parse(metadata);
            var name = document.RootElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? "file"
                : "file";
            var parent = document.RootElement.TryGetProperty("parents", out var parents) &&
                         parents.ValueKind == JsonValueKind.Array
                ? parents[0].GetString()
                : null;
            var id = "created-" + (_nodes.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var node = AddFile(id, name!, parent, content);
            WriteCalls++;
            return Json(FileJson(node));
        }
        using var text = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var folderName = text.RootElement.TryGetProperty("name", out var folderNameElement)
            ? folderNameElement.GetString() ?? "Folder"
            : "Folder";
        var folderParent = text.RootElement.TryGetProperty("parents", out var folderParents) &&
                           folderParents.ValueKind == JsonValueKind.Array
            ? folderParents[0].GetString()
            : null;
        CreateFolderCalls++;
        CreatedFolders.Add(folderName!);
        var folderId = "folder-" + (_nodes.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var created = AddFolder(folderId, folderName!, folderParent);
        WriteCalls++;
        return Json(FileJson(created));
    }

    private static (byte[] Metadata, byte[] Content) SplitMultipart(byte[] body, string boundary)
    {
        var marker = Encoding.UTF8.GetBytes("--" + boundary);
        var contentMarker = Encoding.UTF8.GetBytes("Content-Type: application/octet-stream");
        var metadataMarker = Encoding.UTF8.GetBytes("Content-Type: application/json");
        var metadataStart = IndexOf(body, metadataMarker) + metadataMarker.Length;
        var metadataSep = IndexOf(body, Encoding.UTF8.GetBytes("\r\n\r\n"), metadataStart);
        var contentStart = IndexOf(body, contentMarker, metadataSep) + contentMarker.Length;
        var contentSep = IndexOf(body, Encoding.UTF8.GetBytes("\r\n\r\n"), contentStart);
        var closing = IndexOf(body, marker, contentSep);
        var metadata = body[(metadataSep + 4)..IndexOf(body, marker, metadataSep)];
        var content = body[(contentSep + 4)..(closing - 2)];
        return (metadata, content);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
    {
        for (var index = from; index <= haystack.Length - needle.Length; index++)
        {
            var match = true;
            for (var offset = 0; offset < needle.Length; offset++)
                if (haystack[index + offset] != needle[offset]) { match = false; break; }
            if (match) return index;
        }
        return -1;
    }

    private async Task<HttpResponseMessage> UpdateContentAsync(Node node, HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var content = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        node.Content = content;
        node.Version++;
        WriteCalls++;
        return Json(FileJson(node));
    }

    private async Task<HttpResponseMessage> PatchAsync(Node node, HttpRequestMessage request,
        Dictionary<string, string> query, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (body.Contains("\"trashed\":true", StringComparison.Ordinal))
            node.Trashed = true;
        if (!string.IsNullOrWhiteSpace(body) && body != "null")
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("name", out var name))
                node.Name = name.GetString() ?? node.Name;
            if (document.RootElement.TryGetProperty("title", out var title))
                node.Name = title.GetString() ?? node.Name;
            if (document.RootElement.TryGetProperty("labels", out var labels) &&
                labels.TryGetProperty("trashed", out var trashLabel) && trashLabel.GetBoolean())
                node.Trashed = true;
            if (document.RootElement.TryGetProperty("trashed", out var trashed) &&
                trashed.ValueKind == JsonValueKind.True)
                node.Trashed = true;
        }
        if (query.TryGetValue("addParents", out var addParents) && !string.IsNullOrWhiteSpace(addParents))
            node.ParentId = addParents;
        node.Version++;
        WriteCalls++;
        return Json(FileJson(node));
    }

    private async Task<HttpResponseMessage> CopyAsync(string fileId, HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!_nodes.TryGetValue(fileId, out var source)) return Error(HttpStatusCode.NotFound, "notFound");
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var name = document.RootElement.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? source.Name
            : source.Name;
        var parent = document.RootElement.TryGetProperty("parents", out var parents) &&
                     parents.ValueKind == JsonValueKind.Array
            ? parents[0].GetString()
            : source.ParentId;
        var copy = AddFile("copy-" + (_nodes.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            name!, parent, source.Content);
        WriteCalls++;
        return Json(FileJson(copy));
    }

    private HttpResponseMessage StartSession(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sessionId = "session-" + (++_sessionCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _sessions[sessionId] = "pending";
        _sessionContent[sessionId] = [];
        var body = request.Content is null
            ? "{}"
            : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var name = document.RootElement.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? "uploaded.bin"
            : "uploaded.bin";
        var parent = document.RootElement.TryGetProperty("parents", out var parents) &&
                     parents.ValueKind == JsonValueKind.Array && parents.GetArrayLength() > 0
            ? parents[0].GetString()
            : "root-folder";
        _sessionTargets[sessionId] = (name!, parent);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Headers.Location = new Uri(SessionLocationForTest ??
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=" + sessionId,
            UriKind.RelativeOrAbsolute);
        return response;
    }

    private async Task<HttpResponseMessage> SessionChunkAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        SessionChunkCalls++;
        var sessionId = ParseQuery(request.RequestUri!.Query)["upload_id"];
        if (SessionGone) return Error(HttpStatusCode.NotFound, "session not found");
        if (!_sessions.ContainsKey(sessionId)) return Error(HttpStatusCode.NotFound, "unknown session");
        var accumulated = _sessionContent[sessionId];
        var contentRange = request.Content?.Headers.TryGetValues("Content-Range", out var values) == true
            ? values.FirstOrDefault()
            : null;
        if (contentRange is not null && contentRange.StartsWith("bytes */", StringComparison.Ordinal))
        {
            if (_completedSessions.TryGetValue(sessionId, out var completedId))
                return Json(FileJson(_nodes[completedId]));
            var response = new HttpResponseMessage((HttpStatusCode)308) { Content = new StringContent("") };
            if (accumulated.Count > 0)
                response.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (accumulated.Count - 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            return response;
        }
        var total = long.Parse(contentRange!.Split('/').Last(), System.Globalization.CultureInfo.InvariantCulture);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var start = long.Parse(contentRange.Split(' ').Last().Split('-').First().Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        if (ShortChunkAcknowledgement && start + body.LongLength < total && _sessions[sessionId] == "pending")
        {
            _sessions[sessionId] = "short";
            var accepted = Math.Max(1, body.Length / 2);
            accumulated.AddRange(body.AsSpan(0, accepted).ToArray());
            var shortResponse = new HttpResponseMessage((HttpStatusCode)308) { Content = new StringContent("") };
            shortResponse.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (accumulated.Count - 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            return shortResponse;
        }
        accumulated.AddRange(body);
        if (accumulated.Count < total)
        {
            var partial = new HttpResponseMessage((HttpStatusCode)308) { Content = new StringContent("") };
            partial.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (accumulated.Count - 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            return partial;
        }
        var target = _sessionTargets[sessionId];
        var node = _nodes.Values.FirstOrDefault(item => item.Name == target.Name && !item.Trashed)
                   ?? AddFile("resumed-" + sessionId, target.Name, target.Parent, []);
        node.Content = accumulated.ToArray();
        node.Version++;
        WriteCalls++;
        _completedSessions[sessionId] = node.Id;
        if (LoseSessionCompletionAfterApply)
        {
            LoseSessionCompletionAfterApply = false;
            throw new HttpRequestException("synthetic completion response lost after one applied write");
        }
        return Json(FileJson(node));
    }

    private HttpResponseMessage ChangesJson(Dictionary<string, string> query)
    {
        var pageToken = query.TryGetValue("pageToken", out var token) ? token : CurrentChangeToken;
        CurrentChangeToken = "start-token-" + (_changes.Count + 1).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var entries = _changes.Select(change =>
        {
            if (change.Removed)
                return "{\"fileId\":\"" + change.FileId + "\",\"removed\":true,\"changeType\":\"file\",\"time\":\"2026-09-17T00:00:00.000Z\"}";
            var node = _nodes[change.FileId];
            return "{\"fileId\":\"" + change.FileId + "\",\"removed\":false,\"changeType\":\"file\",\"time\":\"2026-09-17T00:00:00.000Z\",\"file\":" +
                   FileJson(node) + "}";
        });
        _ = pageToken;
        return Json("{\"newStartPageToken\":\"" + CurrentChangeToken + "\",\"changes\":[" +
                    string.Join(',', entries) + "]}");
    }

    private static HttpResponseMessage Media(byte[] content) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(content)
    };

    // Google documents byte-range partial download for media requests; the fixture honours the exact
    // requested window so the client's 206 and Content-Range checks are exercised for real.
    private static HttpResponseMessage MediaRange(byte[] content, HttpRequestMessage request)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        if (range is null) return Media(content);
        var from = range.From ?? 0;
        var to = range.To ?? content.LongLength - 1;
        if (from < 0 || to >= content.LongLength || to < from)
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = new StringContent("range not satisfiable")
            };
        var slice = content[(int)from..(int)(to + 1)];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice)
        };
        response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to,
            content.LongLength);
        return response;
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static string Q(string value) => JsonSerializer.Serialize(value);

    private static HttpResponseMessage Error(HttpStatusCode status, string reason) => new(status)
    {
        Content = new StringContent("{\"error\":{\"message\":\"" + reason + "\"}}", Encoding.UTF8,
            "application/json")
    };

    private static string FileJson(Node node)
    {
        var parents = node.ParentId is null ? "[]" : "[\"" + node.ParentId + "\"]";
        var driveId = node.DriveId is null ? string.Empty : ",\"driveId\":\"" + node.DriveId + "\"";
        var shortcut = node.ShortcutTargetId is null
            ? string.Empty
            : ",\"shortcutDetails\":{\"targetId\":\"" + node.ShortcutTargetId +
              "\",\"targetMimeType\":\"application/vnd.google-apps.folder\"}";
        var digest = node.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal)
            ? string.Empty
            : ",\"size\":\"" + node.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) +
              "\",\"md5Checksum\":\"" + node.Md5 + "\",\"sha256Checksum\":\"" + node.Sha256 +
              "\",\"headRevisionId\":\"" + node.HeadRevisionId + "\"";
        return "{\"id\":\"" + node.Id + "\",\"name\":\"" + node.Name + "\",\"mimeType\":\"" +
               node.MimeType + "\",\"parents\":" + parents + ",\"trashed\":" +
               (node.Trashed ? "true" : "false") + ",\"version\":\"" +
               node.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"" + digest +
               driveId + shortcut +
               ",\"capabilities\":{\"canDownload\":" + (node.CanDownload ? "true" : "false") + "}}";
    }
}
