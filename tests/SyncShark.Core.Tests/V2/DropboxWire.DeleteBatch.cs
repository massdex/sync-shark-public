using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Scripted conditional delete batches, including one racing revision and asynchronous completion.</summary>
internal sealed partial class DropboxWire
{
    private object[]? _deleteJobEntries;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object[]> _deleteJobs = new(StringComparer.Ordinal);

    public Dictionary<string, int> DeleteWriteRefusals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<int> DeleteBatchSizes { get; } = [];
    public TaskCompletionSource? DeleteBatchEntered { get; set; }
    public TaskCompletionSource? ReleaseDeleteBatch { get; set; }
    public string? RaceDeleteBatchPath { get; set; }
    public bool DeleteBatchAsyncJob { get; set; }
    public int DeleteBatchPendingChecks { get; set; }
    public string? DeleteJobTerminal { get; set; }
    // Folder-delete concurrency check pauses a status read while an unrelated upload commits.
    public Func<Task>? BeforeDeleteJobCheck { get; set; }
    public bool RefuseFolderDelete { get; set; }
    public bool FailFolderResultAfterDelete { get; set; }
    public int LastDeleteBatchSize { get; private set; }
    public List<string> LastDeleteBatchPaths { get; private set; } = [];

    private async Task<HttpResponseMessage> DeleteBatchAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var entries = document.RootElement.GetProperty("entries");
        LastDeleteBatchPaths = entries.EnumerateArray().Select(entry => entry.GetProperty("path").GetString()!).ToList();
        LastDeleteBatchSize = entries.GetArrayLength();
        DeleteBatchSizes.Add(LastDeleteBatchSize);
        DeleteBatchEntered?.TrySetResult();
        if (ReleaseDeleteBatch is { } release) await release.Task.WaitAsync(ct);
        if (LastDeleteBatchSize is < 1 or > 1000) return Error(HttpStatusCode.BadRequest);
        var results = new List<object>();
        foreach (var entry in entries.EnumerateArray())
        {
            var node = FindNode(entry.GetProperty("path").GetString()!);
            if (!entry.TryGetProperty("parent_rev", out var parentRev))
            {
                // a folder entry carries no parent_rev and removes the whole subtree.
                LastDeleteParentRev = null;
                results.Add(node is { Folder: true } && !RefuseFolderDelete
                    ? new Dictionary<string, object> { [".tag"] = FailFolderResultAfterDelete ? "failure" : "success", ["metadata"] = BuryFolder(node) }
                    : new Dictionary<string, object>
                    {
                        [".tag"] = "failure", ["failure"] = new Dictionary<string, object> { [".tag"] = "path_lookup" }
                    });
                continue;
            }
            LastDeleteParentRev = parentRev.GetString();
            if (node is not null && DeleteWriteRefusals.TryGetValue(node.Relative, out var refusals) && refusals > 0)
            {
                DeleteWriteRefusals[node.Relative] = refusals - 1;
                results.Add(new Dictionary<string, object>
                {
                    [".tag"] = "failure", ["failure"] = new Dictionary<string, object> { [".tag"] = "too_many_write_operations" }
                });
                continue;
            }
            if (node is not null && string.Equals(node.Relative, RaceDeleteBatchPath, StringComparison.OrdinalIgnoreCase))
            {
                node = node with { Rev = node.Rev + "-raced" };
                _items[node.Relative] = node;
                RaceDeleteBatchPath = null;
            }
            if (node is null || node.Folder || !string.Equals(node.Rev, LastDeleteParentRev, StringComparison.Ordinal))
            {
                results.Add(new Dictionary<string, object>
                {
                    [".tag"] = "failure", ["failure"] = new Dictionary<string, object> { [".tag"] = "path_lookup" }
                });
                continue;
            }
            RemoveSubtree(node.Relative);
            results.Add(new Dictionary<string, object>
            {
                [".tag"] = "success", ["metadata"] = Metadata(node)
            });
        }
        _deleteJobEntries = results.ToArray();
        var jobId = "delete-job-" + (_deleteJobs.Count + 1);
        _deleteJobs[jobId] = _deleteJobEntries;
        return DeleteBatchAsyncJob
            ? Json(new Dictionary<string, object> { [".tag"] = "async_job_id", ["async_job_id"] = jobId })
            : CompletedDeleteBatch();
    }

    private async Task<HttpResponseMessage> CheckDeleteBatchAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var jobId = document.RootElement.GetProperty("async_job_id").GetString()!;
        if (BeforeDeleteJobCheck is { } before) await before();
        if (DeleteJobTerminal is { } terminal)
            return terminal == "failed"
                ? Json(new Dictionary<string, object> { [".tag"] = terminal })
                : FolderError(terminal);
        if (DeleteBatchPendingChecks-- > 0)
            return Json(new Dictionary<string, object> { [".tag"] = "in_progress" });
        return Json(new Dictionary<string, object>
        {
            [".tag"] = "complete", ["entries"] = _deleteJobs.GetValueOrDefault(jobId) ?? _deleteJobEntries ?? []
        });
    }

    private HttpResponseMessage CompletedDeleteBatch() => Json(new Dictionary<string, object>
    {
        [".tag"] = "complete", ["entries"] = _deleteJobEntries ?? []
    });
}
