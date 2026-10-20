namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // One shared authority for app and CLI. These choices change scheduling or exclusion policy,
    // never turn an uncertain effect into deletion permission or mark an effect successful.
    internal async Task<bool> ApplyRetryNoticeChoiceAsync(string pairId, string noticeId, string choice, CancellationToken ct,
        Action<PairDef, PairDef>? saveNarrowerPair = null, Action<SyncAction, ActionResult>? removalResult = null)
    {
        string? observationSession = null;
        PairDef? observedPair = null;
        try
        {
            if ((choice is NoticeChoices.ReviewRemoval or NoticeChoices.ReviewRemovalA or NoticeChoices.ReviewRemovalB or NoticeChoices.ConfirmRemoval) &&
                _store is SqliteStateStore db)
            {
                // Only endpoint preparation needs admission. Network/disk subtree enumeration is independent
                // of other pairs and never holds the global run gate or mutates their live inventories.
                var input = await RunExclusiveAsync(null, async (_, token) =>
                {
                    var saved = _store.NoticeById(pairId, noticeId);
                    if (saved is null || saved.Value.Closed || !NoticeChoices.Options(saved.Value.Notice).Any(o => o.Choice == choice))
                        throw new SyncRunRefusedException("That review is no longer available. Refresh notices.");
                    var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is no longer available.");
                    RefuseLayout(pair);
                    await _endpoints.PrepareAsync(pair, token);
                    return (Pair: pair, Path: saved.Value.Notice.Path, A: _endpoints.Inventory(pair.A), B: _endpoints.Inventory(pair.B));
                }, ct);
                observedPair = input.Pair;
                observationSession = Guid.NewGuid().ToString("N");
                var session = observationSession;
                await Task.WhenAll(Read(input.A, Side.A), Read(input.B, Side.B));
                async Task Read(IInventorySource inventory, Side side) =>
                    await inventory.ReadSubtreeAsync(input.Path, rows =>
                    {
                        if (rows.Any(item => !PairPath.IsSelfOrUnder(item.Path, input.Path)))
                            throw new IOException("Folder listing escaped its requested subtree.");
                        db.StageRemovalItems(session, side, rows);
                        return Task.CompletedTask;
                    }, ct);
                // A root replacement cannot bind an observation from the old endpoint to the new one.
                if (_store.Pair(pairId) is not { } current || current.A != input.Pair.A || current.B != input.Pair.B)
                    throw new SyncRunRefusedException("The folder roots changed during review. Refresh the review; nothing was removed.");
            }
            return await ApplyRetryNoticeChoiceCoreAsync(pairId, noticeId, choice, ct, saveNarrowerPair, removalResult, observationSession, observedPair);
        }
        finally
        {
            if (observationSession is not null && _store is SqliteStateStore db) db.ClearRemovalObservation(observationSession);
        }
    }

    private Task<bool> ApplyRetryNoticeChoiceCoreAsync(string pairId, string noticeId, string choice, CancellationToken ct,
        Action<PairDef, PairDef>? saveNarrowerPair, Action<SyncAction, ActionResult>? removalResult, string? observationSession, PairDef? observedPair) =>
        RunExclusiveAsync(null, async (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            var saved = _store.NoticeById(pairId, noticeId);
            if (saved is null || saved.Value.Closed || !NoticeChoices.IsRetryNotice(saved.Value.Notice) ||
                !NoticeChoices.Options(saved.Value.Notice).Any(option => option.Choice == choice))
                throw new SyncRunRefusedException("That retry notice or option is no longer available. Refresh notices.");
            var notice = saved.Value.Notice;
            var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is no longer available.");
            if (observedPair is not null && (pair.A != observedPair.A || pair.B != observedPair.B))
                throw new SyncRunRefusedException("The folder roots changed during review. Refresh the review; nothing was removed.");
            if (NoticeChoices.IsRemovalReview(notice) || choice is NoticeChoices.ReviewRemoval or NoticeChoices.ReviewRemovalA or NoticeChoices.ReviewRemovalB)
            {
                await RemovalChoiceAsync(pair, notice, choice, token, removalResult, observationSession);
                return true;
            }
            if (NoticeChoices.IsRemovalAttention(notice))
            {
                if (choice == NoticeChoices.Dismiss) _store.CloseNotice(noticeId, choice);
                // Opening settings is navigation only; neither choice authorizes an effect or retries a removal.
                return true;
            }
            if (notice.ReasonKey == "deletion-review" && choice is NoticeChoices.DeleteReviewed or NoticeChoices.KeepRestore)
            {
                var retry = _store.SyncRetryAt(pairId, notice.Path);
                if (retry?.ProposedActions is not { Count: > 0 } proposed)
                    throw new SyncRunRefusedException("The deletion proposal is no longer available. Refresh notices.");
                var actions = choice == NoticeChoices.DeleteReviewed ? proposed : proposed
                    .Where(action => action.Kind == ActionKind.Delete && action.ExpectedTarget is not null)
                    .Select(action => action with
                    {
                        Kind = action.ExpectedTarget!.Kind == ItemKind.Folder ? ActionKind.MkDir : ActionKind.CopyNew,
                        Target = action.Target == Side.A ? Side.B : Side.A,
                        Source = action.ExpectedTarget, ExpectedTarget = null, RecoveryFirst = false,
                        OppositeAbsent = false, FrozenVictimSignature = null, ProviderEffect = null
                    }).ToArray();
                _store.SaveSyncRetry(pairId, retry with
                {
                    Kind = "reviewed-choice", ReviewedActions = actions, ProposedActions = null,
                    Held = null, HeldReviewed = false, RetryAfter = DateTimeOffset.MinValue
                });
                _store.CloseNotice(noticeId, choice);
                return true;
            }
            if (choice == NoticeChoices.RetryNow)
            {
                if (_store.SyncRetryAt(pairId, notice.Path) is { } retry)
                    _store.SaveSyncRetry(pairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                // Keep the notice visible until verified success. A retry request is not success.
                return true;
            }
            if (choice is NoticeChoices.Exclude or NoticeChoices.Ignore)
            {
                var paths = CreateExclusions.Validate(pair.Excluded.Concat([notice.Path]).ToArray());
                if (saveNarrowerPair is not null) saveNarrowerPair(pair, pair with { Excluded = paths });
                else _store.SavePair(pair with { Excluded = paths });
                _store.ClearSyncRetry(pairId, notice.Path);
                // Retain pending effects for verify-only reconciliation under the normal exclusion policy.
            }
            _store.CloseNotice(noticeId, choice);
            return true;
        }, ct);
}
