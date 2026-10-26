using System.Net;
using System.Text.Json;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>Coalesces folder creates; an existing folder at the path is the same result.</summary>
public sealed partial class DropboxPilotClient
{
    private const int CreateFolderBatchMaximumEntries = 1000;
    private readonly object _mkdirBatchGate = new();
    private List<PendingDropboxMkDir> _mkdirBatchPending = [];
    private bool _creatingMkDirBatch;

    /// <summary>
    /// Queues one folder create for the next <c>files/create_folder_batch</c> call, capturing the durable intent
    /// context before this returns. A parent created by the same plan is already present (the structure phase is
    /// levelled by depth), so a batched create never walks its ancestors.
    /// </summary>
    internal Task<PilotRemoteEntry> MkDirBatchedAsync(string relativePath, V2Settings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = PilotPath.NormalizeDropboxRelative(relativePath);
        if (settings.MkDirBatchSize is < 1 or > CreateFolderBatchMaximumEntries ||
            settings.MkDirBatchMaxAge <= TimeSpan.Zero || settings.BatchPollInterval <= TimeSpan.Zero ||
            settings.BatchPollTimeout <= TimeSpan.Zero || settings.DropboxWriteOperationRetryCount is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "Dropbox folder-create batching settings are invalid.");
        var item = new PendingDropboxMkDir(normalized, ProviderEffectContext.Current.Value, ct);
        List<PendingDropboxMkDir>? ready = null, first = null;
        lock (_mkdirBatchGate)
        {
            _mkdirBatchPending.Add(item);
            if (_mkdirBatchPending.Count == 1) first = _mkdirBatchPending;
            if (!_creatingMkDirBatch && _mkdirBatchPending.Count >= settings.MkDirBatchSize)
                ready = TakeMkDirBatch(settings);
        }
        // An enqueued effect is drained even if the caller cancels. Release only the staging worker;
        // its pending-effect bound and durable intent remain until this individual result is committed.
        SyncEffectStaging.Current.Value?.Release();
        SyncTransferResourceController.Lease.Current.Value?.Complete(true);
        if (ready is not null) _ = DispatchMkDirBatchesAsync(ready, settings);
        else if (first is not null) _ = SubmitMkDirBatchAfterAgeAsync(first, settings);
        return item.Result.Task;
    }

    private async Task SubmitMkDirBatchAfterAgeAsync(List<PendingDropboxMkDir> batch, V2Settings settings)
    {
        await Task.Delay(settings.MkDirBatchMaxAge);
        lock (_mkdirBatchGate)
        {
            if (!ReferenceEquals(batch, _mkdirBatchPending) || _creatingMkDirBatch) return;
            batch = TakeMkDirBatch(settings);
        }
        await DispatchMkDirBatchesAsync(batch, settings);
    }

    private List<PendingDropboxMkDir> TakeMkDirBatch(V2Settings settings)
    {
        var count = Math.Min(settings.MkDirBatchSize, _mkdirBatchPending.Count);
        var batch = _mkdirBatchPending.GetRange(0, count);
        _mkdirBatchPending = _mkdirBatchPending.GetRange(count, _mkdirBatchPending.Count - count);
        _creatingMkDirBatch = true;
        return batch;
    }

    private async Task DispatchMkDirBatchesAsync(List<PendingDropboxMkDir> batch, V2Settings settings)
    {
        while (true)
        {
            await SubmitMkDirBatchAsync(batch, settings).ConfigureAwait(false);
            lock (_mkdirBatchGate)
            {
                if (_mkdirBatchPending.Count == 0) { _creatingMkDirBatch = false; return; }
                batch = TakeMkDirBatch(settings);
            }
        }
    }

    private void RequeueMkDir(PendingDropboxMkDir item)
    {
        lock (_mkdirBatchGate) _mkdirBatchPending.Add(item);
    }

