namespace Massdex.SyncShark.V2;

public sealed partial class V2SyncHost
{
    private const string AllGroups = "All groups";

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncConflictComparison>> LoadOpenConflictComparisonsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SyncConflictComparison>>(
            _store.OpenNotices(null).Select(NoticeMapping.ToComparison).ToArray());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncConflictComparison>> LoadOpenConflictComparisonsPageAsync(int limit, int offset,
        string? search, string? group = null, CancellationToken cancellationToken = default, string? connectionId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SyncConflictComparison>>(
            Filtered(search, group, connectionId).Skip(offset).Take(limit).Select(NoticeMapping.ToComparison).ToArray());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncConflictSelection>> CaptureOpenConflictSelectionAsync(string? search,
        string? group = null, CancellationToken cancellationToken = default, string? connectionId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SyncConflictSelection>>(
            Filtered(search, group, connectionId).Select(NoticeMapping.ToSelection).ToArray());
    }

    /// <inheritdoc />
    public async Task<SyncConflictContent> LoadConflictContentAsync(string pairId, string relativePath,
        long maximumContentBytes = 8L * 1024 * 1024, CancellationToken cancellationToken = default,
        string? conflictId = null, long? conflictGeneration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pair = _store.Pair(pairId) ?? throw new InvalidOperationException("This folder pair is no longer available.");
        var local = _store.Live(pair.A.RootId, [relativePath]).SingleOrDefault();
        var dropbox = _store.Live(pair.B.RootId, [relativePath]).SingleOrDefault();
        var notice = _store.OpenNotices(pairId)
            .FirstOrDefault(item => string.Equals(item.Path, relativePath, StringComparison.Ordinal) &&
                (conflictId is null || item.NoticeId == conflictId));
        NoticeComparison? comparison = null;
        if (notice?.ConflictVersions is not null && _run is SyncRun engine)
            comparison = await engine.CompareNoticeAsync(pairId, notice.NoticeId!, cancellationToken);
        return new SyncConflictContent(
            ConflictId: conflictId ?? notice?.NoticeId ?? string.Empty,
            PairId: pairId,
            RelativePath: relativePath,
            LocalSize: local?.Size,
            DropboxSize: dropbox?.Size,
            LocalHash: local?.Hash,
            DropboxHash: dropbox?.Hash,
            LocalModifiedUtc: local?.Mtime,
            DropboxModifiedUtc: dropbox?.Mtime,
            LocalBytes: comparison?.SnapshotA is not null && comparison.A.Length == 0 ? null : comparison?.A,
            DropboxBytes: comparison?.SnapshotB is not null && comparison.B.Length == 0 ? null : comparison?.B,
            Note: comparison?.Note ?? (notice?.Detail ?? string.Empty) + " " + EdgeMessages.NotInV2Text("contentPreview"),
            MergeBaseText: comparison?.BaseText, MergedText: comparison?.MergedText,
            SnapshotA: comparison?.SnapshotA, SnapshotB: comparison?.SnapshotB);
    }

    /// <inheritdoc />
    public async Task<SyncAuthorityBatchResult> ResolveConflictSelectionCommandAsync(
        IReadOnlyList<SyncConflictResolutionRequest> requests, string? connectionId = null,
        CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null, Action<string>? admissionProgress = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        // Validate every target first and close nothing on failure: a stale selection must change nothing.
        var targets = new List<(Notice Notice, string Choice, string? Item, NoticeActionInput? Input)>();
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            var itemChoice = request.NoticeChoice is { } named && NoticeChoices.IsHeldItemChoice(named);
            var saved = _store.NoticeById(request.Conflict.PairId, request.Conflict.ConflictId);
            var notice = saved is { Closed: false } &&
                (itemChoice && saved.Value.Notice.DefenderHistory?.Cause == DefenderReview.HeldCause ||
                 string.Equals(saved.Value.Notice.Path, request.Conflict.RelativePath, StringComparison.Ordinal))
                ? saved.Value.Notice : null;
            if (notice is null)
                throw new InvalidOperationException("A selected notice changed or was already closed. Nothing was changed.");
            var pair = _store.Pair(notice.PairId);
            if (pair is null || !InScope(pair, connectionId))
                throw new InvalidOperationException("The selected pair is unavailable in this account. Refresh notices.");
            var options = NoticeChoices.Options(notice);
            if (request.NoticeChoice is { } choice)
            {
                if (!options.Any(option => option.Choice == choice))
                    throw new InvalidOperationException("This option is no longer available. Refresh notices and choose again.");
                targets.Add((notice, choice, itemChoice ? request.Conflict.RelativePath : null, request.NoticeInput));
                continue;
            }
            if (options.Count > 0)
                throw new InvalidOperationException("Choose one of the actions offered by this notice.");
            if (request.Resolution is SyncAuthorityConflictResolutionKind.KeepLocal
                or SyncAuthorityConflictResolutionKind.KeepDropbox)
                throw EdgeMessages.NotInV2("applyingChoice");
            if (notice.Kind == NoticeKind.UserDecision)
                throw new InvalidOperationException(EdgeMessages.Text("decisionNeeded"));
            targets.Add((notice, NoticeChoices.Accept, null, null));
        }
        foreach (var (notice, choice, item, input) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Apply first, acknowledge second. A failed operation keeps the notice retryable.
            // Pause is idempotent if interrupted between these two durable writes.
            if (StorageNotices.Handles(notice) && _store is SqliteStateStore storage)
            {
                ReconnectCloudFiles(cancellationToken);
                if (_cloudFiles is null) throw new IOException("Storage provider is unavailable; Retry later.");
                await StorageNotices.ApplyAsync(storage, _cloudFiles, notice, choice, cancellationToken);
                continue;
            }
            if (JournalSizing.IsNotice(notice))
            {
                await JournalSizing.ApplyAsync(_store, notice, choice, new AttendedElevatedAction(), cancellationToken);
                continue;
            }
            if (NoticeChoices.IsRetryNotice(notice))
            {
                messages[notice.NoticeId!] = await ApplyRetryNoticeChoiceAsync(notice, choice, cancellationToken, onProgress, input);
                continue;
            }
            if (NoticeChoices.IsQuarantineChoice(choice))
            {
                if (_run is not SyncRun quarantineRun) throw new InvalidOperationException("This engine cannot resolve quarantine holds.");
                var held = await quarantineRun.ResolveQuarantineAsync(notice.PairId, notice.NoticeId!, choice, cancellationToken, itemPath: item);
                if (held.Outcome != Outcome.Done) throw new InvalidOperationException(held.Reason ?? "The item stays held.");
                continue;
            }
            if (notice.ConflictVersions is not null)
            {
                if (_run is not SyncRun conflictEngine)
                    throw new InvalidOperationException("This engine cannot apply conflict notice choices.");
                var request = requests.Single(item => item.Conflict.ConflictId == notice.NoticeId);
                var result = await conflictEngine.ResolveConflictNoticeAsync(notice.PairId, notice.NoticeId!, choice,
                    request.NoticeDestinationPath, cancellationToken);
                if (result.Outcome != Outcome.Done)
                    throw new InvalidOperationException(result.Reason ?? "The choice is still pending; no notice was closed.");
                continue;
            }
            if (choice is NoticeChoices.UseOtherVersion or NoticeChoices.SaveOtherVersionAsCopy)
            {
                if (_run is not SyncRun engine)
                    throw new InvalidOperationException("This engine cannot restore a retained notice version. No file was changed.");
                var result = await engine.RestoreNoticeVersionAsync(notice.PairId, notice.NoticeId!,
                    choice == NoticeChoices.SaveOtherVersionAsCopy, cancellationToken);
                if (result.Outcome != Outcome.Done)
                    throw new InvalidOperationException(result.Reason ?? "The version was not restored; the notice remains open.");
                // The executor commit closes the notice atomically with verified completion.
                continue;
            }
            if (choice == NoticeChoices.PausePair)
                await SetPairEnabledAsync(notice.PairId, false, cancellationToken);
            _store.CloseNotice(notice.NoticeId!, choice);
        }
        var items = new List<SyncAuthorityBatchItemResult>();
        foreach (var group in targets.GroupBy(target => target.Notice.PairId))
        {
            var pairDef = _store.Pair(group.Key)
                ?? throw new InvalidDataException("This folder pair is no longer available.");
            var decisions = group.Select(target => new SyncDecision(target.Notice.Path, SyncDecisionKind.Recovered, messages.GetValueOrDefault(target.Notice.NoticeId!) ?? NoticeChoices.Result(target.Choice, target.Notice)))
                .ToArray();
            items.Add(new SyncAuthorityBatchItemResult(Mapped(pairDef), new SyncRunResult(group.Key, decisions), null, null));
        }
        return new SyncAuthorityBatchResult(items, false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> LoadHeldItemsAsync(string pairId, string noticeId, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var held = _store.NoticeById(pairId, noticeId) is { Closed: false } found &&
            found.Notice.DefenderHistory is { Cause: DefenderReview.HeldCause } metadata
            ? _store.DefenderHeldPaths(metadata.RootId, limit, pairId) : [];
        return Task.FromResult(held);
    }

    private IEnumerable<Notice> Filtered(string? search, string? group, string? connectionId)
    {
        foreach (var notice in _store.OpenNotices(null))
        {
            var pair = _store.Pair(notice.PairId);
            if (pair is null || !InScope(pair, connectionId)) continue;
            if (search is not null && !notice.Path.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (group is not null && group != AllGroups &&
                !string.Equals(group, NoticeMapping.GroupLabel(notice.Kind), StringComparison.Ordinal)) continue;
            yield return notice;
        }
    }
}
