using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// Closed sequential sessions stage independently of their bounded, conditional namespace commits.
public sealed partial class DropboxPilotClient
{
    internal const long BatchedUploadMaximumBytes = 8L * 1024 * 1024;
    private readonly object _batchGate = new();
    private DropboxUploadQueue? _uploadQueue;

    internal Task<PilotUploadResult> UploadBatchedCreateAsync(string relativePath, Stream source,
        string expectedContentHash, CancellationToken cancellationToken) =>
        UploadBatchedAsync(relativePath, source, expectedContentHash, null, V2.V2Settings.Default, cancellationToken);

    /// <summary>Stages a bounded small upload and waits for its strict create or revision-conditional batch result.</summary>
    internal async Task<PilotUploadResult> UploadBatchedAsync(string relativePath, Stream source,
        string expectedContentHash, string? expectedRevision, V2.V2Settings settings, CancellationToken cancellationToken,
        DateTimeOffset? clientModified = null)
    {
        if (!source.CanSeek || source.Length > BatchedUploadMaximumBytes)
            throw new IOException("A batched upload source must be seekable and within the small-upload limit.");
        if (settings.DropboxWriteOperationRetryCount is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(settings.DropboxWriteOperationRetryCount));
        if (settings.UploadCommitConcurrency is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(settings.UploadCommitConcurrency));
        DropboxUploadQueue queue;
        lock (_batchGate) queue = _uploadQueue ??= new DropboxUploadQueue(settings,
            (batch, maximum) => CommitBatchAsync(batch, maximum, settings));
        await V2.RunOperations.WaitAsync(queue.Slots, "upload queue slots", cancellationToken, V2.RunOperationKind.TransferSlot).ConfigureAwait(false);
        var queued = false;
        try
        {
            var normalizedPath = PilotPath.NormalizeDropboxRelative(relativePath);
            var (sessionId, count) = await StartClosedSessionAsync(source, settings, cancellationToken).ConfigureAwait(false);
            if (SyncUploadSource.Current.Value is { } owner)
                await owner.ReleaseAsync(source).ConfigureAwait(false);
            var pending = new DropboxUploadCommit(normalizedPath, expectedRevision, sessionId,
                expectedContentHash, count, clientModified);
            using var wait = pending.Instrumentation?.Measure(V2.RunTimingStage.BatchWait);
            queue.Enqueue(pending);
            queued = true;
            SyncTransferResourceController.Lease.Current.Value?.Complete(true);
            SyncEffectStaging.Current.Value?.Release();
            // A transmitted effect drains to a durable result even when its original caller cancels.
            return await pending.Result.Task.ConfigureAwait(false);
        }
        finally { if (!queued) queue.Slots.Release(); }
    }

    private HttpContent SessionContent(Stream source, int count)
    {
        var content = new UploadPayloadContent(new ThrottledLeaveOpenReadStream(source, _transferLimiter), count);
        content.Headers.ContentType = new("application/octet-stream");
        return content;
    }

    private async Task<(string SessionId, int Count)> StartClosedSessionAsync(Stream source,
        V2.V2Settings settings, CancellationToken cancellationToken)
    {
        using var stagingTiming = V2.RunInstrumentation.Current?.Measure(V2.RunTimingStage.Staging);
        source.Position = 0;
        var count = checked((int)source.Length);
        var sessionId = await SendClosedSessionStartAsync(source, count, settings, cancellationToken).ConfigureAwait(false);
        return (sessionId, count);
    }

