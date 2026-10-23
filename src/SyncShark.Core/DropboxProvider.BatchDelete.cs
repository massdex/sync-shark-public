using System.Net;
using System.Text.Json;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>Coalesces conditional file deletes; folder removal stays on the empty-only path.</summary>
public sealed partial class DropboxPilotClient
{
    private const int DeleteBatchMaximumEntries = 1000;
    private readonly object _deleteBatchGate = new();
    private List<PendingDropboxDelete> _deleteBatchPending = [];
    private int _activeDeleteBatches;
    private DropboxDeleteJobGate DeleteJobs => DropboxDeleteJobGate.For(
        expectedConnection.Normalize().ConnectionId, expectedConnection.RootNamespaceId);

    /// <summary>Queues a file whose stable identity, path and revision must still match before submission.</summary>
    internal Task DeleteBatchedFileAsync(PilotRemoteEntry expected, V2Settings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (expected.Kind != PilotEntryKind.File || !IsExactDropboxObjectIdentity(expected.Id) ||
            string.IsNullOrWhiteSpace(expected.Revision) || string.IsNullOrWhiteSpace(expected.ContentHash))
            throw new InvalidDataException("A batch delete requires an exact file identity, revision and hash.");
        PilotPath.NormalizeDropboxRelative(expected.RelativePath);
        if (settings.DeleteBatchSize is < 1 or > DeleteBatchMaximumEntries ||
            settings.DeleteBatchMaxAge <= TimeSpan.Zero || settings.BatchPollInterval <= TimeSpan.Zero ||
            settings.BatchPollTimeout <= TimeSpan.Zero || settings.DropboxWriteOperationRetryCount is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "Dropbox delete batching settings are invalid.");
        var item = new PendingDropboxDelete(expected, ProviderEffectContext.Current.Value, ct);
        List<PendingDropboxDelete>? ready = null, first = null;
        lock (_deleteBatchGate)
        {
            _deleteBatchPending.Add(item);
            if (_deleteBatchPending.Count == 1) first = _deleteBatchPending;
            if (_activeDeleteBatches < DropboxDeleteJobGate.MaximumConcurrency && _deleteBatchPending.Count >= settings.DeleteBatchSize)
                ready = TakeDeleteBatch(settings);
        }
        // An enqueued effect is drained even if the caller cancels. Release only the staging worker;
        // its pending-effect bound and durable intent remain until this individual result is committed.
        SyncEffectStaging.Current.Value?.Release();
        SyncTransferResourceController.Lease.Current.Value?.Complete(true);
        if (ready is not null) _ = DispatchDeleteBatchesAsync(ready, settings);
        else if (first is not null) _ = SubmitDeleteBatchAfterAgeAsync(first, settings);
        return item.Result.Task;
    }

    private async Task SubmitDeleteBatchAfterAgeAsync(List<PendingDropboxDelete> batch, V2Settings settings)
    {
        await Task.Delay(settings.DeleteBatchMaxAge);
        lock (_deleteBatchGate)
        {
            if (!ReferenceEquals(batch, _deleteBatchPending) || _activeDeleteBatches >= DropboxDeleteJobGate.MaximumConcurrency) return;
            batch = TakeDeleteBatch(settings);
        }
        await DispatchDeleteBatchesAsync(batch, settings);
    }

    private List<PendingDropboxDelete> TakeDeleteBatch(V2Settings settings)
    {
        var count = Math.Min(settings.DeleteBatchSize, _deleteBatchPending.Count);
        var batch = _deleteBatchPending.GetRange(0, count);
        _deleteBatchPending = _deleteBatchPending.GetRange(count, _deleteBatchPending.Count - count);
        _activeDeleteBatches++;
        return batch;
    }

