using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>
/// a large Dropbox folder is deleted in one delete_batch entry, then the change feed from the
/// plan's scan cursor proves what the job removed, and anything added or changed after the scan is restored.
/// </summary>
public sealed partial class DropboxWriter
{
    private const int FolderBatchEntries = 1000;
    private const int MaximumFeedPages = 100_000;

    private enum FeedState { Live, DeletedEarlier, DeletedByJob }

    // One durable provider state for the whole group, not one copy of the feed per victim.
    // RestorePending checkpoints all candidates together before recovery starts. When RestoreStarted exists,
    // only that per-path map proves submission; legacy checkpoints retain conservative pending semantics.
    private sealed record FolderRecovery(List<DropboxChange> Before, string Middle,
        string? JobId = null, bool[]? Completed = null, string[]? RestorePending = null,
        DateTimeOffset? SubmittedAt = null, List<DropboxChange>? After = null,
        string[]? Restored = null, bool ExpiredUnknown = false,
        Dictionary<string, string>? SettledUnrestored = null, string[]? Unobserved = null,
        Dictionary<string, string?>? RestoreStarted = null, Dictionary<string, DateTimeOffset>? RestoreRetryAfter = null,
        Dictionary<string, string?>? RestoreRequested = null);

    // Explicit user authority only: retain the exact submitted revision, not a fresh latest revision.
    internal static string AuthorizeRestoreAgain(string state, string folderPath)
    {
        var recovery = JsonSerializer.Deserialize<FolderRecovery>(state)
            ?? throw new InvalidDataException("Missing folder recovery checkpoint.");
        var started = new Dictionary<string, string?>(recovery.RestoreStarted ?? [], StringComparer.OrdinalIgnoreCase);
        var requested = new Dictionary<string, string?>(recovery.RestoreRequested ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var path in started.Keys.Where(path => PairPath.IsSelfOrUnder(path, folderPath)).ToArray())
        {
            requested[path] = started[path];
            started.Remove(path);
        }
        return JsonSerializer.Serialize(recovery with { RestoreStarted = started, RestoreRequested = requested });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FolderTreeOutcome>> DeleteFolderTreesAsync(string scanCursor,
        IReadOnlyList<FolderTree> trees, Func<IReadOnlyList<FolderTree>, string, Task> beforeSubmit, CancellationToken ct)
    {
        var outcomes = new List<FolderTreeOutcome>(trees.Count);
        var submit = new List<FolderTree>();
        FolderRecovery? recovery = null;
        var prepared = false;
        Task SaveAsync() => beforeSubmit(submit, JsonSerializer.Serialize(recovery));
        try
        {
            await _client.DeleteFoldersAsync(trees.Take(FolderBatchEntries)
                .Select(tree => DropboxItems.ToEntry(tree.Folder)).ToArray(), _settings,
                async job => { recovery = recovery! with { JobId = job }; await SaveAsync(); },
                async completed => { recovery = recovery! with { Completed = completed }; await SaveAsync(); },
                async () =>
                {
                    // Gate/root verification and durable intents precede this read. The owner-approved
                    // source-only probe follows it; then one durable group checkpoint precedes submission.
                    ct.ThrowIfCancellationRequested();
                    var (before, middle) = await ReadFeedAsync(scanCursor, ct);
                    foreach (var tree in trees)
                        if (submit.Count >= FolderBatchEntries || FolderReplaced(tree.Folder, before))
                            outcomes.Add(FolderTreeOutcome.Of(tree, FolderTreeStatus.NotSubmitted,
                                "The folder changed after the scan or exceeds the single batch limit."));
                        else submit.Add(tree);
                    if (submit.Count == 0) return Array.Empty<PilotRemoteEntry>();
                    // Owner-approved prepare slot: fresh source proof after the pre-feed and before
                    // the sole durable group checkpoint. A refusal takes existing guarded per-item fallback.
                    foreach (var tree in submit)
                        if (tree.RevalidateDelete is { } revalidate && !await revalidate().ConfigureAwait(false))
                        {
                            outcomes.AddRange(submit.Select(candidate => FolderTreeOutcome.Of(candidate,
                                FolderTreeStatus.NotSubmitted, "Source presence or absence is unconfirmed before submit.")));
                            submit.Clear();
                            return Array.Empty<PilotRemoteEntry>();
                        }
                    recovery = new FolderRecovery(before.Where(change => submit.Any(tree =>
                        PairPath.IsSelfOrUnder(change.Path, tree.Folder.Path))).ToList(), middle,
                        SubmittedAt: DateTimeOffset.UtcNow);
                    prepared = true; // A checkpoint failure may have committed; never fall back across that ambiguity.
                    await SaveAsync();
                    return submit.Select(tree => DropboxItems.ToEntry(tree.Folder)).ToArray();
                });
        }
        catch (Exception e) when (e is not PairPausedException && !(e is OperationCanceledException && ct.IsCancellationRequested && !prepared))
        {
            outcomes.AddRange((prepared ? submit : trees.Where(tree =>
                    !outcomes.Any(outcome => ReferenceEquals(outcome.Tree, tree))))
                .Select(tree => FolderTreeOutcome.Of(tree,
                    prepared ? FolderTreeStatus.Uncertain : FolderTreeStatus.NotSubmitted,
                    "The whole-folder delete could not be completed: " + Describe(e))));
            return outcomes;
        }
        if (submit.Count > 0)
            outcomes.AddRange(await FinishFolderGroupAsync(submit, recovery!, async updated =>
            { recovery = updated; await SaveAsync(); }));
        return outcomes;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FolderTreeOutcome>> ReconcileFolderTreesAsync(string scanCursor,
        IReadOnlyList<FolderTree> trees, string recoveryState, Func<string, Task> checkpoint, CancellationToken ct)
    {
        try
        {
            var recovery = JsonSerializer.Deserialize<FolderRecovery>(recoveryState)
                ?? throw new InvalidDataException("The whole-folder checkpoint is missing.");
            if (recovery.After is not null)
                return await FinishFolderGroupAsync(trees, recovery,
                    updated => checkpoint(JsonSerializer.Serialize(updated)));
            if (recovery.Completed is null)
            {
                if (recovery.JobId is not null)
                {
                    var job = await _client.ReconcileFolderDeleteJobAsync(recovery.JobId, trees.Count, ct);
                    if (!job.Unavailable && job.Completed is null)
                        throw new InvalidDataException("The whole-folder delete job is still pending.");
                    recovery = recovery with { JobId = job.Unavailable ? null : recovery.JobId, Completed = job.Completed };
                    await checkpoint(JsonSerializer.Serialize(recovery));
                }
                if (recovery.JobId is null)
                {
                    var (after, _) = await ReadFeedAsync(recovery.Middle, ct);
                    var latest = new Dictionary<string, DropboxChange>(StringComparer.OrdinalIgnoreCase);
                    // A victim already absent before submission is settled too, unless the job window
                    // subsequently shows it live again. It need not wait for the unknown-submit grace.
                    foreach (var change in recovery.Before.Concat(after)) latest[change.Path] = change;
                    var allDeleted = trees.SelectMany(tree => tree.Victims
                            .Where(item => !SyncIgnorePolicy.IsIgnored(item.Path, item.Kind == ItemKind.Folder, _ignoreRules))
                            .Append(tree.Folder))
                        .All(item => latest.TryGetValue(item.Path, out var change) && change.Entry is null);
                    var grace = _settings.UnknownCommitGracePeriod > TimeSpan.FromHours(2)
                        ? _settings.UnknownCommitGracePeriod : TimeSpan.FromHours(2);
                    // Owner policy, NOT an API lifetime guarantee: unknown submissions are assumed quiescent after
                    // at least two hours. Known pending jobs never take this expiry branch.
                    if (!allDeleted && (recovery.SubmittedAt is null || DateTimeOffset.UtcNow - recovery.SubmittedAt < grace))
                        throw new InvalidDataException("The unknown whole-folder submit is still within its observation grace period.");
                    return await FinishFolderGroupAsync(trees, recovery,
                        updated => checkpoint(JsonSerializer.Serialize(updated)), after, expiredUnknown: !allDeleted);
                }
            }
            return await FinishFolderGroupAsync(trees, recovery,
                updated => checkpoint(JsonSerializer.Serialize(updated)));
        }
        catch (Exception e) when (e is not PairPausedException && !(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            return trees.Select(tree => FolderTreeOutcome.Of(tree, FolderTreeStatus.Uncertain,
                "The whole-folder recovery could not be checked: " + Describe(e))).ToArray();
        }
    }

    private async Task<IReadOnlyList<FolderTreeOutcome>> FinishFolderGroupAsync(IReadOnlyList<FolderTree> trees,
        FolderRecovery recovery, Func<FolderRecovery, Task> checkpoint,
        List<DropboxChange>? observed = null, bool expiredUnknown = false)
    {
        try
        {
            if (recovery.After is null)
            {
                if (observed is null && (recovery.Completed is null || recovery.Completed.Length != trees.Count))
                    throw new InvalidDataException("The whole-folder terminal results do not match the frozen group.");
                var window = observed ?? (await ReadFeedAsync(recovery.Middle, CancellationToken.None)).Changes;
                // Close the job's observation window durably BEFORE any recovery effects. A later reconcile
                // must not interpret subsequent user deletes as effects of this already terminal job.
                var compact = CompactFolderWindow(trees, recovery.Before, window);
                recovery = recovery with
                {
                    After = compact.Exceptions, Unobserved = compact.Unobserved,
                    ExpiredUnknown = expiredUnknown
                };
                await checkpoint(recovery);
            }
            var after = recovery.After;
            var results = new List<FolderTreeOutcome>();
            var pending = new HashSet<string>(recovery.RestorePending ?? [], StringComparer.OrdinalIgnoreCase);
            var restored = new HashSet<string>(recovery.Restored ?? [], StringComparer.OrdinalIgnoreCase);
            var settled = new Dictionary<string, string>(recovery.SettledUnrestored ?? [], StringComparer.OrdinalIgnoreCase);
            var started = new Dictionary<string, string?>(recovery.RestoreStarted ?? [], StringComparer.OrdinalIgnoreCase);
            var retryAfter = new Dictionary<string, DateTimeOffset>(recovery.RestoreRetryAfter ?? [], StringComparer.OrdinalIgnoreCase);
            var requested = new Dictionary<string, string?>(recovery.RestoreRequested ?? [], StringComparer.OrdinalIgnoreCase);
            var unobserved = recovery.Unobserved?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            async Task SaveRecoveryAsync()
            {
                try { await checkpoint(recovery); }
                catch (Exception error) when (error is not PairPausedException) { throw new RecoveryCheckpointException(error); }
            }
            for (var i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                // Even a refused folder gets the feed check: partial effects must not bypass recovery.
                var resumePending = (recovery.RestoreStarted is null ? pending : started.Keys.AsEnumerable())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var result = await CheckTreeAsync(tree, recovery.Before, after, resumePending, restored, settled,
                    started, retryAfter, requested, unobserved, recovery.Completed?[i] == false, async paths =>
                {
                    var candidates = paths.ToArray();
                    if (candidates.Length == 0) return;
                    recovery = recovery with { RestorePending = candidates, RestoreStarted = started };
                    await SaveRecoveryAsync();
                }, async (path, revision) =>
                {
                    started[path] = revision;
                    recovery = recovery with { RestoreStarted = started, RestorePending = null, RestoreRequested = requested };
                    await SaveRecoveryAsync();
                }, async path =>
                {
                    started.Remove(path);
                    retryAfter[path] = DateTimeOffset.UtcNow + _settings.AutoRetryDelay;
                    recovery = recovery with { RestoreStarted = started, RestorePending = null,
                        RestoreRetryAfter = retryAfter };
                    await SaveRecoveryAsync();
                }, async (path, reason) =>
                {
                    if (reason is null) restored.Add(path);
                    else settled[path] = reason;
                    started.Remove(path);
                    pending.Remove(path);
                    retryAfter.Remove(path);
                    requested.Remove(path);
                    recovery = recovery with { Restored = restored.ToArray(), SettledUnrestored = settled,
                        RestoreStarted = started, RestorePending = null, RestoreRetryAfter = retryAfter, RestoreRequested = requested };
                    await SaveRecoveryAsync();
                });
                if (recovery.ExpiredUnknown && result.Status == FolderTreeStatus.Completed)
                    result = result with
                    {
                        Kept = result.Kept.Concat(result.Unconfirmed).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        Unconfirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    };
                results.Add(result);
            }
            return results;
        }
        catch (Exception e) when (e is not PairPausedException)
        {
            return trees.Select(tree => FolderTreeOutcome.Of(tree, FolderTreeStatus.Uncertain,
                "The change feed after the whole-folder delete could not be read: " + Describe(e))).ToArray();
        }
    }

    // Builds the deleted set from the feed entries at or under the folder, restores what was not planned, and says
    // which victims the delete really removed.
    private async Task<FolderTreeOutcome> CheckTreeAsync(FolderTree tree, IReadOnlyList<DropboxChange> before,
        IReadOnlyList<DropboxChange> after, IReadOnlySet<string> restorePending,
        IReadOnlySet<string> completedRestores, Dictionary<string, string> settledUnrestored,
        IReadOnlyDictionary<string, string?> started, IReadOnlyDictionary<string, DateTimeOffset> retryAfter,
        IReadOnlyDictionary<string, string?> requested, IReadOnlySet<string>? unobserved, bool refused,
        Func<IEnumerable<string>, Task> recordRestores, Func<string, string?, Task> recordStarted,
        Func<string, Task> recordNotApplied, Func<string, string?, Task> recordSettled)
    {
        try
        {
            var root = tree.Folder.Path;
            var planned = tree.Victims.Append(tree.Folder)
                .ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            var state = new Dictionary<string, FeedState>(StringComparer.OrdinalIgnoreCase);
            var kinds = new Dictionary<string, ItemKind>(StringComparer.OrdinalIgnoreCase);
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool IgnoredChild(string path)
            {
                if (PairPath.Same(path, root)) return false;
                var kind = kinds.TryGetValue(path, out var observedKind) ? observedKind :
                    planned.TryGetValue(path, out var victim) ? victim.Kind : (ItemKind?)null;
                // Tombstones have no kind. Without an observed/planned kind, only a rule matching both
                // possibilities proves exclusion (not a filename rule accidentally matching a folder).
                return kind is { } known
                    ? SyncIgnorePolicy.IsIgnored(path, known == ItemKind.Folder, _ignoreRules)
                    : SyncIgnorePolicy.IsIgnored(path, false, _ignoreRules) &&
                      SyncIgnorePolicy.IsIgnored(path, true, _ignoreRules);
            }
            void Observe(DropboxChange change, bool byJob)
            {
                if (!PairPath.IsSelfOrUnder(change.Path, root)) return;
                if (change.Entry is null)
                {
                    state[change.Path] = byJob ? FeedState.DeletedByJob : FeedState.DeletedEarlier;
                    return;
                }
                var item = DropboxItems.ToItem(change.Entry);
                state[change.Path] = FeedState.Live;
                kinds[change.Path] = item.Kind;
                // Unexpected: not a frozen victim, or a victim that is now another object, kind or file revision.
                if (!planned.TryGetValue(change.Path, out var victim) || victim.Kind != item.Kind ||
                    !string.Equals(victim.ObjectId, item.ObjectId, StringComparison.Ordinal) ||
                    (item.Kind == ItemKind.File &&
                     !string.Equals(victim.VersionId, item.VersionId, StringComparison.Ordinal)))
                    changed.Add(change.Path);
            }
            foreach (var change in before) Observe(change, false);
            // Compact checkpoints omit ordinary victim tombstones. Missing observations stay explicit.
            if (unobserved is not null)
                foreach (var path in planned.Keys)
                    if (!unobserved.Contains(path)) state[path] = FeedState.DeletedByJob;
            foreach (var change in after) Observe(change, true);
            var unknownRestores = new List<string>();
            foreach (var path in restorePending.Where(path => PairPath.IsSelfOrUnder(path, root) &&
                         !completedRestores.Contains(path) && !settledUnrestored.ContainsKey(path) && !IgnoredChild(path)))
            {
                var current = await ObserveAsync(path, CancellationToken.None);
                if (current is not null && started.TryGetValue(path, out var revision) &&
                    (revision is null ? current.Kind == ItemKind.Folder : current.VersionId == revision))
                {
                    await recordSettled(path, null);
                    state[path] = FeedState.Live;
                }
                else if (current is null)
                    // Absence cannot distinguish a failed request from a completed restore later deleted
                    // by the user. Retain the Started authority and only observe; never replay or settle it.
                    unknownRestores.Add(path);
                else
                    await recordSettled(path, path + " (path now in use)");
            }
            // A completed recovery effect belongs to this job, not to future user edits/deletions.
            // Mark it settled instead of replaying it from the frozen tombstone on every resume.
            foreach (var path in completedRestores.Where(path => PairPath.IsSelfOrUnder(path, root)))
            { state[path] = FeedState.Live; changed.Add(path); }
            changed.RemoveWhere(IgnoredChild);

            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unconfirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var restore = new List<(string Path, ItemKind? Kind)>();
            foreach (var (path, victim) in planned)
            {
                if (IgnoredChild(path)) continue;
                if (!state.TryGetValue(path, out var seen)) unconfirmed.Add(path);
                else if (seen == FeedState.Live) kept.Add(path);
                else if (seen == FeedState.DeletedByJob && changed.Contains(path))
                    restore.Add((path, kinds.TryGetValue(path, out var kind) ? kind : victim.Kind));
            }
            foreach (var (path, seen) in state)
                if (seen == FeedState.DeletedByJob && !planned.ContainsKey(path) && !IgnoredChild(path))
                    restore.Add((path, kinds.TryGetValue(path, out var kind) ? kind : null));

            // Resumed pending effects were observed above; their live state is no longer read from a frozen feed.
            var restored = restorePending.Concat(changed).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => PairPath.IsSelfOrUnder(path, root) && !IgnoredChild(path) &&
                    state.TryGetValue(path, out var seen) && seen == FeedState.Live).ToList();
            var unrestored = new List<string>();
            foreach (var (path, reason) in settledUnrestored.Where(entry =>
                         PairPath.IsSelfOrUnder(entry.Key, root) && !IgnoredChild(entry.Key)))
            { kept.Add(path); unrestored.Add(reason); }
            var unknownPaths = unknownRestores.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Restoring a child also creates parents. Do not indirectly replay an unknown folder restore.
            restore.RemoveAll(entry => settledUnrestored.ContainsKey(entry.Path) ||
                restorePending.Contains(entry.Path) || PairPath.Ancestors(entry.Path).Any(unknownPaths.Contains));
            unrestored.AddRange(unknownRestores.Select(path => path +
                " (restore submission unknown; verify only, never automatically replayed)"));
            async Task SettledAsync(string path, string reason)
            { await recordSettled(path, reason); unrestored.Add(reason); kept.Add(path); }
            string? refusal = null;
            if (refused && unconfirmed.Count > 0)
            {
                refusal = $"Dropbox refused the folder delete; {unconfirmed.Count} unconfirmed victim(s) were skipped.";
                kept.UnionWith(unconfirmed);
                unconfirmed.Clear();
            }
            // Candidate marker is not proof of an attempted effect; RestoreStarted distinguishes new checkpoints.
            await recordRestores(restore.Select(entry => entry.Path));
            string? failure = null;
            var retryableFailure = unknownRestores.Count > 0;
            if (retryableFailure) failure = "A submitted restore remains unverified. Check Dropbox history or verify again; absence is not success.";
            // An unexpected folder is recreated (it has no content of its own; its files are restored below), shallowest
            // first, so a restored file's parents already exist. A file goes back to its latest revision.
            foreach (var (path, _) in restore.Where(entry => entry.Kind == ItemKind.Folder)
                         .OrderBy(entry => PairPath.Depth(entry.Path)))
            {
                if (PairPath.Ancestors(path).Any(parent => started.ContainsKey(parent) ||
                    retryAfter.TryGetValue(parent, out var parentDue) && parentDue > DateTimeOffset.UtcNow))
                { retryableFailure = true; unrestored.Add(path); failure ??= "A parent restore needs verification or a due retry."; continue; }
                if (retryAfter.TryGetValue(path, out var due) && due > DateTimeOffset.UtcNow)
                { retryableFailure = true; unrestored.Add(path); failure ??= "Restore retry is not due yet."; continue; }
                try
                {
                    if (await ObserveAsync(path, CancellationToken.None) is { } current)
                    {
                        if (current.Kind == ItemKind.Folder)
                        { await recordSettled(path, null); restored.Add(path); }
                        else await SettledAsync(path, path + " (path now in use)");
                        continue;
                    }
                    await _client.RestoreDeletedFolderAsync(path, CancellationToken.None, () => recordStarted(path, null));
                    await recordSettled(path, null);
                    restored.Add(path);
                }
                catch (Exception e) when (e is not (RecoveryCheckpointException or PairPausedException))
                {
                    var rejection = (e as FolderRestoreNotAppliedException)?.InnerException ?? e;
                    if ((e is FolderRestoreNotAppliedException || !started.ContainsKey(path)) && PermanentRestoreRejection(rejection))
                    {
                        await SettledAsync(path, path + " (restore rejected: " + rejection.Message +
                            "; resolve the provider restriction or occupied path, then restore from Dropbox history)");
                        continue;
                    }
                    if (e is FolderRestoreNotAppliedException || !started.ContainsKey(path))
                        await recordNotApplied(path);
                    if (e is FolderRestoreNotAppliedException { InnerException: PairPausedException pause }) throw pause;
                    unrestored.Add(path);
                    retryableFailure = true;
                    failure ??= Describe(e);
                }
            }
            foreach (var (path, kind) in restore.Where(entry => entry.Kind != ItemKind.Folder))
            {
                if (PairPath.Ancestors(path).Any(parent => started.ContainsKey(parent) ||
                    retryAfter.TryGetValue(parent, out var parentDue) && parentDue > DateTimeOffset.UtcNow))
                { retryableFailure = true; unrestored.Add(path); failure ??= "A parent restore needs verification or a due retry."; continue; }
                if (retryAfter.TryGetValue(path, out var due) && due > DateTimeOffset.UtcNow)
                { retryableFailure = true; unrestored.Add(path); failure ??= "Restore retry is not due yet."; continue; }
                try
                {
                    var revision = requested.TryGetValue(path, out var selected) ? selected :
                        (await _client.ListFolderDeleteRevisionsAsync(path, kind is null, CancellationToken.None)).FirstOrDefault()?.Revision;
                    if (revision is null)
                    {
                        // DeletedMetadata carries no kind. An empty history cannot prove an empty folder rather
                        // than an expired file; never fabricate a directory. This cannot improve on another run.
                        await SettledAsync(path, path + (kind is null ? " (type unknown)" : " (revision unavailable)"));
                        continue;
                    }
                    // restore has no conditional no-overwrite option. Owner accepts only the residual race AFTER
                    // this absent check; an already occupied path is left alone and explicitly reported.
                    if (await ObserveAsync(path, CancellationToken.None) is not null)
                    {
                        await SettledAsync(path, path + " (path now in use)");
                        continue;
                    }
                    var returned = await _client.RestoreDeletedFileAsync(path, revision, CancellationToken.None,
                        () => recordStarted(path, revision));
                    if (!string.Equals(returned.Revision, revision, StringComparison.Ordinal))
                    {
                        await SettledAsync(path, path + " (restore revision differs; inspect Dropbox version history)");
                    }
                    else
                    {
                        await recordSettled(path, null);
                        restored.Add(path);
                    }
                }
                catch (Exception e) when (e is not (RecoveryCheckpointException or PairPausedException))
                {
                    var rejection = (e as FolderRestoreNotAppliedException)?.InnerException ?? e;
                    if ((e is FolderRestoreNotAppliedException || !started.ContainsKey(path)) && PermanentRestoreRejection(rejection))
                    {
                        await SettledAsync(path, path + " (restore rejected: " + rejection.Message +
                            "; resolve the provider restriction or occupied path, then restore from Dropbox history)");
                        continue;
                    }
                    if (e is FolderRestoreNotAppliedException || !started.ContainsKey(path))
                        await recordNotApplied(path);
                    if (e is FolderRestoreNotAppliedException { InnerException: PairPausedException pause }) throw pause;
                    unrestored.Add(path);
                    retryableFailure = true;
                    failure ??= Describe(e);
                }
            }
            // A restored item brings its folders back, so those victims were not removed after all.
            foreach (var path in restored) kept.Add(path);
            foreach (var path in kept.ToArray())
            {
                foreach (var ancestor in PairPath.Ancestors(path))
                    if (PairPath.IsSelfOrUnder(ancestor, root)) kept.Add(ancestor);
            }
            restored.Sort(StringComparer.OrdinalIgnoreCase);
            unrestored.Sort(StringComparer.OrdinalIgnoreCase);
            return !retryableFailure
                ? new FolderTreeOutcome(tree, FolderTreeStatus.Completed, refusal, kept, unconfirmed, restored, unrestored)
                : new FolderTreeOutcome(tree, FolderTreeStatus.Uncertain,
                    $"Could not restore {unrestored.Count} item(s) added or changed after the scan: {failure}",
                    kept, unconfirmed, restored, unrestored);
        }
        catch (Exception e) when (e is not PairPausedException)
        {
            return FolderTreeOutcome.Of(tree, FolderTreeStatus.Uncertain,
                "The whole-folder delete could not be checked: " + Describe(e));
        }
    }

    private static bool PermanentRestoreRejection(Exception error) =>
        error is DropboxApiException { StatusCode: { } status } && (int)status is >= 400 and < 500 &&
        status is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.RequestTimeout);

    private sealed class RecoveryCheckpointException(Exception inner) : Exception("Recovery checkpoint failed.", inner);

    // True when the feed shows the folder's own path deleted, retyped or replaced by another object since the scan.
    private static bool FolderReplaced(ItemState folder, IReadOnlyList<DropboxChange> feed) =>
        feed.Any(change => PairPath.Same(change.Path, folder.Path) &&
            (change.Entry is null || change.Entry.Kind != PilotEntryKind.Directory ||
             !string.Equals(change.Entry.Id, folder.ObjectId, StringComparison.Ordinal)));

    // Every change page from the cursor to the end of the feed, and the cursor after it.
    private async Task<(List<DropboxChange> Changes, string Cursor)> ReadFeedAsync(string cursor, CancellationToken ct)
    {
        var changes = new List<DropboxChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { cursor };
        for (var pages = 1; ; pages++)
        {
            if (pages > MaximumFeedPages)
                throw new InvalidDataException("Dropbox exceeded the bounded change-feed page limit.");
            var page = await _client.ReadChangePageAsync(cursor, ct);
            changes.AddRange(page.Changes);
            cursor = page.Cursor;
            if (!page.HasMore) return (changes, cursor);
            if (!seen.Add(cursor)) throw new InvalidDataException("Dropbox cycled a change-feed cursor.");
        }
    }

    private static string Describe(Exception e) => e.GetType().Name + ": " + e.Message;
}
