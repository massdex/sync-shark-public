namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Observes the saved job once. Checking a token cannot resubmit a member.</summary>
public sealed class FolderJobs
{
    /// <summary>Selects only read routes. An unrecognized persisted route can never issue a write.</summary>
    public static string CheckRoute(string route) => RoutesFor(route).Check;

    internal const string CreateCheck = "files/create_folder_batch/check";

    private static (string Launch, string Check) RoutesFor(string route) => route switch
    {
        "files/create_folder_batch" or CreateCheck => ("files/create_folder_batch", CreateCheck),
        "files/move_batch_v2" or "files/move_batch/check_v2" => ("files/move_batch_v2", "files/move_batch/check_v2"),
        "files/delete_batch" or "files/delete_batch/check" => ("files/delete_batch", "files/delete_batch/check"),
        _ => throw new InvalidDataException(nameof(route))
    };

    public async Task<JobReply> ReadAsync(DropboxApi api, PairConfig pair, AcceptedJob job, CancellationToken ct)
    {
        if (pair.PairId != job.PairId || string.IsNullOrWhiteSpace(job.JobId) || job.Launch.Count == 0)
            throw new InvalidDataException(nameof(job));
        var routes = RoutesFor(job.Route);
        using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(routes.Check))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { async_job_id = job.JobId })
        };
        using var response = await api.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            try { await api.ThrowFailureAsync(response, routes.Check, ct, job.Launch[0].Path).ConfigureAwait(false); }
            catch (ItemFailedException failure) when (failure.Cause == TextKey.FailDropboxDeleteJobInvalid) { return new JobReply.Invalid(); }
        }
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await DropboxReply.ReadAsync(body, DropboxReplyShapes.Job(pair, job with { Route = routes.Launch }), ct).ConfigureAwait(false);
    }
}