    private async Task DispatchDeleteBatchesAsync(List<PendingDropboxDelete> batch, V2Settings settings)
    {
        while (true)
        {
            var jobs = DeleteJobs;
            await jobs.EnterAsync().ConfigureAwait(false);
            var throttles = batch.Sum(item => item.ThrottleCount);
            try { await SubmitDeleteBatchAsync(batch, settings).ConfigureAwait(false); }
            finally { jobs.Exit(batch.Sum(item => item.ThrottleCount) == throttles); }
            lock (_deleteBatchGate)
            {
                _activeDeleteBatches--;
                if (_deleteBatchPending.Count == 0) return;
                batch = TakeDeleteBatch(settings);
            }
        }
    }

    private void RequeueDelete(PendingDropboxDelete item)
    {
        lock (_deleteBatchGate) _deleteBatchPending.Add(item);
    }

    private async Task SubmitDeleteBatchAsync(IReadOnlyList<PendingDropboxDelete> candidates, V2Settings settings)
    {
        IReadOnlyList<PendingDropboxDelete> batch = candidates;
        var submitted = false;
        try
        {
            var gate = DropboxUploadCommitGate.For(expectedConnection.Normalize().ConnectionId,
                expectedConnection.RootNamespaceId, settings.UploadCommitConcurrency);
            await V2.RunOperations.WaitAsync(gate, "upload commit gate").ConfigureAwait(false);
            JsonDocument response;
            var refusals = new List<Func<Task>>();
            try
            {
                // Deletes stay id-rooted by parent_rev binds the file, not its parent, so a
                // path-rooted delete after a root swap could target a replacement folder at the old path.
                await VerifyRootIdentityAsync(CancellationToken.None).ConfigureAwait(false);
                // No per-file get_metadata pre-check: every entry is sent with parent_rev, so Dropbox deletes it only
                // if that exact file revision is still current at that id-rooted path; a changed or replaced file fails
                // its own entry and is never deleted. The pre-check repeated that guarantee at one Dropbox read per
                // file (1,000 per batch), which made batched deletes rate-limit bound (~1 file/s live, 2026-10-17).
                batch = candidates.Where(item => !item.Result.Task.IsCompleted).ToArray();
                // Recheck after batch/job/commit admission waits, before any submit checkpoint.
                // Guard callbacks coalesce these requests through the existing 128-item absence probe.
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
                batch = admitted;
                if (batch.Count == 0) return;
                // Token resolution and request pacing after this checkpoint remain a residual window.
                var submissionId = Guid.NewGuid().ToString("N");
                await Task.WhenAll(batch.Select(item => item.Context?.RecordAsync("delete-submit", submissionId)
                    ?? Task.CompletedTask));
                foreach (var instrumentation in batch.Select(item => item.Instrumentation).Distinct())
                    instrumentation?.RecordBatch(batch.Count);
                submitted = true;
                response = await LaunchDeleteBatchAsync(batch, settings);
            }
            finally
            {
                gate.Release();
                await Task.WhenAll(refusals.Select(refuse => refuse())).ConfigureAwait(false);
            }
            // Polling a server job is a read, not a namespace write. Keep its separate bounded job slot.
            using var ownedResponse = response;
            try { await CompleteDeleteBatchAsync(batch, response.RootElement, settings); }
            catch (Exception exception)
            {
                throw new DropboxEffectUncertainException("The accepted delete batch could not be verified.", exception);
            }
        }
        catch (Exception exception)
        {
            foreach (var item in batch)
            {
                item.WaitTiming?.Dispose();
                item.Result.TrySetException(submitted && exception is not DropboxApiException
                    ? new DropboxEffectUncertainException("The conditional delete batch outcome is unknown.", exception)
                    : exception);
            }
        }
    }

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

