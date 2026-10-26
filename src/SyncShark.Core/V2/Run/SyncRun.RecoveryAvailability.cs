namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private Plan HoldUnavailableRecovery(PairDef pair, Plan plan, IReadOnlySet<string>? reviewedDeletes)
    {
        var unavailable = RecoverySafety.Unavailable(_endpoints.Writer(pair.A), _endpoints.Writer(pair.B));
        if (unavailable is null)
        {
            foreach (var notice in _store.UnresolvedNotices(pair.PairId))
                if (notice.ReasonKey == "recovery-unavailable" && notice.NoticeId is not null)
                    _store.CloseNotice(notice.NoticeId, "resolved");
            return plan;
        }
        var blocked = plan.Actions.Where(RecoverySafety.RequiresRecovery).ToArray();
        var scopes = blocked.Select(action => action.Path).Concat(plan.HeldDeletions.Select(item => item.Path));
        var guard = LinkExclusions.None.WithPending(scopes);
        bool Touches(SyncAction action) => RowPaths(action).Any(path => guard.Covers(path) || guard.Contains(path));
        var held = plan.Actions.Where(action => RecoverySafety.RequiresRecovery(action) || Touches(action)).ToArray();
        SyncRetry DeferredRecovery(string path, SyncRetry? prior)
        {
            var retry = prior ?? new SyncRetry(path, "recovery", 0, unavailable, DateTimeOffset.MinValue);
            if (retry.RetryAfter > _time.GetUtcNow()) return retry;
            var failure = FailureRetryOwner.Failed(retry.Attempts, _time.GetUtcNow());
            return retry with { Attempts = failure.Attempts, RetryAfter = failure.RetryAfter };
        }
        foreach (var group in held.GroupBy(action => action.Path, StringComparer.OrdinalIgnoreCase))
        {
            var old = _store.SyncRetryAt(pair.PairId, group.Key);
            if (old?.Kind == "decision") continue;
            _store.SaveSyncRetry(pair.PairId, DeferredRecovery(group.Key, old) with
            { LastError = unavailable, DeleteBlockedDetail = null, DeleteBlockedDeferred = false,
                ReviewedActions = old?.ReviewedActions ?? (plan.FirstSyncOptions?.FirstSync == true ? group.ToArray() : null),
                ProposedActions = old?.ProposedActions ?? group.ToArray() });
        }
        foreach (var item in plan.HeldDeletions)
        {
            var old = _store.SyncRetryAt(pair.PairId, item.Path);
            if (old?.Kind == "decision") continue;
            if (plan.FirstSyncOptions?.FirstSync == true &&
                reviewedDeletes?.Contains(FolderDelete.VictimKey(item.Local, item.Path)) != true)
            {
                // An unavailable Recovery location never upgrades an unreviewed held path.
                _store.SaveSyncRetry(pair.PairId, DeferredRecovery(item.Path, old) with
                { LastError = unavailable, DeleteBlockedDetail = null, DeleteBlockedDeferred = false });
                continue;
            }
            _store.SaveSyncRetry(pair.PairId, DeferredRecovery(item.Path, old) with
            { LastError = unavailable, DeleteBlockedDetail = null, DeleteBlockedDeferred = false, Held = old?.Held ?? item,
                HeldReviewed = old?.HeldReviewed == true || reviewedDeletes?.Contains(FolderDelete.VictimKey(item.Local, item.Path)) == true,
                HeldExpected = old?.HeldExpected ?? _store.Live(item.Local == Side.A ? pair.A.RootId : pair.B.RootId, [item.Path]).FirstOrDefault() });
        }
        WriteNotices([new Notice(pair.PairId, 40, NoticeKind.Info, "",
            "Recovery location unavailable (" + unavailable + "); deletions and replacements paused. Retry when the location returns, or open settings to choose another location.",
            "recovery-unavailable")]);
        return plan with { Actions = plan.Actions.Except(held).ToArray(), HeldDeletions = [],
            RecoveryPending = held.Select(action => action.Path).Concat(plan.HeldDeletions.Select(item => item.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }
}
