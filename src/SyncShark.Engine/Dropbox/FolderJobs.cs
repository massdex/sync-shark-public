using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Observes the saved job once. Checking a token cannot resubmit a member.</summary>
public sealed class FolderJobs
{
    /// <summary>Selects only read routes. An unrecognized persisted route can never issue a write.</summary>
    public static string CheckRoute(string route) => RoutesFor(route).Check;

    private static (string Launch, string Check) RoutesFor(string route) => route switch
    {
        "files/create_folder_batch" or "files/create_folder_batch/check" => ("files/create_folder_batch", "files/create_folder_batch/check"),
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
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using var error = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            var failure = new DropboxErrors().Classify(response.StatusCode, error.RootElement, routes.Check, false,
                new(Read: true, Dispatched: true, ProvenNoEffect: true, Transient: false));
            if (failure.Cause == TextKey.FailDropboxDeleteJobInvalid) return new JobReply.Invalid();
            throw new ItemFailedException(job.Launch[0].Path, failure.Cause, null) { Disposition = failure.Disposition };
        }
        return await DropboxReply.ReadAsync(body, DropboxReplyShapes.Job(pair, job with { Route = routes.Launch }), ct).ConfigureAwait(false);
    }
}
