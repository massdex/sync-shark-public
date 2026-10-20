namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private readonly object _noticeChoiceGate = new();

    internal void UpdatePairSettings(Action update)
    {
        lock (_noticeChoiceGate) update();
    }

    internal Task UpdatePairSettingsAsync(Action update, CancellationToken ct) =>
        RunExclusiveAsync(null, (_, _) => { update(); return Task.FromResult(true); }, ct);

    internal bool TryApplyImmediateNoticeChoice(string pairId, string noticeId, string choice, CancellationToken ct)
    {
        if (choice is not (NoticeChoices.RetryNow or NoticeChoices.Ignore)) return false;
        lock (_noticeChoiceGate)
        {
            ct.ThrowIfCancellationRequested();
            var saved = _store.NoticeById(pairId, noticeId);
            if (saved is null || saved.Value.Closed || !NoticeChoices.IsRetryNotice(saved.Value.Notice) ||
                !NoticeChoices.Options(saved.Value.Notice).Any(option => option.Choice == choice)) return false;
            var notice = saved.Value.Notice;
            if (NoticeChoices.IsPairRetryNotice(notice)) return ApplyPairNoticeChoice(pairId, noticeId, choice, ct);
            if (choice != NoticeChoices.RetryNow) return false;
            _store.RequestSyncRetryNow(pairId, notice.Path);
            return true; // scheduler will pick up retries; no synchronous run or effect here
        }
    }

    internal bool ApplyPairNoticeChoice(string pairId, string noticeId, string choice, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var saved = _store.NoticeById(pairId, noticeId);
        if (saved is null || saved.Value.Closed || !NoticeChoices.IsPairRetryNotice(saved.Value.Notice) ||
            !NoticeChoices.Options(saved.Value.Notice).Any(option => option.Choice == choice))
            throw new SyncRunRefusedException("That pair notice or option is no longer available. Refresh notices.");
        if (choice == NoticeChoices.Ignore) _store.CloseNotice(noticeId, choice);
        return true;
    }

    // One shared authority for app and CLI. These choices change scheduling or exclusion policy,
    // never turn an uncertain effect into deletion permission or mark an effect successful.
    internal Task<bool> ApplyRetryNoticeChoiceAsync(string pairId, string noticeId, string choice, CancellationToken ct,
        Action<PairDef, PairDef>? saveNarrowerPair = null, Notice? acceptedExclusion = null) =>
        RunExclusiveAsync(null, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            var saved = _store.NoticeById(pairId, noticeId);
            // A validated exclusion accepted before waiting remains user intent even if the run
            // resolves its notice. Read the pair only AFTER admission, never save a run-start snapshot.
            var notice = acceptedExclusion ?? (saved is { Closed: false } ? saved.Value.Notice : null);
            if (notice is null || !NoticeChoices.IsRetryNotice(notice) ||
                !NoticeChoices.Options(notice).Any(option => option.Choice == choice) ||
                (acceptedExclusion is not null && (NoticeChoices.IsPairRetryNotice(notice) ||
                    choice is not (NoticeChoices.Ignore or NoticeChoices.Exclude) || notice.NoticeId != noticeId)))
                throw new SyncRunRefusedException("That retry notice or option is no longer available. Refresh notices.");
            var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is no longer available.");
            if (NoticeChoices.IsPairRetryNotice(notice))
            {
                if (choice == NoticeChoices.Ignore) _store.CloseNotice(noticeId, choice);
                // A pair failure is not a path exclusion or item retry. The host wakes the pair.
                return Task.FromResult(true);
            }
            if (NoticeChoices.IsRemovalAttention(notice))
            {
                if (choice == NoticeChoices.Dismiss) _store.CloseNotice(noticeId, choice);
                // Opening settings is navigation only; neither choice authorizes an effect or retries a removal.
                return Task.FromResult(true);
            }
            if (notice.ReasonKey == "deletion-review" && choice is NoticeChoices.DeleteReviewed or NoticeChoices.KeepRestore)
            {
                // A folder or flagged survivor never carries deletion authority, even if a fabricated
                // request bypasses the option list.
                if (choice == NoticeChoices.DeleteReviewed && notice.Rule == NoticeChoices.DeletionReviewRestoreOnlyRule)
                    throw new SyncRunRefusedException(
                        "This item cannot be deleted automatically. Choose Keep / restore or Ignore; nothing was deleted.");
                var retry = _store.SyncRetryAt(pairId, notice.Path);
                if (retry?.ProposedActions is not { Count: > 0 } proposed)
                    throw new SyncRunRefusedException("The deletion proposal is no longer available. Refresh notices.");
                // Explicit notice deletion is Recovery-first, even if cached history would let
                // automatic reviewed propagation skip Recovery for identical content.
                var actions = choice == NoticeChoices.DeleteReviewed
                    ? proposed.Select(action => action.Kind == ActionKind.Delete ? action with { RecoveryFirst = true } : action).ToArray()
                    : proposed
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
                return Task.FromResult(true);
            }
            if (choice == NoticeChoices.RetryNow)
            {
                if (_store.SyncRetryAt(pairId, notice.Path) is { } retry)
                    _store.SaveSyncRetry(pairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                // Keep the notice visible until verified success. A retry request is not success.
                return Task.FromResult(true);
            }
            if (choice is NoticeChoices.Exclude or NoticeChoices.Ignore)
            {
                lock (_noticeChoiceGate)
                {
                    pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is no longer available.");
                    var paths = CreateExclusions.Validate(pair.Excluded.Concat([notice.Path]).ToArray());
                    if (saveNarrowerPair is not null) saveNarrowerPair(pair, pair with { Excluded = paths });
                    else _store.SavePair(pair with { Excluded = paths });
                }
                _store.ClearSyncRetry(pairId, notice.Path);
                // Retain pending effects for verify-only reconciliation under the normal exclusion policy.
            }
            _store.CloseNotice(noticeId, choice);
            return Task.FromResult(true);
        }, ct);
}
