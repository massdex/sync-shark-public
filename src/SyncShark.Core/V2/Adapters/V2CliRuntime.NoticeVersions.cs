namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    /// <summary>Explicit notice-only recovery; the general Recovery restore API remains separate.</summary>
    public Task<SyncCliRunData> ResolveConflictAsync(string pairId, string relativePath, string choice,
        string? conflictId, long? generation, string? accountId, CancellationToken cancellationToken) =>
        ResolveNoticeAsync(pairId, relativePath, choice, conflictId, generation, accountId, cancellationToken, new());

    public async Task<SyncCliRunData> ResolveNoticeAsync(string pairId, string relativePath, string choice,
        string? conflictId, long? generation, string? accountId, CancellationToken cancellationToken, NoticeActionInput input)
    {
        var pair = RequirePair(pairId);
        var separator = choice.IndexOf('=');
        var destination = separator < 0 ? null : choice[(separator + 1)..];
        choice = (separator < 0 ? choice : choice[..separator]).ToLowerInvariant();
        if (!NoticeChoices.IsExplicitNoticeChoice(choice))
            throw new SyncCliUsageException("Use an explicit notice choice from conflicts list; ordinary winners cannot resolve a notice.");
        if (conflictId is null || generation != 0)
            throw new SyncCliUsageException("Use the exact --key and --generation 0 from conflicts list.");
        if (accountId is not null && !string.Equals(pair.B.AccountId, accountId, StringComparison.Ordinal))
            throw new SyncCliUsageException("The selected notice is not in this account.");
        if (JournalSizing.IsChoice(choice))
        {
            var journalNotice = _store.UnresolvedNotices(pairId).FirstOrDefault(item => item.NoticeId == conflictId &&
                PairPath.Same(item.Path, relativePath) && JournalSizing.IsNotice(item))
                ?? throw new SyncCliReviewException("Refresh the journal notice; it is no longer open.");
            await JournalSizing.ApplyAsync(_store, journalNotice, choice, new AttendedElevatedAction(), cancellationToken);
            return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
        }
        var selected = SelectedNotice(pairId, conflictId);
        if (selected is { Closed: false } && StorageNotices.HandlesAtPath(selected.Value.Notice, relativePath) &&
            _store is SqliteStateStore storage)
        {
            await StorageNotices.ApplyAsync(storage, StorageNoticeConnections, selected.Value.Notice, choice,
                cancellationToken, IsStoragePaused, Console.Error.WriteLine);
            return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
        }
        if (selected is { Closed: false } &&
            (NoticeChoices.IsPairRetryNotice(selected.Value.Notice) ||
             NoticeChoices.IsIndexFailure(selected.Value.Notice) ||
             PairPath.Same(selected.Value.Notice.Path, relativePath)) &&
            _run.TryApplyImmediateNoticeChoice(pairId, conflictId, choice, cancellationToken))
        {
            // A root-indexing retry wakes the independent index host; it never starts a sync run here.
            if (!_borrowed && choice == NoticeChoices.RetryNow && _store.FirstSyncDone(pairId) &&
                !NoticeChoices.IsIndexFailure(selected.Value.Notice))
            {
                var report = await _run.IncrementalAsync(pairId, new RunProgressForwarder(Console.Error.WriteLine), cancellationToken);
                return new SyncCliRunData(pairId, pair.Name, report.Uploaded, report.Downloaded, 0,
                    report.KeptBoth, report.Held, report.Outcome == RunOutcomes.Stopped)
                { Failed = report.Failed, Deleted = report.Deleted, Moved = report.Moved, NeedsVerification = report.Uncertain, Diagnostics = report.Diagnostics };
            }
            return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
        }
        if (selected is { Closed: false } && selected.Value.Notice.Path.Length > 0 &&
            !NoticeChoices.IsPairRetryNotice(selected.Value.Notice) &&
            !NoticeChoices.IsIndexFailure(selected.Value.Notice) && string.IsNullOrEmpty(relativePath))
            throw new SyncCliUsageException("--path is required for item notices.");
        // Quarantine owns its admission scope: some choices update shared, cross-pair holds,
        // and attended elevation deliberately runs outside its lease. Never wrap it in a pair lease.
        if (selected is { } quarantine && (quarantine.Notice.Quarantine is not null || quarantine.Notice.DefenderHistory is not null) &&
            NoticeChoices.IsQuarantineChoice(choice))
        {
            var heldItem = NoticeChoices.IsHeldItemChoice(choice) && quarantine.Notice.DefenderHistory?.Cause == DefenderReview.HeldCause;
            if ((!heldItem && !PairPath.Same(quarantine.Notice.Path, relativePath)) ||
                (quarantine.Closed && !_store.QuarantineRecoveryRetained(pairId, conflictId)))
                throw new SyncCliReviewException("That choice is unavailable. Refresh conflicts list; nothing was changed.");
            try
            {
                var held = await _run.ResolveQuarantineAsync(pairId, conflictId, choice, cancellationToken,
                    itemPath: heldItem ? relativePath : null);
                if (held.Outcome != Outcome.Done) throw new SyncCliReviewException(held.Reason ?? "The item stays held.");
                if (held.Reason is not null) Console.Error.WriteLine(held.Reason);
                return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
            }
            catch (SyncRunRefusedException error) { throw new SyncCliReviewException(error.Message); }
        }
        var acceptedExclusion = selected is { Closed: false } &&
            NoticeChoices.CanQueueExclusion(selected.Value.Notice, relativePath, choice)
                ? selected.Value.Notice : null;
        return await _run.WithPairAdmissionAsync(pairId, async () =>
        {
            try
            {
                var retained = SelectedNotice(pairId, conflictId);
                // A held-notice item choice names the held item in --path; every other choice names the notice's own path.
                // A root-indexing notice is root-level, so it never asks the caller for an item path.
                var itemChoice = NoticeChoices.IsHeldItemChoice(choice) && retained?.Notice.DefenderHistory?.Cause == DefenderReview.HeldCause;
                var notice = acceptedExclusion ?? (retained is { } found &&
                    (itemChoice || NoticeChoices.IsIndexFailure(found.Notice) || PairPath.Same(found.Notice.Path, relativePath)) &&
                    (!found.Closed || _store.QuarantineRecoveryRetained(pairId, conflictId)) ? found.Notice : null);
                if (notice is not null) conflictId = notice.NoticeId ?? conflictId;
                if (notice is not null && NoticeChoices.IsRetryNotice(notice) && NoticeChoices.IsRetryChoice(choice))
                {
                    var removed = 0;
                    var removalFailures = 0;
                    var removalUncertain = 0;
                    var choiceResult = await _run.ApplyRetryNoticeChoiceResultAsync(pairId, conflictId, choice, cancellationToken,
                        (previous, updated) => SaveNarrowerPair(previous, updated), (action, result) =>
                        {
                            if (result.Outcome == Outcome.Done && action.ExpectedTarget?.Kind == ItemKind.File) removed++;
                            if (result.Outcome == Outcome.Failed) removalFailures++;
                            if (result.Outcome == Outcome.Uncertain) removalUncertain++;
                        }, new RunProgressForwarder(Console.Error.WriteLine), acceptedExclusion: acceptedExclusion, input: input,
                        updateRebuildableRule: (currentPair, current, replacement) => UpdateRebuildableRule(currentPair, current, replacement));
                    Console.Error.WriteLine(choiceResult.Message);
                    if (!choiceResult.Accepted)
                        return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 1, false)
                        { NotAttempted = 1 };
                    if ((NoticeChoices.IsPairRetryNotice(notice) && choice != NoticeChoices.RetryNow) || NoticeChoices.IsIndexFailure(notice) || notice.ReasonKey == PlanNotices.PlanBoundExceededKey)
                        return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
                    if (choice is not (NoticeChoices.RestoreAgain or NoticeChoices.Rename or NoticeChoices.RetryNow or NoticeChoices.DeleteReviewed or NoticeChoices.KeepRestore) || !_store.FirstSyncDone(pairId))
                        return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false)
                        { Deleted = removed, Failed = removalFailures, NeedsVerification = removalUncertain };
                    var retryReport = await _run.IncrementalAsync(pairId, new RunProgressForwarder(Console.Error.WriteLine), cancellationToken);
                    return new SyncCliRunData(pairId, pair.Name, retryReport.Uploaded, retryReport.Downloaded, 0,
                        retryReport.KeptBoth, retryReport.Held, retryReport.Outcome == RunOutcomes.Stopped)
                    {
                        Failed = retryReport.Failed, Deleted = retryReport.Deleted, Moved = retryReport.Moved,
                        NeedsVerification = retryReport.Uncertain, Diagnostics = retryReport.Diagnostics
                    };
                }
                if (notice is null || !NoticeChoices.Options(notice).Any(option => option.Choice == choice))
                    throw new SyncCliReviewException("That choice is unavailable. Refresh conflicts list; nothing was changed.");
                if (notice.ConflictVersions is not null)
                {
                    if (choice == NoticeChoices.Compare)
                    {
                        var comparison = await _run.CompareNoticeAsync(pairId, conflictId, cancellationToken);
                        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
                            A = ConflictText.Decode(comparison.A), B = ConflictText.Decode(comparison.B),
                            comparison.BaseText, comparison.MergedText, comparison.Note,
                            comparison.SnapshotA, comparison.SnapshotB }));
                    }
                    else
                    {
                        var applied = await _run.ResolveConflictNoticeAsync(pairId, conflictId, choice, destination, cancellationToken);
                        if (applied.Outcome != Outcome.Done)
                            throw new SyncCliReviewException(applied.Reason ?? "The choice remains pending.");
                        Console.Error.WriteLine(applied.Reason);
                    }
                    return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
                }
                if (notice.RetainedVersion is null)
                    throw new SyncCliReviewException("This notice has no retained version. Nothing was changed.");
                if (choice == NoticeChoices.Dismiss)
                {
                    _store.CloseNotice(conflictId, choice);
                    return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
                }
                var result = await _run.RestoreNoticeVersionAsync(pairId, conflictId,
                    choice == NoticeChoices.SaveOtherVersionAsCopy, cancellationToken);
                if (result.Outcome != Outcome.Done)
                    throw new SyncCliReviewException(result.Reason ?? "The version was not restored; the notice remains open.");
                return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
            }
            catch (SyncRunRefusedException error) { throw new SyncCliReviewException(error.Message); }
        }, cancellationToken, Console.Error.WriteLine);
    }

    /// <summary>
    /// The open notice a choice names. A root-indexing notice lives physically on its lowest pair and is
    /// projected to the other pairs sharing that root, so a selection made through a projection resolves to
    /// the same physical row before any early lookup or stale-target check.
    /// </summary>
    private (Notice Notice, bool Closed)? SelectedNotice(string pairId, string noticeId)
    {
        var selected = _store.NoticeById(pairId, noticeId);
        if (selected is null && _store is SqliteStateStore storage &&
            storage.IndexNoticeById(pairId, noticeId) is { } physical)
            return (physical.Notice, false);
        return selected;
    }
}