    private async Task CommitBatchAsync(IReadOnlyList<DropboxUploadCommit> batch, int maximumBatchEntries,
        V2.V2Settings settings)
    {
        var retryCount = settings.DropboxWriteOperationRetryCount;
        var gate = DropboxUploadCommitGate.For(expectedConnection.Normalize().ConnectionId,
            expectedConnection.RootNamespaceId, settings.UploadCommitConcurrency);
        await V2.RunOperations.WaitAsync(gate, "upload commit gate").ConfigureAwait(false);
        // The commit itself is one tracked batch operation, named with its route and size, so a slow finish_batch_v2
        // is named by the run's sweeper.
        using var batchOp = V2.RunOperations.Begin(V2.RunOperationKind.BatchCommit,
            "upload_session/finish_batch_v2 (" + batch.Count + " files)");
        // The explicit, entry-scaled timeout is armed only when a request actually leaves (past the root check, the
        // durable upload-commit checkpoints and the pacing wait) and is separate per attempt, so a queued or retried
        // batch is never cut early; the transport client's own timeout is set above it.
        CancellationTokenSource? operation = null;
        var transmitted = false;
        try
        {
            for (var attempt = 0; batch.Count > 0; attempt++)
            {
                transmitted = false;
                // The batch's commit paths use the root's own verified current path, never the folder id, whose
                // id-prefixed entry paths make Dropbox's batch commit roughly an order of magnitude slower.
                var rootPath = await VerifyRootPathAsync(CancellationToken.None).ConfigureAwait(false);
                await RecordUploadCommitCheckpointsAsync(batch).ConfigureAwait(false);
                using var request = await AuthorizedAsync(HttpMethod.Post,
                    "https://api.dropboxapi.com/2/files/upload_session/finish_batch_v2", CancellationToken.None);
                request.Content = new StringContent(JsonSerializer.Serialize(
                    new { entries = batch.Select(item => UploadBatchEntry(item, rootPath)).ToArray() }, JsonOptions),
                    Encoding.UTF8, "application/json");
                batch[0].Instrumentation?.RecordBatch(batch.Count);
                using var attemptTimeout = new CancellationTokenSource();
                operation = attemptTimeout;
                var timeout = settings.BatchOperationTimeoutFor(batch.Count, maximumBatchEntries);
                using var document = await SendConditionalEffectAsync(request, attemptTimeout.Token,
                    () =>
                    {
                        transmitted = true;
                        attemptTimeout.CancelAfter(timeout);
                        if (attempt > 0)
                            foreach (var instrumentation in batch.Select(item => item.Instrumentation).Distinct())
                                instrumentation?.RecordRetry();
                    }).ConfigureAwait(false);
                // One cheap read after the commit: a root that moved underneath the batch makes every entry
                // uncertain, and reconciliation resolves them instead of a blind replay.
                await RequireBatchRootUnmovedAsync(rootPath).ConfigureAwait(false);
                var entries = document.RootElement.GetProperty("entries");
                if (entries.GetArrayLength() != batch.Count)
                    throw new InvalidDataException("Dropbox returned a different number of batch commit results.");
                var retry = await Task.WhenAll(entries.EnumerateArray().Select((entry, index) =>
                    CompleteUploadEntryAsync(batch[index], entry, attempt < retryCount, rootPath))).ConfigureAwait(false);
                batch = batch.Where((_, index) => retry[index]).ToArray();
            }
        }
        catch (Exception exception) when (!(operation?.IsCancellationRequested ?? false) &&
            (!transmitted || exception is DropboxApiException
                { StatusCode: { } status } && (int)status is >= 400 and < 500 &&
                status is not (System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests)))
        {
            await Task.WhenAll(batch.Select(async item =>
            {
                try
                {
                    await item.RecordAsync("upload-rejected").ConfigureAwait(false);
                    item.Result.TrySetException(!transmitted && exception is DropboxEffectUncertainException
                        { InnerException: { } cause } ? cause : exception);
                }
                catch (Exception error) { item.Result.TrySetException(new DropboxEffectUncertainException("Could not persist the upload rejection.", error)); }
            })).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            foreach (var item in batch)
                item.Result.TrySetException(new DropboxEffectUncertainException(
                    "The upload batch commit outcome is unknown. " + SyncActivityText.Failure(exception), exception));
        }
        finally { gate.Release(); }
    }

    // One durable write per batch: every entry's commit checkpoint joins a single store transaction through its
    // owning committer (the Executor's group commit), so a 1000-entry batch pays one checkpoint write, not 1000.
    // Entries whose context is free-standing (no committer) fall back to their own recorder. Every checkpoint is
    // awaited before the send below, so "checkpoint durable before send" is preserved either way.
    private static async Task RecordUploadCommitCheckpointsAsync(IReadOnlyList<DropboxUploadCommit> batch)
    {
        foreach (var group in batch.Where(item => item.Context is { Owner: not null })
                     .GroupBy(item => item.Context!.Owner!))
            await group.Key.RecordProviderCheckpointsAsync(group
                .Select(item => (item.Context!.Action!,
                    new V2.ProviderEffectCheckpoint("upload-commit", item.SessionId, DateTimeOffset.UtcNow)))
                .ToArray()).ConfigureAwait(false);
        foreach (var item in batch.Where(item => item.Context is { Owner: null }))
            await item.Context!.RecordAsync("upload-commit", item.SessionId).ConfigureAwait(false);
    }

    private object UploadBatchEntry(DropboxUploadCommit item, string verifiedRootPath) => new
    {
        cursor = new { session_id = item.SessionId, offset = item.Size },
        commit = CommitInfo(item.RelativePath, item.ExpectedRevision, verifiedRootPath: verifiedRootPath, clientModified: item.ClientModifiedUtc)
    };

    private async Task<bool> CompleteUploadEntryAsync(DropboxUploadCommit item, JsonElement entry, bool retryAllowed,
        string verifiedRootPath)
    {
        try
        {
            var tag = ReadUnionTag(entry);
            if (tag == "success")
            {
                item.Result.TrySetResult(ParseUploadResult(entry, item.ExpectedContentHash, item.Size,
                    verifiedRootPath, item.RelativePath));
                return false;
            }
            if (tag != "failure") throw new DropboxEffectUncertainException("Dropbox returned an unrecognised batch commit entry.");
            try { await item.RecordAsync("upload-rejected").ConfigureAwait(false); }
            catch (Exception error)
            {
                // The rejection could not be made durable; the refusal may still have been applied, so the effect
                // is uncertain, never a plain I/O failure the caller might replan on.
                throw new DropboxEffectUncertainException("Could not persist the upload rejection.", error);
            }
            var reason = ReadUnionTag(entry.TryGetProperty("failure", out var failure) ? failure : default);
            if (reason == "path") reason = WriteErrorTag(failure);
            if (reason == "too_many_write_operations")
            {
                _transferLimiter.Resources.ProviderThrottled(new(TransferResourceKey, SyncProviderOperation.Write), TimeSpan.Zero);
                if (retryAllowed) return true;
            }
            throw new DropboxApiException(reason == "too_many_write_operations"
                ? System.Net.HttpStatusCode.TooManyRequests : System.Net.HttpStatusCode.Conflict,
                "Dropbox refused this file's conditional upload batch entry (reason: " + (reason ?? "unknown") +
                "); no effect was applied.") { WriteErrorTag = reason };
        }
        catch (Exception exception) { item.Result.TrySetException(exception); }
        return false;
    }
}