    private async Task<JsonDocument> LaunchDeleteBatchAsync(IReadOnlyList<PendingDropboxDelete> batch,
        V2Settings settings)
    {
        // The explicit, entry-scaled timeout is armed only when the launch request actually leaves (past token
        // resolution and the pacing wait), so a submission is never cut early; the transport client's timeout is above it.
        using var operation = new CancellationTokenSource();
        var timeout = settings.BatchOperationTimeoutFor(batch.Count, settings.DeleteBatchSize);
        try
        {
            return await PostConditionalEffectJsonAsync("https://api.dropboxapi.com/2/files/delete_batch",
                new { entries = batch.Select(item => new
                {
                    path = RequiredRootId() + "/" + PilotPath.NormalizeDropboxRelative(item.Expected.RelativePath),
                    parent_rev = item.Expected.Revision
                }).ToArray() }, operation.Token, () =>
                {
                    operation.CancelAfter(timeout);
                    foreach (var collector in batch.Where(item => item.WriteOperationRetries > 0)
                        .Select(item => item.Instrumentation).Distinct()) collector?.RecordRetry();
                });
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

    private async Task CompleteDeleteBatchAsync(IReadOnlyList<PendingDropboxDelete> batch, JsonElement response,
        V2Settings settings)
    {
        var tag = ReadUnionTag(response);
        if (tag == "complete")
        {
            await CompleteDeleteEntriesAsync(batch, response, settings);
            return;
        }
        if (tag != "async_job_id")
            throw new DropboxEffectUncertainException("Dropbox returned an unknown delete batch launch result.");
        var jobId = response.GetProperty("async_job_id").GetString();
        if (string.IsNullOrWhiteSpace(jobId))
            throw new DropboxEffectUncertainException("Dropbox omitted the delete batch job identifier.");
        await Task.WhenAll(batch.Select((item, index) => item.Context?.RecordAsync("delete-job",
            JsonSerializer.Serialize(new { jobId, entryIndex = index, entryCount = batch.Count }))
            ?? Task.CompletedTask));
        await PollDeleteBatchAsync(batch, jobId, settings);
    }

    private async Task CompleteDeleteEntriesAsync(IReadOnlyList<PendingDropboxDelete> batch, JsonElement result,
        V2Settings settings)
    {
        var entries = result.GetProperty("entries");
        if (entries.GetArrayLength() != batch.Count)
            throw new DropboxEffectUncertainException("Dropbox returned a different number of delete results.");
        for (var index = 0; index < batch.Count; index++)
        {
            var item = batch[index];
            if (item.Result.Task.IsCompleted) continue;
            var retry = false;
            try
            {
                if (!ValidateDeleteBatchEntry(entries[index], item.Expected))
                {
                    if (item.Context is { } context)
                    {
                        try { await context.RecordAsync("delete-rejected", item.Expected.Id!); }
                        catch (Exception error)
                        {
                            // The rejection could not be made durable; the refusal may still have been applied, so
                            // the effect is uncertain, never a plain I/O failure the caller might replan on.
                            throw new DropboxEffectUncertainException("Could not persist the delete rejection.", error);
                        }
                    }
                    if (ReadUnionTag(entries[index].GetProperty("failure")) == "too_many_write_operations")
                    {
                        item.ThrottleCount++;
                        DeleteJobs.Throttled();
                        _transferLimiter.Resources.ProviderThrottled(new(TransferResourceKey, SyncProviderOperation.Write), TimeSpan.Zero);
                        if (item.WriteOperationRetries < settings.DropboxWriteOperationRetryCount)
                        {
                            item.WriteOperationRetries++;
                            retry = true;
                            RequeueDelete(item);
                            continue;
                        }
                        throw new DropboxApiException(HttpStatusCode.Conflict,
                            "Dropbox throttled this delete; the file was kept and will be retried on a later run.")
                        { WriteErrorTag = "too_many_write_operations" };
                    }
                    throw new DropboxApiException(HttpStatusCode.Conflict,
                        "Dropbox refused this file's revision-conditional batch delete; it was not deleted.")
                    { WriteErrorTag = WriteErrorTag(entries[index].GetProperty("failure")) };
                }
                item.Result.TrySetResult();
            }
            catch (Exception exception) { item.Result.TrySetException(exception); }
            finally { if (!retry) item.WaitTiming?.Dispose(); }
        }
    }
}