    private async Task SubmitMkDirBatchAsync(IReadOnlyList<PendingDropboxMkDir> batch, V2Settings settings)
    {
        var submitted = false;
        try
        {
            JsonDocument response;
            string rootPath;
            // The namespace commit gate admits writes only: it covers the launch request and is released as
            // soon as that request has been transmitted. Polling the accepted job and reading an existing
            // folder back are reads, so they run outside it exactly like delete batches; an unrelated refresh
            // or list never waits behind a long folder-create poll.
            var gate = DropboxUploadCommitGate.For(expectedConnection.Normalize().ConnectionId,
                expectedConnection.RootNamespaceId, settings.UploadCommitConcurrency);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Entry paths use the root's verified current path, not the folder id: id-prefixed paths make
                // Dropbox's batch endpoints roughly an order of magnitude slower.
                rootPath = await VerifyRootPathAsync(CancellationToken.None).ConfigureAwait(false);
                var submissionId = Guid.NewGuid().ToString("N");
                await Task.WhenAll(batch.Select(item => item.Context?.RecordAsync("mkdir-submit", submissionId)
                    ?? Task.CompletedTask));
                foreach (var instrumentation in batch.Select(item => item.Instrumentation).Distinct())
                    instrumentation?.RecordBatch(batch.Count);
                submitted = true;
                response = await LaunchMkDirBatchAsync(batch, rootPath, settings);
            }
            finally { gate.Release(); }
            // Polling a server job is a read, not a namespace write; BatchPollTimeout bounds it on its own.
            using var ownedResponse = response;
            try { await CompleteMkDirBatchAsync(batch, response.RootElement, rootPath, settings); }
            catch (Exception exception)
            {
                throw new DropboxEffectUncertainException("The accepted folder-create batch could not be verified.", exception);
            }
        }
        catch (Exception exception)
        {
            foreach (var item in batch)
            {
                item.WaitTiming?.Dispose();
                item.Result.TrySetException(submitted && exception is not DropboxApiException
                    ? new DropboxEffectUncertainException("The folder-create batch outcome is unknown.", exception)
                    : exception);
            }
        }
    }

    private async Task<JsonDocument> LaunchMkDirBatchAsync(IReadOnlyList<PendingDropboxMkDir> batch,
        string rootPath, V2Settings settings)
    {
        // The explicit, entry-scaled timeout is armed only when the launch request actually leaves (past token
        // resolution and the pacing wait), so a submission is never cut early; the transport client's timeout is above it.
        using var operation = new CancellationTokenSource();
        var timeout = settings.BatchOperationTimeoutFor(batch.Count, settings.MkDirBatchSize);
        try
        {
            return await PostConditionalEffectJsonAsync("https://api.dropboxapi.com/2/files/create_folder_batch",
                new
                {
                    paths = batch.Select(item => rootPath + "/" + item.Path).ToArray(),
                    autorename = false,
                    force_async = false
                }, operation.Token, () =>
                {
                    operation.CancelAfter(timeout);
                    foreach (var collector in batch.Where(item => item.WriteOperationRetries > 0)
                        .Select(item => item.Instrumentation).Distinct()) collector?.RecordRetry();
                });
        }
        catch (DropboxApiException)
        {
            foreach (var item in batch)
                if (item.Context is { } context) await context.RecordAsync("mkdir-rejected", item.Path);
            throw;
        }
    }

    private async Task CompleteMkDirBatchAsync(IReadOnlyList<PendingDropboxMkDir> batch, JsonElement response,
        string rootPath, V2Settings settings)
    {
        var tag = ReadUnionTag(response);
        if (tag == "complete")
        {
            // One cheap read after the commit: a root that moved underneath the batch makes every entry
            // uncertain, and reconciliation resolves them instead of a blind replay.
            await RequireBatchRootUnmovedAsync(rootPath).ConfigureAwait(false);
            await CompleteMkDirEntriesAsync(batch, response, settings);
            return;
        }
        if (tag != "async_job_id")
            throw new DropboxEffectUncertainException("Dropbox returned an unknown folder-create batch launch result.");
        var jobId = response.GetProperty("async_job_id").GetString();
        if (string.IsNullOrWhiteSpace(jobId))
            throw new DropboxEffectUncertainException("Dropbox omitted the folder-create batch job identifier.");
        await Task.WhenAll(batch.Select((item, index) => item.Context?.RecordAsync("mkdir-job",
            JsonSerializer.Serialize(new { jobId, entryIndex = index, entryCount = batch.Count }))
            ?? Task.CompletedTask));
        await PollMkDirBatchAsync(batch, jobId, rootPath, settings);
    }

    private async Task CompleteMkDirEntriesAsync(IReadOnlyList<PendingDropboxMkDir> batch, JsonElement result,
        V2Settings settings)
    {
        var entries = result.GetProperty("entries");
        if (entries.GetArrayLength() != batch.Count)
            throw new DropboxEffectUncertainException("Dropbox returned a different number of folder-create results.");
        for (var index = 0; index < batch.Count; index++)
        {
            var item = batch[index];
            if (item.Result.Task.IsCompleted) continue;
            var retry = false;
            try
            {
                var outcome = ReadMkDirBatchEntry(entries[index], item.Path);
                if (outcome.Created is { } created) item.Result.TrySetResult(created);
                else if (outcome.AlreadyFolder)
                {
                    // Already a folder at the path: the create is the same result, so read the folder's own id back.
                    PilotRemoteEntry? current;
                    try { current = await FindEntryAtPathAsync(item.Path, CancellationToken.None).ConfigureAwait(false); }
                    catch (InvalidDataException error)
                    {
                        // A read outside the selected pair root cannot prove this create landed in the pair.
                        throw new DropboxEffectUncertainException(
                            "Dropbox returned an existing-folder result outside the selected pair root.", error);
                    }
                    if (current is not { Kind: PilotEntryKind.Directory })
                        throw new DropboxEffectUncertainException(
                            "Dropbox reported an existing folder but read back no folder at the path.");
                    item.Result.TrySetResult(current);
                }
                else
                {
                    if (outcome.Retryable)
                    {
                        _transferLimiter.Resources.ProviderThrottled(new(TransferResourceKey, SyncProviderOperation.Write), TimeSpan.Zero);
                        if (item.WriteOperationRetries < settings.DropboxWriteOperationRetryCount)
                        {
                            item.WriteOperationRetries++;
                            retry = true;
                            RequeueMkDir(item);
                            continue;
                        }
                    }
                    if (item.Context is { } context)
                    {
                        try { await context.RecordAsync("mkdir-rejected", item.Path).ConfigureAwait(false); }
                        catch (Exception error)
                        {
                            // The refusal could not be made durable; it may still have been applied, so the effect
                            // is uncertain, never a plain I/O failure the caller might replan on.
                            throw new DropboxEffectUncertainException("Could not persist the folder-create refusal.", error);
                        }
                    }
                    throw new DropboxApiException(HttpStatusCode.Conflict,
                        "Dropbox refused this folder's create batch entry; it was not created.")
                    { WriteErrorTag = outcome.WriteErrorTag };
                }
            }
            catch (Exception exception) { item.Result.TrySetException(exception); }
            finally { if (!retry) item.WaitTiming?.Dispose(); }
        }
    }

    /// <summary>One per-entry create outcome: the created folder, an existing folder, or a refusal.</summary>
    private readonly record struct MkDirBatchEntry(PilotRemoteEntry? Created, bool AlreadyFolder, bool Retryable, string? WriteErrorTag = null);

    // Dropbox's batch entry is a union keyed by its variant ({"success":{"metadata":{...}}} / {"failure":{...}}),
    // but a flat {".tag":"success"/"failure", ...} spelling is accepted too, exactly as the shared batch reader
    // (<c>DropboxCliRouteRunner.BatchEntries</c>) reads it. An "already a folder" conflict is the same result as a
    // create, so it is not a failure; every other refusal is this entry's own failure.
    private MkDirBatchEntry ReadMkDirBatchEntry(JsonElement entry, string expectedRelativePath)
    {
        var variant = MkDirEntryVariant(entry);
        if (variant == "success")
        {
            PilotRemoteEntry current;
            // A create-batch success entry carries an untagged FolderMetadata struct (the entry's own
            // ".tag" is "success"), so the known kind is supplied rather than read from the metadata.
            try { current = RemoteEntryFromMetadata(MkDirSuccessMetadata(entry), assumedKind: PilotEntryKind.Directory); }
            catch (InvalidDataException error)
            {
                // A result path outside the selected root cannot prove this create landed in the pair.
                throw new DropboxEffectUncertainException(
                    "Dropbox returned a folder-create result outside the selected pair root.", error);
            }
            if (current.Kind != PilotEntryKind.Directory ||
                !string.Equals(PilotPath.DropboxComparisonKey(current.RelativePath),
                    PilotPath.DropboxComparisonKey(expectedRelativePath), StringComparison.Ordinal))
                throw new DropboxEffectUncertainException(
                    "Dropbox's folder-create result did not match the planned folder identity and path.");
            return new MkDirBatchEntry(current, false, false);
        }
        if (variant != "failure")
            throw new DropboxEffectUncertainException("Dropbox returned an unknown per-folder create outcome.");
        var failure = entry.TryGetProperty("failure", out var failureNode) ? failureNode : entry;
        var reason = ReadUnionTag(failure);
        var writeError = failure;
        if (string.Equals(reason, "path", StringComparison.Ordinal) &&
            failure.TryGetProperty("path", out var pathError))
        {
            writeError = pathError;
            reason = ReadUnionTag(pathError);
        }
        var conflict = string.Equals(reason, "conflict", StringComparison.Ordinal) &&
            writeError.TryGetProperty("conflict", out var conflictNode) ? ReadUnionTag(conflictNode) : null;
        if (string.Equals(reason, "conflict", StringComparison.Ordinal) &&
            string.Equals(conflict, "folder", StringComparison.Ordinal))
            return new MkDirBatchEntry(null, true, false);
        return new MkDirBatchEntry(null, false,
            string.Equals(reason, "too_many_write_operations", StringComparison.Ordinal), WriteErrorTag(failure));
    }

    private static string? MkDirEntryVariant(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (entry.TryGetProperty("success", out _)) return "success";
        if (entry.TryGetProperty("failure", out _)) return "failure";
        return ReadUnionTag(entry);
    }

    private static JsonElement MkDirSuccessMetadata(JsonElement entry)
    {
        if (entry.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.Object)
            return success.TryGetProperty("metadata", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : success;
        if (entry.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            return metadata;
        throw new DropboxEffectUncertainException("Dropbox returned no folder metadata for a created folder.");
    }
}
