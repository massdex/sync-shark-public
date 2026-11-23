using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checking a saved launch must select its observation route, never a mutation.</summary>
public static class FolderJobsChecks
{
    public static async Task RunAsync()
    {
        var pair = ItemChecks.Pair();
        var job = new AcceptedJob(pair.PairId, "files/upload", "token", [new(null, "folder", null)]);
        foreach (var invalid in new[] { job, job with { PairId = "other", Route = "files/delete_batch" } })
        {
            try { await new FolderJobs().ReadAsync(null!, pair, invalid, default); }
            catch (InvalidDataException) { continue; }
            throw new InvalidDataException("An unsupported or cross-pair job reached the provider");
        }
    }

    /// <summary>F1: a rejected check cannot consume the saved job, and the next trigger checks the very same token.</summary>
    public static async Task InvalidTokenAsync()
    {
        var pair = ItemChecks.Pair();
        var failures = new List<string>();
        foreach (var (launch, check) in new[] { ("files/move_batch_v2", "files/move_batch/check_v2"),
            ("files/create_folder_batch", "files/create_folder_batch/check"), ("files/delete_batch", "files/delete_batch/check") })
        foreach (var savedRoute in new[] { launch, check })
        {
            var saved = new AcceptedJob(pair.PairId, savedRoute, "token-" + savedRoute, [new(null, "item", null)]);
            var observations = new List<(string? Path,string? Token)>();
            using var client = new HttpClient(new DropboxSideChecks.ReplyWire(async (request, ct) =>
            {
                using var args = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
                observations.Add((request.RequestUri?.AbsolutePath,args.RootElement.GetProperty("async_job_id").GetString()));
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{".tag":"in_progress"}""") };
            }));
            var observed = await new FolderJobs().ReadAsync(DropboxSideChecks.BoundApi(client,TimeProvider.System), pair, saved, default);
            if (observations.Count != 1) failures.Add($"Saved {savedRoute} job was not checked exactly once");
            else if (observations[0] != ("/2/" + check,saved.JobId)) failures.Add($"Saved {savedRoute} job used the wrong check route or token");
            if (observed is not JobReply.Running) failures.Add($"Lost running status for {savedRoute}");
        }
        Check.Require(failures.Count == 0, string.Join("; ", failures));
        var job = new AcceptedJob(pair.PairId, "files/delete_batch", "saved-token", [new("id:selected", "item", null)]);
        string[] replies = ["""{"error":{".tag":"invalid_async_job_id"}}""", """{"error":{".tag":"other"},"error_summary":"invalid_async_job_id/"}""", "{", """{".tag":"in_progress"}"""];
        var calls = 0;
        using var http = new HttpClient(new DropboxSideChecks.ReplyWire(async (request, ct) =>
        {
            Check.Require(request.RequestUri!.AbsolutePath.EndsWith("/files/delete_batch/check", StringComparison.Ordinal), "Job re-sent a mutation");
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Check.Require(body.RootElement.GetProperty("async_job_id").GetString() == job.JobId, "Job check used another token");
            if (++calls is 1 or 2)
            {
                var busy = DropboxSideChecks.Throttled();
                busy.Content = calls == 1 ? new StringContent("""{"error":{"retry_after":29}}""") : new BrokenBody();
                return busy;
            }
            var index = calls - 3;
            return new HttpResponseMessage(index == replies.Length - 1 ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.Conflict)
                { Content = new StringContent(replies[index]) };
        }));
        var clock = new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var jobs = new FolderJobs(); var api = DropboxSideChecks.BoundApi(http, clock);
        await DropboxSideChecks.ExpectThrottleAsync(async () => { await jobs.ReadAsync(api, pair, job, default); }, clock, 29);
        await DropboxSideChecks.ExpectThrottleAsync(async () => { await jobs.ReadAsync(api, pair, job, default); }, clock);
        Check.Require(await jobs.ReadAsync(api, pair, job, default) is JobReply.Invalid, "Exact structured invalid token was not Invalid");
        foreach (var _ in replies.Skip(1).Take(2))
        {
            try { await jobs.ReadAsync(api, pair, job, default); }
            catch (Exception e) when (e is ItemFailedException or System.Text.Json.JsonException) { continue; }
            throw new InvalidDataException("Unrelated or malformed 409 was accepted as a job outcome");
        }
        Check.Require(await jobs.ReadAsync(api, pair, job, default) is JobReply.Running && calls == replies.Length + 2,
            "A later trigger could not check the same saved job");
    }

    private sealed class BrokenBody : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            Task.FromException(new IOException("Interrupted refusal body"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
