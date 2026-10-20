using System.Globalization;
using System.Net;
using System.Text.Json;
using Massdex.SyncShark;

namespace Massdex.SyncShark.V2;

// The mutation and content endpoints of the in-memory Dropbox (delete, move, mkdir, upload, download).
internal sealed partial class DropboxWire
{
    private async Task<HttpResponseMessage> DeleteAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var root = document.RootElement;
        var path = root.GetProperty("path").GetString()!;
        LastDeleteParentRev = root.TryGetProperty("parent_rev", out var parentRev) ? parentRev.GetString() : null;
        var node = FindNode(path);
        if (node is null) return NotFound();
        if (LastDeleteParentRev is not null && !string.Equals(LastDeleteParentRev, node.Rev, StringComparison.Ordinal))
            return Conflict();
        RemoveSubtree(node.Relative);
        return Json(new { metadata = Metadata(node) });
    }

    private async Task<HttpResponseMessage> MoveAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var root = document.RootElement;
        var fromPath = root.GetProperty("from_path").GetString()!;
        var destination = ToRelative(root.GetProperty("to_path").GetString()!);
        AttemptedWritePaths.Add(destination!);
        if (destination is not null && NameWriteErrors.TryGetValue(destination, out var refusal))
            return NameError("to", refusal);
        var node = _items.Values.FirstOrDefault(item => string.Equals(item.Id, fromPath, StringComparison.Ordinal));
        if (node is null || destination is null || destination.Length == 0) return NotFound();
        if (_items.ContainsKey(destination)) return Conflict();
        Rebase(node.Relative, destination);
        return Json(new { metadata = Metadata(_items[destination]) });
    }

    private HttpResponseMessage CreateFolder(string path)
    {
        var relative = ToRelative(path);
        if (relative is null || relative.Length == 0 || _items.ContainsKey(relative)) return Conflict();
        var node = NewNode(relative, Array.Empty<byte>(), true, null);
        _items[relative] = node;
        return Json(new { metadata = Metadata(node) });
    }

    private async Task<HttpResponseMessage> UploadAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
        var relative = ToRelative(argument.RootElement.GetProperty("path").GetString()!)!;
        AttemptedWritePaths.Add(relative);
        if (NameWriteErrors.TryGetValue(relative, out var refusal)) return NameError("path", refusal);
        var mode = argument.RootElement.GetProperty("mode");
        LastUploadMode = mode.ValueKind == JsonValueKind.Object ? "update" : mode.GetString();
        LastUploadExpectedRevision = LastUploadMode == "update" ? mode.GetProperty("update").GetString() : null;
        var bytes = await request.Content!.ReadAsByteArrayAsync(token);
        if (LastUploadMode == "update")
        {
            if (!_items.TryGetValue(relative, out var current) ||
                !string.Equals(current.Rev, mode.GetProperty("update").GetString(), StringComparison.Ordinal))
                return Conflict();
        }
        else if (_items.ContainsKey(relative)) return Conflict();
        var node = StoreUpload(relative, bytes, argument.RootElement);
        return Json(FileMetadata(node));
    }

    private async Task<HttpResponseMessage> StartSessionAsync(HttpRequestMessage request, CancellationToken token)
    {
        UploadSessionStarted?.Invoke();
        var session = "session-" + (++_ids).ToString(CultureInfo.InvariantCulture);
        _sessions[session] = await request.Content!.ReadAsByteArrayAsync(token);
        return Json(new { session_id = session });
    }

    private async Task<HttpResponseMessage> FinishBatchAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var batchEntries = document.RootElement.GetProperty("entries");
        LastUploadCommitPaths = batchEntries.EnumerateArray()
            .Select(entry => entry.GetProperty("commit").GetProperty("path").GetString()!).ToList();
        var results = new List<object>();
        foreach (var entry in batchEntries.EnumerateArray())
        {
            var relative = ToRelative(entry.GetProperty("commit").GetProperty("path").GetString()!)!;
            AttemptedWritePaths.Add(relative);
            if (NameWriteErrors.TryGetValue(relative, out var refusal))
            {
                results.Add(new Dictionary<string, object>
                {
                    [".tag"] = "failure",
                    ["failure"] = new Dictionary<string, object>
                    {
                        [".tag"] = "path",
                        [UploadRefusalUsesReason ? "reason" : "path"] = new Dictionary<string, object> { [".tag"] = refusal }
                    }
                });
                continue;
            }
            var bytes = _sessions[entry.GetProperty("cursor").GetProperty("session_id").GetString()!];
            var mode = entry.GetProperty("commit").GetProperty("mode");
            LastUploadMode = mode.ValueKind == JsonValueKind.Object ? "update" : mode.GetString();
            LastUploadExpectedRevision = LastUploadMode == "update" ? mode.GetProperty("update").GetString() : null;
            var exists = _items.TryGetValue(relative, out var current);
            if (LastUploadMode == "update" ? !exists || current!.Rev != mode.GetProperty("update").GetString() : exists)
            {
                results.Add(new Dictionary<string, object>
                {
                    [".tag"] = "failure",
                    ["failure"] = new Dictionary<string, object>
                    {
                        [".tag"] = "path",
                        ["path"] = new Dictionary<string, object> { [".tag"] = "conflict" }
                    }
                });
                continue;
            }
            var node = StoreUpload(relative, bytes, entry.GetProperty("commit"));
            var pathDisplay = UploadSuccessPathOverride ?? Display(relative);
            results.Add(new Dictionary<string, object>
            {
                [".tag"] = "success", ["id"] = node.Id, ["rev"] = node.Rev,
                ["size"] = bytes.Length, ["content_hash"] = DropboxContentHash.Compute(bytes),
                ["client_modified"] = node.ClientModified!,
                ["path_display"] = pathDisplay, ["path_lower"] = pathDisplay.ToLowerInvariant()
            });
        }
        // Applied before the move, so the post-commit root read observes the new path.
        if (MoveRootOnFinishBatch) { MoveRootOnFinishBatch = false; RootPath = "/Moved"; }
        return Json(new { entries = results });
    }

    private HttpResponseMessage Download(HttpRequestMessage request)
    {
        using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
        var target = argument.RootElement.GetProperty("path").GetString()!;
        var revision = target.StartsWith("rev:", StringComparison.Ordinal) ? target[4..] : target;
        LastDownloadRevision = revision;
        var node = _history.GetValueOrDefault(revision) ?? _items.Values.FirstOrDefault(item => !item.Folder &&
            string.Equals(item.Rev, revision, StringComparison.Ordinal));
        if (node is null) return NotFound();
        var metadata = FileMetadata(node);
        if (DownloadMetadataIdOverride is { } id) metadata["id"] = id;
        if (DownloadMetadataRevisionOverride is { } headerRevision) metadata["rev"] = headerRevision;
        if (DownloadMetadataHashOverride is { } headerHash) metadata["content_hash"] = headerHash;
        if (DownloadMetadataSizeOverride is { } headerSize) metadata["size"] = headerSize;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(DownloadBodyOverride ?? node.Bytes) };
        response.Headers.TryAddWithoutValidation("Dropbox-API-Result", JsonSerializer.Serialize(metadata));
        return response;
    }

    private async Task<HttpResponseMessage> ListRevisionsAsync(string path, CancellationToken token)
    {
        var inFlight = Interlocked.Increment(ref _revisionsInFlight);
        lock (_revisionLock)
            if (inFlight > _maxConcurrentRevisions) _maxConcurrentRevisions = inFlight;
        try
        {
            if (OnRevisionAsync is { } hook) await hook(token);
            if (UnknownRevisionPathError is { } pathError && ToRelative(path) is { } buriedPath &&
                _buried.TryGetValue(buriedPath, out var buriedNode) && buriedNode.Folder)
                return FolderError("path", pathError);
            if (BuriedRevisions(path) is { } buried) return Json(buried);
            if (RevisionErrorTag is { } error) return error == "not_found" ? NotFound() : Conflict();
            if (RevisionResponseOverride is { } response) return Json(response);
            var relative = ToRelative(path);
            var deleted = relative is not null && _deleted.Contains(relative, StringComparer.OrdinalIgnoreCase);
            return Json(new
            {
                is_deleted = deleted,
                server_deleted = deleted ? RevisionDeletedTime : null,
                entries = RevisionContentHash is { } contentHash
                    ? new object[]
                    {
                        new Dictionary<string, object>
                        {
                            [".tag"] = "file",
                            ["content_hash"] = contentHash,
                            ["server_modified"] = "2026-10-12T07:00:00Z"
                        }
                    }
                    : Array.Empty<object>()
            });
        }
        finally { Interlocked.Decrement(ref _revisionsInFlight); }
    }

    private Node? FindNode(string path)
    {
        var relative = ToRelative(path);
        if (relative is not null) return _items.TryGetValue(relative, out var byPath) ? byPath : null;
        return path.StartsWith("id:", StringComparison.Ordinal)
            ? _items.Values.FirstOrDefault(item => string.Equals(item.Id, path, StringComparison.Ordinal))
            : null;
    }

    public List<string?> UploadClientModified { get; } = new();
    public string? UploadModifiedOverride { get; set; }

    private Node StoreUpload(string relative, byte[] bytes, JsonElement commit)
    {
        var modified = commit.TryGetProperty("client_modified", out var value) ? value.GetString() : null;
        UploadClientModified.Add(modified);
        var node = Store(relative, bytes) with
        {
            ClientModified = UploadModifiedOverride ?? modified ?? "2026-10-01T00:00:00Z"
        };
        _items[relative] = node;
        return node;
    }

    private async Task<HttpResponseMessage> FinishSessionAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
        var cursor = argument.RootElement.GetProperty("cursor");
        var staged = _sessions[cursor.GetProperty("session_id").GetString()!];
        if (cursor.GetProperty("offset").GetInt64() != staged.Length) return Conflict();
        var tail = await request.Content!.ReadAsByteArrayAsync(token);
        var commit = argument.RootElement.GetProperty("commit");
        var relative = ToRelative(commit.GetProperty("path").GetString()!)!;
        if (_items.ContainsKey(relative)) return Conflict();
        var node = StoreUpload(relative, staged.Concat(tail).ToArray(), commit);
        return Json(FileMetadata(node));
    }

    private Node Store(string relative, byte[] bytes)
    {
        var node = NewNode(relative, bytes, false, "2026-10-01T00:00:00Z");
        if (_items.TryGetValue(relative, out var current) && !current.Folder)
            node = node with { Id = current.Id };
        _items[relative] = node;
        return node;
    }

    private void Rebase(string from, string to)
    {
        var prefix = from + "/";
        foreach (var key in _items.Keys.Where(key => key.Equals(from, StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var node = _items[key];
            _items.Remove(key);
            var moved = to + node.Relative[from.Length..];
            _items[moved] = node with { Relative = moved };
        }
    }

    private void RemoveSubtree(string relative)
    {
        var prefix = relative + "/";
        foreach (var key in _items.Keys.Where(key => key.Equals(relative, StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            _items.Remove(key);
    }

    private Dictionary<string, object> Metadata(Node node) =>
        node.Folder ? FolderMetadata(node.Relative, node.Id) : FileMetadata(node);

    private Dictionary<string, object> FileMetadata(Node node) => new()
    {
        [".tag"] = "file",
        ["id"] = node.Id,
        ["rev"] = node.Rev,
        ["size"] = node.Bytes.Length,
        ["path_display"] = Display(node.Relative),
        ["path_lower"] = Display(node.Relative).ToLowerInvariant(),
        ["content_hash"] = DropboxContentHash.Compute(node.Bytes),
        ["client_modified"] = node.ClientModified ?? "2026-10-01T00:00:00Z",
        ["server_modified"] = node.ClientModified ?? "2026-10-01T00:00:00Z",
        ["is_downloadable"] = true
    };

    private Dictionary<string, object> FolderMetadata(string relative, string id) => new()
    {
        [".tag"] = "folder",
        ["id"] = id,
        ["name"] = relative.Length == 0 ? "Pilot" : Name(relative),
        ["path_display"] = relative.Length == 0 ? RootPath : Display(relative),
        ["path_lower"] = (relative.Length == 0 ? RootPath : Display(relative)).ToLowerInvariant()
    };
}
