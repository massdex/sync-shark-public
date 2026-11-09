using System.Globalization;
using System.Text.Json;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox reads and writes (section 5.4).</summary>
/// <remarks>Over the 300-line prompt because staging, the one publication collector and folder submission share this
/// writer's single provider-effect ownership.</remarks>
public sealed partial class DropboxWriter(
    DropboxApi api, PairConfig pair, EngineSettings settings, DropboxListing listing)
{
    // The write receipt's metadata field name, readable only through this owner (F24).
    internal static string MetadataName => DropboxProtocol.Metadata;

    private readonly LinkedList<PendingPublish> _queue = new();
    private bool _draining;

    /// <summary>Opens the exact Dropbox revision for reading.</summary>
    public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
        MappedAsync(api.DownloadAsync(DropboxProtocol.Download,
            new { path = DropboxProtocol.RevisionPrefix + entry.Revision }, attempt, ct, subject: entry.Path),
            DropboxProtocol.Download, false, entry.Path, attempt);

    // One stage: the snapshot requirement and the direct-versus-session choice are decided once; direct staging returns a single-shot stage, and otherwise the slice loop, running offset and content hasher produce one closed session stage.
    /// <summary>Prepares an upload without publishing its target. A sole single-file operation within the single-upload
    /// limit stages direct; all other work completes a closed session here.</summary>
    public async Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
    {
        RequireSnapshot(path, content, source);
        if (api.Limiter.SelectDirect(content.Length))
            return DropboxStaged.Single(path, content, source);

        var total = content.Length;
        long offset = 0L;
        string? sessionId = null;
        using var hash = new ContentBlockHasher();
        var route = DropboxProtocol.UploadSessionStart;
        RequestAttempt? attempt = null;
        try
        {
            // An empty file still takes one closed zero-byte start, then the normal finish.
            do
            {
                var length = Math.Min(DropboxApiLimits.SingleUploadMaxBytes, total - offset);
                var close = offset + length >= total;
                using var slice = new UploadSlice(content, offset, length);
                attempt = Attempt();
                if (sessionId is null)
                {
                    (sessionId, var sent) = await MappedAsync(api.UploadAsync(route,
                        new { close }, slice, DropboxReplyShapes.SessionId(), attempt, ct, hash),
                        route, true, path, attempt).ConfigureAwait(false);
                    offset = checked(offset + sent);
                }
                else
                {
                    route = DropboxProtocol.UploadSessionAppend;
                    var (_, sent) = await MappedAsync(api.UploadAsync(route,
                        new { cursor = new { session_id = sessionId, offset }, close }, slice, DropboxReplyShapes.Void(),
                        attempt, ct, hash), route, true, path, attempt).ConfigureAwait(false);
                    offset = checked(offset + sent);
                }
            }
            while (offset < total);
        }
        catch (Exception error) when (DropboxErrors.NoUsableReply(error, attempt, ct))
        {
            throw DropboxErrors.Refused(path, error, route, attempt);
        }
        if (offset != total || content.Length != total) throw new ChangedUnderneathException(path);
        return DropboxStaged.Session(path, sessionId!, total, source, hash.Finish());
    }

    private static void RequireSnapshot(string path, Stream content, Entry source)
    {
        if (content.Position != 0 || content.Length != source.Size) throw new ChangedUnderneathException(path);
    }

    private static RequestAttempt Attempt() => new(new object(), AttemptOwner.Request);

    /// <summary>Publishes staged content under the expected revision condition, through the one collector.</summary>
    public async Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected,
        RemovalSelection? selection, CancellationToken ct)
    {
        var upload = (DropboxStaged)staged;
        object mode = expected is null
            ? DropboxProtocol.ModeAdd
            : DropboxProtocol.TaggedUnion(DropboxProtocol.ModeUpdate, expected.Revision);
        var commit = new
        {
            path = DropboxPaths.Remote(pair, upload.Path),
            mode,
            autorename = false,
            client_modified = upload.Source.Modified.UtcDateTime.ToString(
                DropboxProtocol.ClientModifiedFormat, CultureInfo.InvariantCulture),
            strict_conflict = true
        };
        var published = await EnqueueAsync(new PendingPublish(upload, commit, expected is null, ct)).ConfigureAwait(false);
        return new RemovalResult(RemovalOutcome.Published, null, DropboxListing.Respelled(published, upload.Path), null);
    }

    private Task<Entry> EnqueueAsync(PendingPublish member)
    {
        member.Token.ThrowIfCancellationRequested();
        bool start;
        lock (_queue)
        {
            member.Node = _queue.AddLast(member);
            member.Registration = member.Token.Register(Cancel, member);
            start = !_draining;
            _draining = true;
        }
        if (start) _ = DrainAsync();
        return member.Done.Task;
    }

    // Cancellation removes a queued member promptly; after detachment its cohort request owns the outcome.
    private void Cancel(object? state)
    {
        var member = (PendingPublish)state!;
        bool removed;
        lock (_queue)
        {
            removed = member.Node?.List is not null;
            if (removed) _queue.Remove(member.Node!);
        }
        if (removed) member.Done.TrySetCanceled(member.Token);
    }

    // One drain owner and no fill wait; each cohort settles every detached member, so this task never faults.
    private async Task DrainAsync()
    {
        while (true)
        {
            List<PendingPublish> cohort;
            lock (_queue)
            {
                if (_queue.Count == 0)
                {
                    _draining = false;
                    return;
                }
                cohort = Detach();
            }
            foreach (var member in cohort) member.Registration.Dispose();
            await (cohort[0].Upload.Content is null ? FinishCohortAsync(cohort) : PublishDirectAsync(cohort[0]))
                .ConfigureAwait(false);
        }
    }

    // Callers hold the queue lock. The oldest entry names the cohort: a direct entry goes alone, and sessions take the
    // same cancellation token, up to the provider batch bound, without overtaking an older direct entry.
    private List<PendingPublish> Detach()
    {
        var first = _queue.First!;
        var cohort = new List<PendingPublish>();
        for (var node = first; node is not null && cohort.Count < DropboxApiLimits.BatchMaxEntries;)
        {
            var next = node.Next;
            var direct = node.Value.Upload.Content is not null;
            if (direct && cohort.Count != 0) break;
            if (node.Value.Token == first.Value.Token)
            {
                _queue.Remove(node);
                cohort.Add(node.Value);
            }
            if (direct) break;
            node = next;
        }
        return cohort;
    }

    private async Task PublishDirectAsync(PendingPublish member)
    {
        var upload = member.Upload;
        var content = upload.Content!;
        var attempt = Attempt();
        try
        {
            RequireSnapshot(upload.Path, content, upload.Source);
            var (entry, sent, hash) = await api.UploadDirectAsync(member.Commit, content,
                DropboxReplyShapes.Metadata(pair, DropboxProtocol.TagFile), attempt, member.Token).ConfigureAwait(false);
            if (content.Length != upload.Total) throw new ChangedUnderneathException(upload.Path);
            DropboxEntries.ValidateUploadResult(entry, upload.Path, sent, hash);
            api.ReportBatchOutcome(false);
            member.Done.TrySetResult(entry);
        }
        catch (HttpRequestException error)
        {
            member.Done.TrySetException(DirectFailure(error, member, attempt));
        }
        catch (Exception error) when (DropboxErrors.NoUsableReply(error, attempt, member.Token))
        {
            member.Done.TrySetException(DropboxErrors.Refused(upload.Path, error, DropboxProtocol.Upload, attempt));
        }
        catch (Exception error)
        {
            member.Done.TrySetException(error);
        }
    }

    // HTTP-owned throttles were reported by the API; only a structured non-throttle refusal may reset once.
    private Exception DirectFailure(HttpRequestException error, PendingPublish member, RequestAttempt attempt)
    {
        if (error is DropboxApiException refused && DropboxApi.Failure(refused).ValueKind == JsonValueKind.Object)
        {
            try
            {
                var (mapped, _) = DropboxErrors.MapUploadFailure(
                    DropboxApi.Failure(refused), member.ExpectedIsNull, member.Upload.Path, refused);
                if (!DropboxApi.HttpThrottled(refused) && DropboxErrors.IsClassifiedUploadFailure(mapped))
                    api.ReportBatchOutcome(false);
                return mapped;
            }
            catch (JsonException)
            {
                // A union without its tag falls back to the summary mapping below.
            }
        }
        return Mapped(DropboxProtocol.Upload, error, member, attempt);
    }

    private static Exception Mapped(string route, HttpRequestException error, PendingPublish member,
        RequestAttempt attempt)
    {
        var mapped = DropboxErrors.Map(route, error, member.ExpectedIsNull, member.Upload.Path, attempt);
        return mapped.IsAbsent ? error : mapped.Error;
    }

    private async Task FinishCohortAsync(List<PendingPublish> cohort)
    {
        var attempt = Attempt();
        try
        {
            var entries = cohort.Select(member => new
            {
                cursor = new { session_id = member.Upload.SessionId, offset = member.Upload.Total },
                commit = member.Commit
            }).ToArray();
            var targets = cohort.Select(member => (member.Upload.Path, member.ExpectedIsNull)).ToArray();
            var results = await api.FinishBatchAsync(new { entries }, pair, targets, attempt, cohort[0].Token)
                .ConfigureAwait(false);
            SettleCohort(cohort, results, attempt);
        }
        catch (HttpRequestException error)
        {
            foreach (var member in cohort)
                member.Done.TrySetException(Mapped(DropboxProtocol.UploadSessionFinishBatch, error, member, attempt));
        }
        catch (Exception error) when (DropboxErrors.NoUsableReply(error, attempt, cohort[0].Token))
        {
            foreach (var member in cohort)
                member.Done.TrySetException(DropboxErrors.Refused(member.Upload.Path, error,
                    DropboxProtocol.UploadSessionFinishBatch, attempt));
        }
        catch (Exception error)
        {
            foreach (var member in cohort) member.Done.TrySetException(error);
        }
    }

    // The one acceptance walk: every member completion and the single feedback event come from one results array;
    // a reliable throttle is one true event, and a failure surfaces on every member without repeating an effect.
    private void SettleCohort(List<PendingPublish> cohort,
        IReadOnlyList<(Entry? Entry, Exception? Error, bool KnownNoEffect, bool WriteThrottled)> results,
        RequestAttempt attempt)
    {
        var failures = new Exception?[cohort.Count];
        var throttled = false;
        var classified = true;
        for (int index = 0; index < failures.Length; index++)
        {
            var result = results[index];
            var upload = cohort[index].Upload;
            failures[index] = result.Error is { } memberError ? DropboxErrors.WithProviderWait(memberError, attempt) : null;
            throttled |= result.WriteThrottled;
            if (result.Error is { } error)
            {
                classified &= DropboxErrors.IsClassifiedUploadFailure(error);
                continue;
            }
            try
            {
                DropboxEntries.ValidateUploadResult(result.Entry!, upload.Path, upload.Total, upload.ContentHash!);
            }
            catch (ItemFailedException refused)
            {
                failures[index] = refused;
                classified = false;
            }
        }
        Exception? feedback = null;
        try
        {
            if (throttled) api.ReportBatchOutcome(true, attempt.Completion?.ProviderWait);
            else if (classified) api.ReportBatchOutcome(false);
        }
        catch (Exception error)
        {
            feedback = error;
        }
        for (int index = 0; index < failures.Length; index++)
        {
            var failure = failures[index] is { } own && feedback is not null
                ? new AggregateException(own, feedback) : failures[index] ?? feedback;
            if (failure is not null) cohort[index].Done.TrySetException(failure);
            else cohort[index].Done.TrySetResult(results[index].Entry!);
        }
    }

    /// <summary>Returns or creates a folder, refusing an occupying file.</summary>
    public async Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
    {
        var existing = await listing.ReadOneAsync(path, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing.IsFolder ? existing : throw new PathOccupiedException(path);

        var attempt = Attempt();
        try
        {
            var parsed = await api.RpcAsync(DropboxProtocol.CreateFolder,
                new { path = DropboxPaths.Remote(pair, path), autorename = false },
                DropboxReplyShapes.Metadata(pair, DropboxProtocol.TagFolder, nested: true), attempt, ct, subject: path).ConfigureAwait(false);
            return DropboxListing.Respelled(parsed, path);
        }
        catch (HttpRequestException error)
        {
            var mapped = DropboxErrors.Map(DropboxProtocol.CreateFolder, error, true, path, attempt);
            if (!mapped.IsAbsent) throw mapped.Error;
            existing = await listing.ReadOneAsync(path, ct).ConfigureAwait(false)
                ?? throw new ChangedUnderneathException(path);
            return existing.IsFolder ? existing : throw new PathOccupiedException(path);
        }
        catch (Exception error) when (DropboxErrors.NoUsableReply(error, attempt, ct))
        {
            throw DropboxErrors.Refused(path, error, DropboxProtocol.CreateFolder, attempt);
        }
    }

    /// <summary>Deletes the expected file under its revision condition. A folder is refused before sending: only the
    /// selected folder operation may remove a tree.</summary>
    public async Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
    {
        if (expected.IsFolder) throw new NotSupportedException();
        var attempt = Attempt();
        try
        {
            await RpcAsync(DropboxProtocol.Delete,
                new { path = DropboxPaths.Remote(pair, expected.Path), parent_rev = expected.Revision },
                DropboxReplyShapes.Void(), expected.Path, attempt, ct).ConfigureAwait(false);
            return new RemovalResult(RemovalOutcome.Removed, null, null, null);
        }
        catch (Exception error) when (DropboxErrors.NoUsableReply(error, attempt, ct))
        {
            throw DropboxErrors.Refused(expected.Path, error, DropboxProtocol.Delete, attempt);
        }
    }

    /// <summary>Moves an item without overwriting, including case-only renames. An unknown outcome of either hop
    /// keeps the whole intended move, so a later check never restarts it blindly.</summary>
    public async Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
    {
        var fromPath = expected.Path;
        string? intermediate = null;
        try
        {
            if (PathNames.CaseOnly(fromPath, newPath))
            {
                fromPath = intermediate = PathNames.CaseRenamePath(newPath, settings.CaseRenameSuffix);
                await MoveOneAsync(expected.Path, fromPath, ct).ConfigureAwait(false);
            }
            return await MoveOneAsync(fromPath, newPath, ct).ConfigureAwait(false);
        }
        catch (ItemFailedException failed) when (failed.Disposition == FailureDisposition.Permanent)
        {
            throw DropboxErrors.WithMove(failed,
                new IssueSubject.UnknownMove(expected.Id, expected.Path, newPath, intermediate, expected.IsFolder));
        }
    }

    private async Task<Entry> MoveOneAsync(string fromPath, string toPath, CancellationToken ct)
    {
        var attempt = Attempt();
        try
        {
            var parsed = await RpcAsync(DropboxProtocol.Move, new
            {
                from_path = DropboxPaths.Remote(pair, fromPath),
                to_path = DropboxPaths.Remote(pair, toPath),
                autorename = false,
                allow_ownership_transfer = false
            }, DropboxReplyShapes.Metadata(pair, nested: true), toPath, attempt, ct).ConfigureAwait(false);
            return DropboxListing.Respelled(parsed, toPath);
        }
        catch (Exception error) when (DropboxErrors.NoUsableReply(error, attempt, ct))
        {
            throw DropboxErrors.Refused(toPath, error, DropboxProtocol.Move, attempt);
        }
    }

    /// <summary>Submits only the persisted wire-ordered effects; uncertainty never authorizes replay.</summary>
    public partial Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
        IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct);

    /// <summary>Observes a job without granting mutation replay or mapping-settlement authority.</summary>
    public partial Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
        RequestAttempt attempt, CancellationToken ct);

    public async partial Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
        IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
    {
        if (submission.Phase != FolderSubmission.SubmissionPhase.Armed || effects.Count == 0 ||
            effects.Count > DropboxApiLimits.BatchMaxEntries)
            throw new ArgumentException(null, nameof(effects));
        var entries = effects.Select(WireEntry).ToArray();
        try
        {
            return await api.RpcAsync(DropboxProtocol.DeleteBatch, new { entries },
                DropboxReplyShapes.FolderSubmit(pair, effects.Count), attempt, ct).ConfigureAwait(false);
        }
        catch (Exception) when (attempt.Completion is { Result: RequestResult.UnknownWrite })
        {
            return new FolderSubmitResult.Unknown(TextKey.DropboxRefused);
        }
        catch (Exception error) when (attempt.Completion is { NoEffectProven: true } &&
            DropboxErrors.NoUsableReply(error, attempt, ct))
        {
            return new FolderSubmitResult.KnownNoEffect(TextKey.DropboxRefused);
        }
    }

    // Only a recursive folder or conditional file is a wire member; a folder takes no parent_rev.
    private object WireEntry(FolderRemovalEffect effect)
    {
        var expected = effect.Expected ?? throw new ArgumentException(null, nameof(effect));
        var path = DropboxPaths.Remote(pair, expected.Path);
        return effect.Kind switch
        {
            FolderEffectKind.RecursiveFolder when expected.IsFolder => new { path },
            FolderEffectKind.ConditionalFile when !expected.IsFolder && expected.Revision.Length != 0 =>
                new { path, parent_rev = expected.Revision },
            _ => throw new ArgumentException(null, nameof(effect))
        };
    }

    public async partial Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
        RequestAttempt attempt, CancellationToken ct)
    {
        try
        {
            return await api.RpcAsync(DropboxProtocol.DeleteBatchCheck, new { async_job_id = jobId },
                DropboxReplyShapes.FolderJob(pair, expectedCount), attempt, ct).ConfigureAwait(false);
        }
        catch (DropboxApiException error) when (DropboxErrors.IsInvalidFolderJob(error))
        {
            // The diagnostic path stays unbound: the folder runner supplies the durable operation identity.
            throw new ItemFailedException(string.Empty, TextKey.FailDropboxDeleteJobInvalid, error);
        }
    }

    private Task<T> RpcAsync<T>(string route, object arg, DropboxReply<T> reply, string path, RequestAttempt attempt,
        CancellationToken ct) =>
        MappedAsync(api.RpcAsync(route, arg, reply, attempt, ct, subject: path), route, false, path, attempt);

    private static async Task<T> MappedAsync<T>(Task<T> request, string route, bool expectedIsNull, string path,
        RequestAttempt? attempt)
    {
        try
        {
            return await request.ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            var mapped = DropboxErrors.Map(route, error, expectedIsNull, path, attempt);
            throw mapped.IsAbsent ? error : mapped.Error;
        }
    }

    // One queued publication: staged metadata, its condition and token, and its completion. No content buffer.
    private sealed class PendingPublish(DropboxStaged upload, object commit, bool expectedIsNull, CancellationToken token)
    {
        internal DropboxStaged Upload { get; } = upload;
        internal object Commit { get; } = commit;
        internal bool ExpectedIsNull { get; } = expectedIsNull;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<Entry> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<PendingPublish>? Node { get; set; }
        internal CancellationTokenRegistration Registration { get; set; }
    }
}
