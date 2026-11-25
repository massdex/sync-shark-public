using static Massdex.SyncShark.Engine.Dropbox.DropboxProtocol;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Observes the saved job once. Checking a token cannot resubmit a member.</summary>
public sealed class FolderJobs
{
    private static (string Launch, string Check, ActionKind Operation) RoutesFor(string route) => route switch
    {
        CreateFolderBatch or CreateFolderBatchCheck => (CreateFolderBatch, CreateFolderBatchCheck, ActionKind.EnsureDropboxFolder),
        MoveBatch or MoveBatchCheck => (MoveBatch, MoveBatchCheck, ActionKind.MoveDropbox),
        DeleteBatch or DeleteBatchCheck => (DeleteBatch, DeleteBatchCheck, ActionKind.DeleteDropbox),
        _ => throw new InvalidDataException(nameof(route))
    };

    internal static ActionKind Operation(string route) => RoutesFor(route).Operation;

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
            try { await DropboxHttp.ThrowFailureAsync(api, response, routes.Check, ct, job.Launch[0].Path).ConfigureAwait(false); }
            catch (ItemFailedException failure) when (failure.Cause == TextKey.FailDropboxDeleteJobInvalid) { return new JobReply.Invalid(); }
        }
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await DropboxReply.ReadAsync(body, DropboxReplyShapes.Job(pair, job with { Route = routes.Launch }), ct).ConfigureAwait(false);
    }
}
