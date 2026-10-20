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
    public Task<SyncConflictContent> LoadConflictContentAsync(string pairId, string relativePath,
        long maximumContentBytes = 8L * 1024 * 1024, CancellationToken cancellationToken = default,
        string? conflictId = null, long? conflictGeneration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pair = _store.Pair(pairId) ?? throw new InvalidOperationException("This folder pair is no longer available.");
        var local = _store.Live(pair.A.RootId, [relativePath]).SingleOrDefault();
        var dropbox = _store.Live(pair.B.RootId, [relativePath]).SingleOrDefault();
        var notice = _store.OpenNotices(pairId)
            .FirstOrDefault(item => string.Equals(item.Path, relativePath, StringComparison.Ordinal));
        return Task.FromResult(new SyncConflictContent(
            ConflictId: conflictId ?? notice?.NoticeId ?? string.Empty,
            PairId: pairId,
            RelativePath: relativePath,
            LocalSize: local?.Size,
            DropboxSize: dropbox?.Size,
            LocalHash: local?.Hash,
            DropboxHash: dropbox?.Hash,
            LocalModifiedUtc: local?.Mtime,
            DropboxModifiedUtc: dropbox?.Mtime,
            LocalBytes: null,
            DropboxBytes: null,
            Note: (notice?.Detail ?? string.Empty) + " " + EdgeMessages.NotInV2Text("contentPreview")));
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
        var targets = new List<(Notice Notice, string Choice)>();
        foreach (var request in requests)
        {
            var notice = _store.OpenNotices(request.Conflict.PairId).FirstOrDefault(item =>
                string.Equals(item.NoticeId, request.Conflict.ConflictId, StringComparison.Ordinal) &&
                string.Equals(item.Path, request.Conflict.RelativePath, StringComparison.Ordinal));
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
                targets.Add((notice, choice));
                continue;
            }
            if (options.Count > 0)
                throw new InvalidOperationException("Choose one of the actions offered by this notice.");
            if (request.Resolution is SyncAuthorityConflictResolutionKind.KeepLocal
                or SyncAuthorityConflictResolutionKind.KeepDropbox)
                throw EdgeMessages.NotInV2("applyingChoice");
            if (notice.Kind == NoticeKind.UserDecision)
                throw new InvalidOperationException(EdgeMessages.Text("decisionNeeded"));
            targets.Add((notice, NoticeChoices.Accept));
        }
        foreach (var (notice, choice) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Apply first, acknowledge second. A failed operation keeps the notice retryable.
            // Pause is idempotent if interrupted between these two durable writes.
            if (NoticeChoices.IsRetryNotice(notice))
            {
                await ApplyRetryNoticeChoiceAsync(notice, choice, cancellationToken);
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
            var decisions = group.Select(target => new SyncDecision(target.Notice.Path, SyncDecisionKind.Recovered, NoticeChoices.Result(target.Choice, target.Notice)))
                .ToArray();
            items.Add(new SyncAuthorityBatchItemResult(Mapped(pairDef), new SyncRunResult(group.Key, decisions), null, null));
        }
        return new SyncAuthorityBatchResult(items, false);
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
