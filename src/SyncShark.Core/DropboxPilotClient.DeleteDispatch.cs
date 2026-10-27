using System.Text.Json;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

// Request-time preparation for the existing conditional file-delete queue.
public sealed partial class DropboxPilotClient
{
    private static async Task<AbsenceObservation> RevalidateDeleteAsync(PendingDropboxDelete item)
    {
        if (item.Context?.RevalidateDelete is not { } revalidate) return new(null, "No delete authority recheck was supplied.");
        try { return await revalidate().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            item.WaitTiming?.Dispose();
            item.Result.TrySetException(error);
            return new(null, null);
        }
    }

    private static async Task RefuseDeleteAsync(PendingDropboxDelete item, AbsenceObservation observed)
    {
        try
        {
            var result = item.Context?.RefuseDelete is { } refuse ? await refuse(observed).ConfigureAwait(false) : null;
            if (result is null) throw new InvalidOperationException("The refused delete has no guard result.");
            item.Result.TrySetException(new DeleteSourceUnconfirmedException(result));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { item.Result.TrySetException(error); }
        finally { item.WaitTiming?.Dispose(); }
    }

    private async Task<JsonDocument> LaunchDeleteBatchAsync(List<PendingDropboxDelete> batch,
        V2Settings settings, List<Func<Task>> refusals, Action onSending)
    {
        // The explicit, entry-scaled timeout is armed only when the launch request actually leaves (past token
        // resolution and the pacing wait), so a submission is never cut early; the transport client's timeout is above it.
        using var operation = new CancellationTokenSource();
        var timeout = settings.BatchOperationTimeoutFor(batch.Count, settings.DeleteBatchSize);
        try
        {
            using var request = await AuthorizedAsync(HttpMethod.Post,
                "https://api.dropboxapi.com/2/files/delete_batch", operation.Token);
            return await SendConditionalEffectAsync(request, operation.Token, () =>
                {
                    onSending();
                    operation.CancelAfter(timeout);
                    foreach (var collector in batch.Where(item => item.WriteOperationRetries > 0)
                        .Select(item => item.Instrumentation).Distinct()) collector?.RecordRetry();
                }, () => PrepareDeleteBatchAsync(request, batch, refusals));
        }
        catch (DropboxEffectUncertainException)
        {
            // Includes HTTP 429: acceptance is unknown, so lower admission but NEVER replay the launch.
            DeleteJobs.Throttled();
            foreach (var item in batch) item.ThrottleCount++;
            throw;
        }
        catch (DropboxApiException error)
        {
            if (error.WriteErrorTag == "too_many_write_operations")
            {
                DeleteJobs.Throttled();
                foreach (var item in batch) item.ThrottleCount++;
            }
            foreach (var item in batch)
                if (item.Context is { } context) await context.RecordAsync("delete-rejected", item.Expected.Id!);
            throw;
        }
    }

    private async Task PrepareDeleteBatchAsync(HttpRequestMessage request, List<PendingDropboxDelete> batch,
        List<Func<Task>> refusals)
    {
        // This callback runs after token resolution and transport pacing. Keep rejected
        // members out of both the durable submission and its position-indexed payload.
        var admitted = new List<PendingDropboxDelete>(batch.Count);
        foreach (var page in batch.Chunk(128))
        {
            var observations = await Task.WhenAll(page.Select(RevalidateDeleteAsync)).ConfigureAwait(false);
            for (var i = 0; i < page.Length; i++)
            {
                var item = page[i];
                var observed = observations[i];
                if (observed.Absent == true && !item.Result.Task.IsCompleted) admitted.Add(item);
                else if (!item.Result.Task.IsCompleted)
                    refusals.Add(() => RefuseDeleteAsync(item, observed));
            }
        }
        batch.Clear();
        batch.AddRange(admitted);
        if (batch.Count == 0) throw new OperationCanceledException("No delete members remain admitted.");
        var submissionId = Guid.NewGuid().ToString("N");
        await Task.WhenAll(batch.Select(item => item.Context?.RecordAsync("delete-submit", submissionId)
            ?? Task.CompletedTask));
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            entries = batch.Select(item => new
            {
                path = RequiredRootId() + "/" + PilotPath.NormalizeDropboxRelative(item.Expected.RelativePath),
                parent_rev = item.Expected.Revision
            }).ToArray()
        }, JsonOptions), System.Text.Encoding.UTF8, "application/json");
        foreach (var instrumentation in batch.Select(item => item.Instrumentation).Distinct())
            instrumentation?.RecordBatch(batch.Count);
    }
}
