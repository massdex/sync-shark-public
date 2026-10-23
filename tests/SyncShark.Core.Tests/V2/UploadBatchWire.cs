using System.Net;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Scripted upload transport with a held first commit and per-entry or acknowledgement faults.</summary>
internal sealed class UploadBatchWire : HttpMessageHandler
{
    internal readonly List<int> BatchSizes = [];
    internal readonly TaskCompletionSource FirstCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource ReleaseCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource AllStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int ExpectedStarts;
    internal TimeSpan StartLatency, FinishLatency;
    internal bool HoldFirstCommit, HoldAllCommits, RejectOne, LoseAcknowledgement, NestedPathConflict;
    internal int ExpectedConcurrentFinishes;
    internal readonly TaskCompletionSource ConcurrentFinishesReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Starts, Finishes, ActiveFinishes, PeakFinishes;
    // Session starts are counted in flight so a test can prove the namespace cap is never exceeded.
    internal int ActiveStarts, PeakStarts;
    internal bool SequentialClosed = true;
    // The account and namespaces this wire reports; a test can point a client at a private namespace so its
    // gate binding is independent of the fixed default binding other checks share.
    internal string AccountId = "account-id";
    internal string RootNamespaceId = "root-namespace";
    internal string HomeNamespaceId = "home-namespace";
    internal HttpStatusCode? FinishStatus;
    internal Exception? FinishFault;
    internal Func<int, CancellationToken, Task<HttpResponseMessage?>>? StartResponse;
    internal int WriteRejectionFinishes;
    internal int ApplyBeforeLostAcknowledgement;
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> AppliedPaths = new();
    private int _sessions;
    private readonly string _hash;
    internal UploadBatchWire(string hash) => _hash = hash;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route == "/2/users/get_current_account")
            return Json(new { account_id = AccountId, root_info = new { root_namespace_id = RootNamespaceId, home_namespace_id = HomeNamespaceId } });
        if (route == "/2/files/get_metadata")
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var requestedPath = body.RootElement.GetProperty("path").GetString()!;
            if (AppliedPaths.TryGetValue(requestedPath, out var appliedSession))
                return Json(new Dictionary<string, object> { [".tag"] = "file", ["id"] = "id:" + appliedSession,
                    ["name"] = requestedPath.Split('/').Last(), ["path_display"] = requestedPath.Replace(DropboxWire.RootId, "/Pilot"),
                    ["path_lower"] = requestedPath.Replace(DropboxWire.RootId, "/Pilot").ToLowerInvariant(),
                    ["rev"] = "r1", ["size"] = 16, ["content_hash"] = _hash,
                    ["client_modified"] = "2026-10-01T00:00:00Z", ["server_modified"] = "2026-10-01T00:00:00Z" });
            if (requestedPath is not (DropboxWire.RootId or "/Pilot"))
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}") };
            return Json(new Dictionary<string, object> { [".tag"] = "folder", ["id"] = DropboxWire.RootId, ["name"] = "Pilot", ["path_display"] = "/Pilot", ["path_lower"] = "/pilot" });
        }
        if (route == "/2/files/upload_session/start")
        {
            using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            SequentialClosed &= argument.RootElement.GetProperty("close").GetBoolean() &&
                argument.RootElement.GetProperty("session_type").GetProperty(".tag").GetString() == "sequential";
            await request.Content!.ReadAsByteArrayAsync(ct);
            var activeStart = Interlocked.Increment(ref ActiveStarts);
            lock (BatchSizes) PeakStarts = Math.Max(PeakStarts, activeStart);
            try
            {
                if (StartLatency > TimeSpan.Zero) await Task.Delay(StartLatency, ct);
                var start = Interlocked.Increment(ref Starts);
                if (start == ExpectedStarts) AllStarted.TrySetResult();
                if (StartResponse is { } scripted && await scripted(start, ct) is { } response) return response;
                return Json(new { session_id = "s" + Interlocked.Increment(ref _sessions) });
            }
            finally { Interlocked.Decrement(ref ActiveStarts); }
        }
        if (route != "/2/files/upload_session/finish_batch_v2") throw new InvalidOperationException(route);
        var finish = Interlocked.Increment(ref Finishes);
        var active = Interlocked.Increment(ref ActiveFinishes);
        lock (BatchSizes) PeakFinishes = Math.Max(PeakFinishes, active);
        if (active == ExpectedConcurrentFinishes) ConcurrentFinishesReached.TrySetResult();
        try
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var entries = body.RootElement.GetProperty("entries");
            lock (BatchSizes) BatchSizes.Add(entries.GetArrayLength());
            FirstCommit.TrySetResult();
            if (HoldAllCommits || finish == 1 && HoldFirstCommit) await ReleaseCommit.Task.WaitAsync(ct);
            if (FinishLatency > TimeSpan.Zero) await Task.Delay(FinishLatency, ct);
            if (LoseAcknowledgement)
            {
                foreach (var entry in entries.EnumerateArray().Take(ApplyBeforeLostAcknowledgement))
                {
                    var path = entry.GetProperty("commit").GetProperty("path").GetString()!;
                    // Commits use the verified namespace path; metadata reads use the same root's ID.
                    if (path.StartsWith("/Pilot/", StringComparison.OrdinalIgnoreCase))
                        path = DropboxWire.RootId + path["/Pilot".Length..];
                    AppliedPaths[path] = entry.GetProperty("cursor").GetProperty("session_id").GetString()!;
                }
                throw new HttpRequestException("scripted lost acknowledgement");
            }
            if (FinishFault is { } fault) throw fault;
            if (FinishStatus is { } status) return new HttpResponseMessage(status);
            var result = entries.EnumerateArray().Select((entry, i) =>
            {
                var commit = entry.GetProperty("commit");
                if (commit.GetProperty("autorename").GetBoolean() || !commit.GetProperty("strict_conflict").GetBoolean())
                    throw new InvalidOperationException("unsafe commit flags");
                if (finish <= WriteRejectionFinishes && i == 0) return (object)new Dictionary<string, object>
                { [".tag"] = "failure", ["failure"] = new Dictionary<string, object> { [".tag"] = "too_many_write_operations" } };
                if (RejectOne && i == 0) return (object)new Dictionary<string, object>
                {
                    [".tag"] = "failure",
                    ["failure"] = NestedPathConflict
                        ? new Dictionary<string, object>
                        {
                            [".tag"] = "path",
                            ["path"] = new Dictionary<string, object>
                            {
                                [".tag"] = "conflict",
                                ["conflict"] = new Dictionary<string, object> { [".tag"] = "file" }
                            }
                        }
                        : new Dictionary<string, object> { [".tag"] = "path" }
                };
                var commitPath = commit.GetProperty("path").GetString()!;
                return new Dictionary<string, object> { [".tag"] = "success", ["id"] = "id:" + entry.GetProperty("cursor").GetProperty("session_id").GetString(),
                    ["rev"] = "r1", ["size"] = 16, ["content_hash"] = _hash,
                    ["client_modified"] = commit.TryGetProperty("client_modified", out var modified)
                        ? modified.GetString()! : "2026-10-01T00:00:00Z",
                    ["server_modified"] = "2026-10-01T00:00:00Z",
                    ["path_display"] = commitPath, ["path_lower"] = commitPath.ToLowerInvariant() };
            }).ToArray();
            return Json(new { entries = result });
        }
        finally { Interlocked.Decrement(ref ActiveFinishes); }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
