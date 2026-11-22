using System.Net;
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
        try
        {
            using var doc = await api.CallJsonAsync(routes.Check, JsonSerializer.Serialize(new { async_job_id = job.JobId }), ct).ConfigureAwait(false);
            return DropboxReplyShapes.Job(doc.RootElement, job with { Route = routes.Launch }, pair);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict && InvalidToken(error.Message))
        {
            return new JobReply.Invalid();
        }
    }

    private static bool InvalidToken(string body)
    {
        try
        {
            using var error = JsonDocument.Parse(body);
            return error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("error", out var reason) &&
                reason.ValueKind == JsonValueKind.Object && reason.TryGetProperty(".tag", out var tag) && tag.ValueKind == JsonValueKind.String &&
                tag.GetString() == "invalid_async_job_id";
        }
        catch (JsonException) { return false; }
    }
}
