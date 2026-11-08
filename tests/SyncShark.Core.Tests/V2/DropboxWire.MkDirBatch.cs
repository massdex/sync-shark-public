using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Scripted folder-create batches, including an existing folder and asynchronous completion.</summary>
internal sealed partial class DropboxWire
{
    private object[]? _mkdirJobEntries;

    public Dictionary<string, int> MkDirWriteRefusals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<int> MkDirBatchSizes { get; } = [];

    /// <summary>The path argument of every files/get_metadata request, in order.</summary>
    public List<string> MetadataPaths { get; } = [];
    public TaskCompletionSource? MkDirBatchEntered { get; set; }
    public TaskCompletionSource? ReleaseMkDirBatch { get; set; }
    public bool MkDirBatchAsyncJob { get; set; }
    public bool MkDirBatchNestedShape { get; set; }

    /// <summary>Dropbox returns an untagged FolderMetadata struct inside a create-batch success entry.</summary>
    public bool MkDirSuccessUntaggedMetadata { get; set; }
    public int MkDirBatchPendingChecks { get; set; }
    public int LastMkDirBatchSize { get; private set; }
    public List<string> LastMkDirBatchPaths { get; private set; } = [];
    public string? MkDirSuccessPathOverride { get; set; }

    /// <summary>Root-id metadata reads after this many are held on <see cref="RootMetadataGate"/>.</summary>
    public int RootMetadataHoldAfter { get; set; } = int.MaxValue;
    public Task? RootMetadataGate { get; set; }
    private int _rootMetadataReads;

    private async Task<HttpResponseMessage> CreateFolderBatchAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var paths = document.RootElement.GetProperty("paths");
        LastMkDirBatchPaths = paths.EnumerateArray().Select(path => path.GetString()!).ToList();
        LastMkDirBatchSize = paths.GetArrayLength();
        MkDirBatchSizes.Add(LastMkDirBatchSize);
        MkDirBatchEntered?.TrySetResult();
        if (ReleaseMkDirBatch is { } release) await release.Task.WaitAsync(ct);
        if (LastMkDirBatchSize is < 1 or > 1000) return Error(HttpStatusCode.BadRequest);
        var results = new List<object>();
        foreach (var element in paths.EnumerateArray())
        {
            var relative = ToRelative(element.GetString()!);
            if (relative is null || relative.Length == 0)
            {
                results.Add(Refusal("malformed_path"));
                continue;
            }
            AttemptedWritePaths.Add(relative);
            if (NameWriteErrors.TryGetValue(relative, out var refusedName))
            {
                results.Add(Refusal(refusedName));
                continue;
            }
            if (MkDirWriteRefusals.TryGetValue(relative, out var refusals) && refusals > 0)
            {
                MkDirWriteRefusals[relative] = refusals - 1;
                results.Add(Refusal("too_many_write_operations"));
                continue;
            }
            if (_items.TryGetValue(relative, out var occupant))
            {
                // Already a folder is the same result as a create; a file in the way is a per-entry failure.
                results.Add(Refusal(occupant.Folder ? "conflict/folder" : "conflict/file"));
                continue;
            }
            var parent = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            if (parent.Length > 0 && (!_items.TryGetValue(parent, out var parentNode) || !parentNode.Folder))
            {
                results.Add(Refusal("other"));
                continue;
            }
            var node = NewNode(relative, Array.Empty<byte>(), true, null);
            _items[relative] = node;
            results.Add(Success(node));
        }
        _mkdirJobEntries = results.ToArray();
        return MkDirBatchAsyncJob
            ? Json(new Dictionary<string, object> { [".tag"] = "async_job_id", ["async_job_id"] = "mkdir-job-1" })
            : CompletedMkDirBatch();
    }

    // The union keyed by its variant, or Dropbox's flat {".tag":"success", "metadata":{...}} spelling.
    private object Success(Node node)
    {
        var metadata = Metadata(node);
        // Dropbox's create_folder_batch success metadata is a FolderMetadata struct, not the Metadata union:
        // the entry itself carries ".tag":"success" and the nested metadata carries no ".tag" at all.
        if (MkDirSuccessUntaggedMetadata) metadata.Remove(".tag");
        if (MkDirSuccessPathOverride is { } overridden)
        {
            metadata["path_display"] = overridden;
            metadata["path_lower"] = overridden.ToLowerInvariant();
        }
        return MkDirBatchNestedShape
            ? new Dictionary<string, object>
            {
                ["success"] = new Dictionary<string, object> { ["metadata"] = metadata }
            }
            : new Dictionary<string, object> { [".tag"] = "success", ["metadata"] = metadata };
    }

    // A per-entry refusal: a CreateFolderEntryError.path -> WriteError, with an optional WriteConflictError sub-tag.
    private static object Refusal(string reason)
    {
        var slash = reason.IndexOf('/');
        var write = slash < 0
            ? new Dictionary<string, object> { [".tag"] = reason }
            : new Dictionary<string, object>
            {
                [".tag"] = "conflict",
                ["conflict"] = new Dictionary<string, object> { [".tag"] = reason[(slash + 1)..] }
            };
        return new Dictionary<string, object>
        {
            [".tag"] = "failure",
            ["failure"] = new Dictionary<string, object>
            {
                [".tag"] = "path",
                ["path"] = write
            }
        };
    }

    private HttpResponseMessage CheckCreateFolderBatch()
    {
        if (MkDirBatchPendingChecks-- > 0)
            return Json(new Dictionary<string, object> { [".tag"] = "in_progress" });
        return CompletedMkDirBatch();
    }

    private HttpResponseMessage CompletedMkDirBatch() => Json(new Dictionary<string, object>
    {
        [".tag"] = "complete", ["entries"] = _mkdirJobEntries ?? []
    });
}
