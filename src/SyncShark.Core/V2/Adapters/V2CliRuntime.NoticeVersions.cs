namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    /// <summary>Explicit notice-only recovery; the general Recovery restore API remains separate.</summary>
    public async Task<SyncCliRunData> ResolveConflictAsync(string pairId, string relativePath, string choice,
        string? conflictId, long? generation, string? accountId, CancellationToken cancellationToken)
    {
        var pair = RequirePair(pairId);
        choice = choice.ToLowerInvariant();
        if (!NoticeChoices.IsRetainedVersionChoice(choice) && !NoticeChoices.IsRetryChoice(choice))
            throw new SyncCliUsageException("Use decisions resolve for a held winner choice, or choose a retained-version notice action.");
        if (conflictId is null || generation != 0)
            throw new SyncCliUsageException("Use the exact --key and --generation 0 from conflicts list.");
        if (accountId is not null && !string.Equals(pair.B.AccountId, accountId, StringComparison.Ordinal))
            throw new SyncCliUsageException("The selected notice is not in this account.");
        var selected = _store.NoticeById(pairId, conflictId);
        if (selected is { Closed: false } &&
            (NoticeChoices.IsPairRetryNotice(selected.Value.Notice) || PairPath.Same(selected.Value.Notice.Path, relativePath)) &&
            _run.TryApplyImmediateNoticeChoice(pairId, conflictId, choice, cancellationToken))
        {
            if (!_borrowed && choice == NoticeChoices.RetryNow && _store.FirstSyncDone(pairId))
            {
                var report = await _run.IncrementalAsync(pairId, new RunProgressForwarder(Console.Error.WriteLine), cancellationToken);
                return new SyncCliRunData(pairId, pair.Name, report.Uploaded, report.Downloaded, 0,
                    report.KeptBoth, report.Held, report.Outcome == RunOutcomes.Stopped)
                { Failed = report.Failed, Deleted = report.Deleted, Moved = report.Moved, NeedsVerification = report.Uncertain, Diagnostics = report.Diagnostics };
            }
            return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
        }
        if (selected is { Closed: false } && !NoticeChoices.IsPairRetryNotice(selected.Value.Notice) && string.IsNullOrEmpty(relativePath))
            throw new SyncCliUsageException("--path is required for item notices.");
        var acceptedExclusion = selected is { Closed: false } &&
            NoticeChoices.CanQueueExclusion(selected.Value.Notice, relativePath, choice)
                ? selected.Value.Notice : null;
        await WaitForDataLockAsync(_runGate, Console.Error.WriteLine, cancellationToken);
        try
        {
            var current = _store.NoticeById(pairId, conflictId);
            var notice = acceptedExclusion ?? (current is { Closed: false } && PairPath.Same(current.Value.Notice.Path, relativePath)
                ? current.Value.Notice : null);
            if (notice is not null && NoticeChoices.IsRetryNotice(notice) && NoticeChoices.IsRetryChoice(choice))
            {
                await _run.ApplyRetryNoticeChoiceAsync(pairId, conflictId, choice, cancellationToken,
                    (previous, updated) => SaveNarrowerPair(previous, updated), acceptedExclusion);
                if (NoticeChoices.IsPairRetryNotice(notice))
                    return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
                if (choice is not (NoticeChoices.RetryNow or NoticeChoices.DeleteReviewed or NoticeChoices.KeepRestore) || !_store.FirstSyncDone(pairId))
                    return new SyncCliRunData(pairId, pair.Name, 0, 0, 0, 0, 0, false);
                var retryReport = await _run.IncrementalAsync(pairId, new RunProgressForwarder(Console.Error.WriteLine), cancellationToken);
                return new SyncCliRunData(pairId, pair.Name, retryReport.Uploaded, retryReport.Downloaded, 0,
                    retryReport.KeptBoth, retryReport.Held, retryReport.Outcome == RunOutcomes.Stopped)
                {
                    Failed = retryReport.Failed, Deleted = retryReport.Deleted, Moved = retryReport.Moved,
                    NeedsVerification = retryReport.Uncertain, Diagnostics = retryReport.Diagnostics
                };
            }
            if (notice?.RetainedVersion is null || !NoticeChoices.IsRetainedVersionChoice(choice))
                throw new SyncCliReviewException("That retained-version notice is no longer open. Refresh conflicts list; nothing was changed.");
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
        finally { _runGate.Release(); }
    }
}
