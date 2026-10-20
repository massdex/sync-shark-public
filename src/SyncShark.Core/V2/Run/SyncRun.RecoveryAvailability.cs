namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private Plan HoldUnavailableRecovery(PairDef pair, Plan plan, IReadOnlySet<string>? reviewedDeletes)
    {
        var unavailable = RecoverySafety.Unavailable(_endpoints.Writer(pair.A), _endpoints.Writer(pair.B));
        if (unavailable is null)
        {
            foreach (var notice in _store.OpenNotices(pair.PairId))
                if (notice.ReasonKey == "recovery-unavailable" && notice.NoticeId is not null)
                    _store.CloseNotice(notice.NoticeId, "resolved");
            return plan;
        }
        var blocked = plan.Actions.Where(RecoverySafety.RequiresRecovery).ToArray();
        var scopes = blocked.Select(action => action.Path).Concat(plan.HeldDeletions.Select(item => item.Path));
        var guard = LinkExclusions.None.WithPending(scopes);
        bool Touches(SyncAction action) => RowPaths(action).Any(path => guard.Covers(path) || guard.Contains(path));
        var held = plan.Actions.Where(action => RecoverySafety.RequiresRecovery(action) || Touches(action)).ToArray();
        foreach (var group in held.GroupBy(action => action.Path, StringComparer.OrdinalIgnoreCase))
        {
            var old = _store.SyncRetryAt(pair.PairId, group.Key);
            if (old?.Kind == "decision") continue;
            _store.SaveSyncRetry(pair.PairId, (old ?? new SyncRetry(group.Key, "recovery", 0, "", DateTimeOffset.MinValue)) with
            { LastError = unavailable, ReviewedActions = group.ToArray(), RetryAfter = DateTimeOffset.MinValue });
        }
        foreach (var item in plan.HeldDeletions)
        {
            var old = _store.SyncRetryAt(pair.PairId, item.Path);
            if (old?.Kind == "decision") continue;
            _store.SaveSyncRetry(pair.PairId, (old ?? new SyncRetry(item.Path, "recovery", 0, "", DateTimeOffset.MinValue)) with
            { LastError = unavailable, Held = item, HeldReviewed = reviewedDeletes?.Contains(FolderDelete.VictimKey(item.Local, item.Path)) == true });
        }
        WriteNotices([new Notice(pair.PairId, 40, NoticeKind.Info, "",
            "Recovery location unavailable (" + unavailable + "); deletions and replacements paused. Retry when the location returns, or open settings to choose another location.",
            "recovery-unavailable")]);
        return plan with { Actions = plan.Actions.Except(held).ToArray(), HeldDeletions = [] };
    }
}
