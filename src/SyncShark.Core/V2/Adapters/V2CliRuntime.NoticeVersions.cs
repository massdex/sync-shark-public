namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    /// <summary>Explicit notice-only recovery; the general Recovery restore API remains separate.</summary>
    public async Task<SyncCliRunData> ResolveConflictAsync(string pairId, string relativePath, string choice,
        string? conflictId, long? generation, string? accountId, CancellationToken cancellationToken)
    {
        var pair = RequirePair(pairId);
        choice = choice.ToLowerInvariant();
        if (!NoticeChoices.IsRetainedVersionChoice(choice))
            throw new SyncCliUsageException("Use decisions resolve for a held winner choice, or choose a retained-version notice action.");
        if (conflictId is null || generation != 0)
            throw new SyncCliUsageException("Use the exact --key and --generation 0 from conflicts list.");
        if (accountId is not null && !string.Equals(pair.B.AccountId, accountId, StringComparison.Ordinal))
            throw new SyncCliUsageException("The selected notice is not in this account.");
        await WaitForDataLockAsync(_runGate, Console.Error.WriteLine, cancellationToken);
        try
        {
            var notice = _store.OpenNotices(pairId).FirstOrDefault(item => item.NoticeId == conflictId &&
                PairPath.Same(item.Path, relativePath));
            if (notice?.RetainedVersion is null)
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
