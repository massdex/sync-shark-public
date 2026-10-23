using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>Read-only reconciliation of persisted folder-create jobs; an unknown launch is never resubmitted.</summary>
public sealed partial class DropboxPilotClient
{
    /// <summary>Null means pending; true proves the folder was created or already existed; false proves its entry was refused.</summary>
    internal async Task<bool?> ResolveMkDirBatchJobAsync(string relativePath, string jobId,
        int entryIndex, int entryCount, CancellationToken ct, V2.ProviderJobCheckCache? jobChecks = null)
    {
        if (string.IsNullOrWhiteSpace(jobId) || entryIndex < 0 ||
            entryCount is < 1 or > CreateFolderBatchMaximumEntries || entryIndex >= entryCount)
            throw new InvalidDataException("The saved folder-create job identity or entry position is invalid.");
        var path = PilotPath.NormalizeDropboxRelative(relativePath);
        await VerifyRootIdentityAsync(ct);
        // Every entry of one batch names the same job: one reconcile pass checks it once and reads its own entry.
        var job = jobChecks is null
            ? await ReadMkDirBatchJobRootAsync(jobId, ct)
            : await jobChecks.ReadAsync(this, "mkdir", jobId, token => ReadMkDirBatchJobRootAsync(jobId, token), ct);
        var tag = ReadUnionTag(job);
        if (tag == "in_progress") return null;
        if (tag != "complete")
            throw new DropboxEffectUncertainException("Dropbox could not prove the saved folder-create job outcome.");
        var entries = job.GetProperty("entries");
        if (entries.GetArrayLength() != entryCount)
            throw new DropboxEffectUncertainException("The saved folder-create job returned a different result count.");
        var outcome = ReadMkDirBatchEntry(entries[entryIndex], path);
        return outcome.Created is not null || outcome.AlreadyFolder;
    }

    // The job status alone, cloned out of its JsonDocument so every entry of the batch can read it after the
    // response document itself is disposed.
    private async Task<JsonElement> ReadMkDirBatchJobRootAsync(string jobId, CancellationToken ct)
    {
        using var document = await ReadMkDirBatchJobAsync(jobId, ct);
        return document.RootElement.Clone();
    }

    private async Task PollMkDirBatchAsync(IReadOnlyList<PendingDropboxMkDir> batch, string jobId,
        string rootPath, V2.V2Settings settings)
    {
        // Registration starts only after every entry's job identity is durable. One caller stopping
        // must not cancel another caller's check, nor turn an accepted effect into a replayable refusal.
        using var stopped = new CancellationTokenSource();
        var registrations = batch.Select(item => item.StopToken.Register(() =>
        {
            if (item.Result.TrySetException(new DropboxEffectUncertainException(
                "Folder-create polling stopped; reconcile the saved job identifier before replanning.")))
                item.WaitTiming?.Dispose();
            if (batch.All(candidate => candidate.Result.Task.IsCompleted)) stopped.Cancel();
        })).ToArray();
        try
        {
            var started = Environment.TickCount64;
            while (TimeSpan.FromMilliseconds(Environment.TickCount64 - started) < settings.BatchPollTimeout)
            {
                await Task.Delay(settings.BatchPollInterval, stopped.Token);
                using var check = await ReadMkDirBatchJobAsync(jobId, stopped.Token);
                var tag = ReadUnionTag(check.RootElement);
                if (tag == "in_progress") continue;
                if (tag != "complete")
                    throw new DropboxEffectUncertainException("Dropbox could not prove the folder-create job outcome.");
                // A received definitive response owns completion from here; stop callbacks must not
                // race with its per-entry refusal/requeue decision.
                foreach (var registration in registrations) registration.Dispose();
                // The job's completion is its commit point: prove the root did not move while it ran.
                await RequireBatchRootUnmovedAsync(rootPath).ConfigureAwait(false);
                await CompleteMkDirEntriesAsync(batch, check.RootElement, settings);
                return;
            }
            throw new DropboxEffectUncertainException(
                "The folder-create job is still pending; reconcile its saved identifier before replanning.");
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        finally
        {
            // Dispose waits for callbacks before their shared poll source is disposed.
            foreach (var registration in registrations) registration.Dispose();
        }
    }

    private Task<JsonDocument> ReadMkDirBatchJobAsync(string jobId, CancellationToken ct) =>
        PostJsonAsync("https://api.dropboxapi.com/2/files/create_folder_batch/check", new { async_job_id = jobId }, ct);
}
