using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checking a saved launch must select its observation route, never a mutation.</summary>
public static class FolderJobsChecks
{
    public static async Task RunAsync()
    {
        foreach (var (launch, check) in new[] { ("files/move_batch_v2", "files/move_batch/check_v2"),
            ("files/create_folder_batch", "files/create_folder_batch/check"), ("files/delete_batch", "files/delete_batch/check") })
            Check.Require(FolderJobs.CheckRoute(launch) == check && FolderJobs.CheckRoute(check) == check, "Wrong job check route");
        var pair = ItemChecks.Pair();
        var job = new AcceptedJob(pair.PairId, "files/upload", "token", [new(null, "folder", null)]);
        foreach (var invalid in new[] { job, job with { PairId = "other", Route = "files/delete_batch" } })
        {
            try { await new FolderJobs().ReadAsync(null!, pair, invalid, default); }
            catch (InvalidDataException) { continue; }
            throw new InvalidDataException("An unsupported or cross-pair job reached the provider");
        }
        var calls = DropboxSideChecks.CallsIn(nameof(FolderJobs), nameof(FolderJobs.ReadAsync));
        Check.Require(calls.Contains("api.SendAsync") && calls.Any(c => c.EndsWith(".Classify", StringComparison.Ordinal)),
            "Job observation bypassed the structured HTTP status/body classifier");
        DropboxSideChecks.RequireReadFailureHandoff(calls);
    }

    /// <summary>F1: a rejected check cannot consume the saved job, and the next trigger checks the very same token.</summary>
    public static async Task InvalidTokenAsync()
    {
        var pair = ItemChecks.Pair();
        var job = new AcceptedJob(pair.PairId, "files/delete_batch", "saved-token", [new("id:selected", "item", null)]);
        string[] replies = ["""{"error":{".tag":"invalid_async_job_id"}}""", """{"error":{".tag":"other"},"error_summary":"invalid_async_job_id/"}""", "{", """{".tag":"in_progress"}"""];
        var calls = 0;
        using var http = new HttpClient(new DropboxSideChecks.ReplyWire(async (request, ct) =>
        {
            Check.Require(request.RequestUri!.AbsolutePath.EndsWith("/files/delete_batch/check", StringComparison.Ordinal), "Job re-sent a mutation");
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Check.Require(body.RootElement.GetProperty("async_job_id").GetString() == job.JobId, "Job check used another token");
            if (calls++ == 0) return DropboxSideChecks.Throttled();
            var index = calls - 2;
            return new HttpResponseMessage(index == replies.Length - 1 ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.Conflict)
                { Content = new StringContent(replies[index]) };
        }));
        var jobs = new FolderJobs(); var api = DropboxSideChecks.BoundApi(http);
        await DropboxSideChecks.ExpectThrottleAsync(async () => { await jobs.ReadAsync(api, pair, job, default); });
        Check.Require(await jobs.ReadAsync(api, pair, job, default) is JobReply.Invalid, "Exact structured invalid token was not Invalid");
        foreach (var _ in replies.Skip(1).Take(2))
        {
            try { await jobs.ReadAsync(api, pair, job, default); }
            catch (Exception e) when (e is ItemFailedException or System.Text.Json.JsonException) { continue; }
            throw new InvalidDataException("Unrelated or malformed 409 was accepted as a job outcome");
        }
        Check.Require(await jobs.ReadAsync(api, pair, job, default) is JobReply.Running && calls == replies.Length + 1,
            "A later trigger could not check the same saved job");
    }
}
