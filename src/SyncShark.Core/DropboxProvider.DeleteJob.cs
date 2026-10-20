using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>Read-only reconciliation of persisted delete jobs; an unknown launch is never resubmitted.</summary>
public sealed partial class DropboxPilotClient
{
    /// <summary>Null means pending; true proves the expected file was deleted; false proves its entry was refused.</summary>
    internal async Task<bool?> ResolveDeleteBatchJobAsync(PilotRemoteEntry expected, string jobId,
        int entryIndex, int entryCount, CancellationToken ct, V2.ProviderJobCheckCache? jobChecks = null)
    {
        if (string.IsNullOrWhiteSpace(jobId) || entryIndex < 0 || entryCount is < 1 or > DeleteBatchMaximumEntries ||
            entryIndex >= entryCount)
            throw new InvalidDataException("The saved delete job identity or entry position is invalid.");
        await VerifyRootIdentityAsync(ct);
        // Every entry of one batch names the same job: one reconcile pass checks it once and reads its own entry.
        var job = jobChecks is null
            ? await ReadDeleteBatchJobRootAsync(jobId, ct)
            : await jobChecks.ReadAsync(this, "delete", jobId, token => ReadDeleteBatchJobRootAsync(jobId, token), ct);
        var tag = ReadUnionTag(job);
        if (tag == "in_progress") return null;
        if (tag != "complete")
            throw new DropboxEffectUncertainException("Dropbox could not prove the saved delete job outcome.");
        var entries = job.GetProperty("entries");
        if (entries.GetArrayLength() != entryCount)
            throw new DropboxEffectUncertainException("The saved delete job returned a different result count.");
        return ValidateDeleteBatchEntry(entries[entryIndex], expected);
    }

    // The job status alone, cloned out of its JsonDocument so every entry of the batch can read it after the
    // response document itself is disposed.
    private async Task<JsonElement> ReadDeleteBatchJobRootAsync(string jobId, CancellationToken ct)
    {
        using var document = await ReadDeleteBatchJobAsync(jobId, ct);
        return document.RootElement.Clone();
    }

    private async Task PollDeleteBatchAsync(IReadOnlyList<PendingDropboxDelete> batch, string jobId,
        V2.V2Settings settings)
    {
        // Registration starts only after every entry's job identity is durable. One caller stopping
        // must not cancel another caller's check, nor turn an accepted effect into a replayable refusal.
        using var stopped = new CancellationTokenSource();
        var registrations = batch.Select(item => item.StopToken.Register(() =>
        {
            if (item.Result.TrySetException(new DropboxEffectUncertainException(
                "Delete polling stopped; reconcile the saved job identifier before replanning.")))
                item.WaitTiming?.Dispose();
            if (batch.All(candidate => candidate.Result.Task.IsCompleted)) stopped.Cancel();
        })).ToArray();
        try
        {
            var started = Environment.TickCount64;
            // The delete job is one tracked batch operation, announced once, so a slow poll is named by the sweeper.
            using var pollOperation = V2.RunOperations.Begin(V2.RunOperationKind.BatchCommit,
                "delete_batch/check (" + batch.Count + " files)");
            await SyncByteProgressStream.ReportCurrentProcessAsync("Waiting for Dropbox delete job",
                $"{batch.Count} delete(s) are running as one Dropbox job; checking every " +
                $"{settings.BatchPollInterval.TotalMilliseconds:N0} ms.", ProcessMessageKind.Notice).ConfigureAwait(false);
            while (TimeSpan.FromMilliseconds(Environment.TickCount64 - started) < settings.BatchPollTimeout)
            {
                await Task.Delay(settings.BatchPollInterval, stopped.Token);
                using var check = await ReadDeleteBatchJobAsync(jobId, stopped.Token);
                var tag = ReadUnionTag(check.RootElement);
                if (tag == "in_progress") continue;
                if (tag != "complete")
                    throw new DropboxEffectUncertainException("Dropbox could not prove the delete job outcome.");
                // A received definitive response owns completion from here; stop callbacks must not
                // race with its per-entry refusal/requeue decision.
                foreach (var registration in registrations) registration.Dispose();
                await CompleteDeleteEntriesAsync(batch, check.RootElement, settings);
                return;
            }
            throw new DropboxEffectUncertainException("The delete job is still pending; reconcile its saved identifier before replanning.");
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        finally
        {
            // Dispose waits for callbacks before their shared poll source is disposed.
            foreach (var registration in registrations) registration.Dispose();
        }
    }

    private async Task<JsonDocument> ReadDeleteBatchJobAsync(string jobId, CancellationToken ct) =>
        await ReadDeleteBatchCheckAsync(jobId, ct, allowUnavailable: false).ConfigureAwait(false)
        ?? throw new DropboxEffectUncertainException("Dropbox could not prove the delete job outcome.");

    // A check refusal is not a refused delete. Keep the accepted effect unresolved; whole-folder recovery
    // may instead prove its outcome from the saved change feed (decision 73), never by resubmitting it.
    private async Task<JsonDocument?> ReadDeleteBatchCheckAsync(string jobId, CancellationToken ct,
        bool allowUnavailable)
    {
        string? errorTag = null;
        try
        {
            return await PostJsonCoreAsync("https://api.dropboxapi.com/2/files/delete_batch/check",
                new { async_job_id = jobId }, false, ct, allowedConflict: error =>
                {
                    errorTag = ReadUnionTag(error);
                    if (allowUnavailable && errorTag is "invalid_async_job_id" or "expired_async_job_id" or "internal_error")
                        return true;
                    throw new DropboxEffectUncertainException(
                        "Dropbox delete_batch/check returned HTTP 409 (" + errorTag + "); the delete outcome is unverified.");
                }).ConfigureAwait(false);
        }
        finally
        {
            if (errorTag is not null)
                await SyncByteProgressStream.ReportCurrentProcessAsync("Dropbox delete job check unavailable",
                    "delete_batch/check returned HTTP 409 (" + errorTag + "). " +
                    "The accepted delete is not replayed; its outcome still needs reconciliation.",
                    ProcessMessageKind.Notice).ConfigureAwait(false);
        }
    }

    private bool ValidateDeleteBatchEntry(JsonElement entry, PilotRemoteEntry expected)
    {
        var tag = ReadUnionTag(entry);
        if (tag == "failure") return false;
        if (tag != "success")
            throw new DropboxEffectUncertainException("Dropbox returned an unknown per-file delete outcome.");
        try
        {
            var metadata = entry.GetProperty("metadata");
            var actual = RemoteEntryFromMetadata(metadata);
            if (actual.Kind != PilotEntryKind.File || !SameEntryIdentity(actual, expected))
                throw new DropboxEffectUncertainException("Dropbox's delete result did not match the planned file identity, path and revision.");
            return true;
        }
        catch (Exception exception) when (IsEffectBodyShapeException(exception))
        {
            throw new DropboxEffectUncertainException("Dropbox returned malformed per-file delete metadata.", exception);
        }
    }
}
