using System.Text.Json;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

internal sealed class FolderRestoreNotAppliedException(Exception inner)
    : Exception("The restore was not applied: " + inner.Message, inner);

/// <summary>
/// whole-folder deletes through the same delete_batch route and job check as file deletes. A
/// folder takes no parent_rev (Dropbox does not support one for folders), so what the job removed is proven
/// afterwards from the change feed by the caller, never assumed.
/// </summary>
public sealed partial class DropboxPilotClient
{
    // A whole-folder job removes every descendant server-side; a large tree can run far longer than a 1000-file job.
    private static readonly TimeSpan FolderDeleteJobTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan FolderDeleteMinimumPoll = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Deletes up to 1000 folders in one delete_batch, id-rooted , and waits for the job. Each
    /// result is true when Dropbox reports that entry deleted, false when it refused it. Never retried or replayed;
    /// any unproven outcome throws.
    /// </summary>
    internal async Task<bool[]> DeleteFoldersAsync(IReadOnlyList<PilotRemoteEntry> folders, V2Settings settings,
        Func<string, Task>? recordJob = null, Func<bool[], Task>? recordCompleted = null,
        Func<Task<IReadOnlyList<PilotRemoteEntry>>>? prepare = null)
    {
        if (folders.Count is < 1 or > DeleteBatchMaximumEntries)
            throw new ArgumentOutOfRangeException(nameof(folders), "A folder delete batch holds 1 to 1000 folders.");
        if (folders.Any(folder => folder.Kind != PilotEntryKind.Directory || !IsExactDropboxObjectIdentity(folder.Id)))
            throw new InvalidDataException("A whole-folder delete requires exact folder identities.");
        var gate = DropboxUploadCommitGate.For(expectedConnection.Normalize().ConnectionId,
            expectedConnection.RootNamespaceId, settings.UploadCommitConcurrency);
        await V2.RunOperations.WaitAsync(gate, "upload commit gate").ConfigureAwait(false);
        JsonDocument launch;
        try
        {
            // The namespace gate protects the pre-submit feed/checkpoint/submit sequence, not the server's
            // asynchronous job. Other pairs' independent uploads may commit while this job is polled (decision 72).
            await VerifyRootIdentityAsync(CancellationToken.None).ConfigureAwait(false);
            if (prepare is not null) folders = await prepare().ConfigureAwait(false);
            if (folders.Count == 0) return [];
            if (folders.Count > DeleteBatchMaximumEntries || folders.Any(folder =>
                    folder.Kind != PilotEntryKind.Directory || !IsExactDropboxObjectIdentity(folder.Id)))
                throw new InvalidDataException("A prepared folder delete requires 1 to 1000 exact folder identities.");
            launch = await PostConditionalEffectJsonAsync("https://api.dropboxapi.com/2/files/delete_batch",
                new
                {
                    entries = folders.Select(folder => new
                    {
                        path = RequiredRootId() + "/" + PilotPath.NormalizeDropboxRelative(folder.RelativePath)
                    }).ToArray()
                }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        try
        {
            var result = launch;
            var tag = ReadUnionTag(result.RootElement);
            if (tag == "async_job_id")
            {
                var jobId = result.RootElement.GetProperty("async_job_id").GetString();
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new DropboxEffectUncertainException("Dropbox omitted the folder delete job identifier.");
                if (recordJob is not null) await recordJob(jobId).ConfigureAwait(false);
                result = await PollFolderDeleteJobAsync(jobId, folders.Count, settings).ConfigureAwait(false);
                tag = ReadUnionTag(result.RootElement);
            }
            using (result == launch ? null : result)
            {
                var completed = FolderDeleteResults(result.RootElement, folders.Count);
                if (recordCompleted is not null) await recordCompleted(completed).ConfigureAwait(false);
                return completed;
            }
        }
        finally { launch.Dispose(); }
    }

    /// <summary>Observes a persisted job once; never submits another delete.</summary>
    internal async Task<(bool[]? Completed, bool Unavailable)> ReconcileFolderDeleteJobAsync(
        string jobId, int count, CancellationToken ct)
    {
        using var result = await ReadDeleteBatchCheckAsync(jobId, ct, allowUnavailable: true)
            .ConfigureAwait(false);
        if (result is null || ReadUnionTag(result.RootElement) == "failed") return (null, true);
        return (ReadUnionTag(result.RootElement) == "in_progress" ? null : FolderDeleteResults(result.RootElement, count), false);
    }

    private static bool[] FolderDeleteResults(JsonElement result, int count)
    {
        if (ReadUnionTag(result) != "complete")
            throw new DropboxEffectUncertainException("Dropbox could not prove the folder delete outcome.");
        var entries = result.GetProperty("entries");
        if (entries.GetArrayLength() != count)
            throw new DropboxEffectUncertainException("Dropbox returned a different number of folder delete results.");
        return entries.EnumerateArray().Select(entry => ReadUnionTag(entry) switch
        {
            "success" => true,
            "failure" => false,
            _ => throw new DropboxEffectUncertainException("Dropbox returned an unknown folder delete outcome."),
        }).ToArray();
    }

    private async Task<JsonDocument> PollFolderDeleteJobAsync(string jobId, int count, V2Settings settings)
    {
        using var poll = V2.RunOperations.Begin(V2.RunOperationKind.BatchCommit,
            "delete_batch/check (" + count + " folders)");
        var interval = settings.BatchPollInterval > FolderDeleteMinimumPoll ? settings.BatchPollInterval : FolderDeleteMinimumPoll;
        var started = Environment.TickCount64;
        while (TimeSpan.FromMilliseconds(Environment.TickCount64 - started) < FolderDeleteJobTimeout)
        {
            await Task.Delay(interval).ConfigureAwait(false);
            var check = await ReadDeleteBatchJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (ReadUnionTag(check.RootElement) != "in_progress") return check;
            check.Dispose();
        }
        throw new DropboxEffectUncertainException("The folder delete job is still pending.");
    }

    internal async Task RestoreDeletedFolderAsync(string path, CancellationToken ct, Func<Task> recordStarted)
    {
        var sent = false;
        try { await CreateDirectoryAsync(path, ct, async () =>
            { PairPause.Check(); sent = true; await recordStarted().ConfigureAwait(false); }, () => sent = false).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException && !sent)
        { throw new FolderRestoreNotAppliedException(error); }
    }

    /// <summary>Restores one deleted file to a revision, first recreating any parent folder the delete removed.</summary>
    internal async Task<PilotRemoteEntry> RestoreDeletedFileAsync(string relativePath, string revision,
        CancellationToken ct, Func<Task> recordStarted)
    {
        var sent = false;
        try
        {
            var path = PilotPath.NormalizeDropboxRelative(relativePath);
            await EnsureRemoteParentFoldersAsync(path, ct).ConfigureAwait(false);
            return await RestoreRevisionAsync(path, revision, ct, async () =>
                { PairPause.Check(); sent = true; await recordStarted().ConfigureAwait(false); }, () => sent = false).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException && !sent)
        {
            // Parent creation can have effects, but the selected file restore did not. Only this
            // transport owner knows whether the restore crossed the send boundary.
            throw new FolderRestoreNotAppliedException(error);
        }
    }
}
