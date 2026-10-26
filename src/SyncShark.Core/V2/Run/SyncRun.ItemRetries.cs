namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private void UpdateRetryWork(PairDef pair, Plan plan, Tally tally, IReadOnlyList<SyncRetry> previous,
        IReadOnlyList<string> pending, Func<string, bool> evaluated, bool scanComplete)
    {
        var byPath = previous.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var leftAlone = LeftAlone(pair);
        var includes = BuildPredicates(pair, PairInclude.For);
        var decisions = _store.UnresolvedNotices(pair.PairId).Where(PlanNotices.HasPathScope).Where(notice => notice.Kind == NoticeKind.UserDecision)
            .Select(notice => notice.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uncertain = OutstandingFootprints(pair.PairId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reasons = FailureReasons(plan, tally);
        var conflictSuccessors = RetryConflictSuccessors(plan);
        var notices = _store.UnresolvedNotices(pair.PairId).Where(PlanNotices.HasPathScope).Where(notice => notice.ReasonKey is "sync-retry" or "deletion-history-unavailable")
            .ToLookup(notice => notice.Path, StringComparer.OrdinalIgnoreCase);
        var sourceBlocked = plan.Actions.Where(action => tally.WasDeleteSourceBlocked(action.ActionId))
            .Select(action => action.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = pending.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failed = plan.Actions.Where(action => action.Kind != ActionKind.Hold && !tally.WasSettled(action.ActionId))
            .Select(action => action.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        remaining.UnionWith(failed);
        remaining.UnionWith(plan.Notices.Where(PlanNotices.HasPathScope).Where(notice => notice.ReasonKey == PlanNotices.ContentUnconfirmed.Key || notice.ReasonKey == "sync-retry").Select(notice => notice.Path));
        var settled = plan.Actions.Where(action => tally.WasSettled(action.ActionId))
            .SelectMany(action => action.Kind == ActionKind.Move && action.FromPath is not null
                ? new[] { action.Path, action.FromPath } : new[] { action.Path })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in previous.Where(item => !tally.DuplicatePaths.Contains(item.Path)))
        {
            // Run-level debt is settled only after completion, never by item equality/absence.
            if (item.Path.Length == 0 && item.Kind == "run") continue;
            // Read-stage debt is owned by RecordScanRetries, never by action/equality settlement.
            if (plan.RecoveryPending.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            { remaining.Remove(item.Path); continue; }
            if (item.Kind == "scan" || item.ReadRoots is { Count: > 0 } ||
                _store.SyncRetryAt(pair.PairId, item.Path)?.PendingNotice is not null)
            { remaining.Remove(item.Path); continue; }
            if (item.Kind == "deletion-evidence" && _store.SyncRetryAt(pair.PairId, item.Path)?.Kind == "post-preview")
            { remaining.Remove(item.Path); continue; } // Recreated remote: immediate ordinary re-evaluation, not another failed evidence attempt.
            if (_store.SyncRetryAt(pair.PairId, item.Path)?.Kind == "decision")
            { remaining.Remove(item.Path); continue; }
            var rebuildable = item.ReviewedActions?.Any(action => action.RebuildableRemove) == true;
            var excluded = !rebuildable && PairInclude.Excludes(pair, item.Path);
            var a = _store.Live(pair.A.RootId, [item.Path]).FirstOrDefault();
            var b = _store.Live(pair.B.RootId, [item.Path]).FirstOrDefault();
            var notSynced = PairInclude.NotSynced(pair, item.Path, (a ?? b)?.Kind == ItemKind.Folder, rebuildable);
            // A hold is not an exclusion or proof of success. In particular, inventory narrowed by
            // a root/quarantine/grant hold may look absent or equal without evaluating this retry.
            // Only the user's actual exclusion/ignore can retire debt in a protected scope.
            if (!notSynced && ((rebuildable ? leftAlone.CoversExceptLocalLinks(item.Path) : leftAlone.Covers(item.Path)) ||
                !rebuildable && (!includes.A(item.Path) || !includes.B(item.Path))))
            {
                // Rotate held debt out of the bounded due page without recording a failed attempt.
                var current = _store.SyncRetryAt(pair.PairId, item.Path);
                if (current is not null)
                    _store.SaveSyncRetry(pair.PairId, current with { RetryAfter = _time.GetUtcNow().AddMinutes(5) });
                remaining.Remove(item.Path);
                continue;
            }
            var refused = (a?.Flags.HasFlag(ItemFlags.ProviderNameRefused) ?? false) ||
                (b?.Flags.HasFlag(ItemFlags.ProviderNameRefused) ?? false);
            var absent = scanComplete && a is null && b is null && !uncertain.Contains(item.Path);
            var equal = evaluated(item.Path) && !uncertain.Contains(item.Path) && !remaining.Contains(item.Path) &&
                (UnchangedRetryBaseline(pair, item.Path,
                    candidate => !leftAlone.Covers(candidate) && includes.A(candidate) && includes.B(candidate) &&
                        !uncertain.Contains(candidate) && !remaining.Contains(candidate) && !decisions.Contains(candidate))
                    ?? (a is not null && b is not null && Content.SameContent(a, b) == true));
            if (item.Held is not null)
                RunLog.Trace($"Held retry completion evidence: path={item.Path}; kind={item.Kind}; reprobe={item.ReprobeEvidence}; due={item.RetryAfter:O}; current={_store.SyncRetryAt(pair.PairId, item.Path)?.Kind}; remaining={remaining.Contains(item.Path)}; pending={pending.Contains(item.Path, StringComparer.OrdinalIgnoreCase)}; settled={settled.Contains(item.Path)}; equal={equal}; evaluated={evaluated(item.Path)}; absent={absent}; decision={decisions.Contains(item.Path)}; excluded={excluded}; notSynced={notSynced}");
            if (notSynced || decisions.Contains(item.Path) || refused || absent || !remaining.Contains(item.Path) && (settled.Contains(item.Path) || equal ||
                item.Held is not null && !pending.Contains(item.Path, StringComparer.OrdinalIgnoreCase)))
            {
                remaining.Remove(item.Path);
                if (excluded && item.Held is not null) _store.SuspendHeldRetry(pair.PairId, item);
                else _store.ClearSyncRetry(pair.PairId, item.Path);
                // RQ-052: an excluded held item is suspended, not settled. Keep its stored evidence while it
                // is excluded; re-inclusion starts a fresh probe (SavePairIncluded invalidates cached evidence
                // first), so an exclusion never destroys the deletion history.
                if (!excluded && item.Held is { } held)
                    _store.RemoveDeletionEvidence(pair.PairId, held.Local == Side.A ? pair.B.RootId : pair.A.RootId, [item.Path]);
                foreach (var notice in notices[item.Path])
                    if (notice.NoticeId is not null) _store.CloseNotice(notice.NoticeId, excluded ? "exclude" : "resolved");
                // A fresh equal/no-op replan can settle the retry without any Done action.
                var sourceNotice = DeleteSourceGuard.NoticeId(pair.PairId, item.Path);
                if (!sourceBlocked.Contains(item.Path) && _store.NoticeById(pair.PairId, sourceNotice) is { Closed: false })
                    _store.CloseNotice(sourceNotice, excluded ? "exclude" : "resolved");
            }
            else remaining.Add(item.Path); // Back off unresolved no-action rows too; they cannot monopolize a bounded batch.
        }
        foreach (var path in remaining.Where(tally.CompleteRetry))
        {
            var old = _store.SyncRetryAt(pair.PairId, path) ?? byPath.GetValueOrDefault(path);
            if (sourceBlocked.Contains(path))
            {
                // The executor already committed the retry with its one explanatory notice. Keep it due
                // next run; do not replace it with a second generic failure notice/backoff.
                // Preserve provider Retry-After from the durable guard result.
                if (old is not null) _store.SaveSyncRetry(pair.PairId, old);
                continue;
            }
            SaveFailedRetry(pair, path, reasons.GetValueOrDefault(path)?.Reason, old,
                conflictSuccessors.GetValueOrDefault(path), reasons.GetValueOrDefault(path), !tally.DuplicatePaths.Contains(path));
        }
        _store.RefreshInterruptedDeleteNotice(pair.PairId);
        _store.RefreshBlockedDeleteNotice(pair.PairId);
    }

    // An unresolved tagged successor retains its explanation whether apply or reconciliation failed.
    private static Dictionary<string, SyncAction> RetryConflictSuccessors(Plan plan) => plan.Actions
        .Where(action => action.ConflictTransitionKey is not null && !action.SuppressReconciledBaseline)
        .GroupBy(action => action.Path, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    // Every correspondence must prove live equality or unchanged identities against its successful
    // baseline. Null means no correspondence exists; the caller retains ordinary path equality.
    private bool? UnchangedRetryBaseline(PairDef pair, string path, Func<string, bool> unprotected)
    {
        var found = false;
        foreach (var baseline in _store.BaselinesAt(pair.PairId, Side.A, [path])
            .Concat(_store.BaselinesAt(pair.PairId, Side.B, [path])).DistinctBy(item => item.ObjectKey))
        {
            found = true;
            if (!unprotected(baseline.A.Path) || !unprotected(baseline.B.Path)) return false;
            var a = _store.Live(pair.A.RootId, [baseline.A.Path]).FirstOrDefault();
            var b = _store.Live(pair.B.RootId, [baseline.B.Path]).FirstOrDefault();
            if (a is null || b is null || a.Flags != ItemFlags.None || b.Flags != ItemFlags.None) return false;
            if (Content.SameContent(a, b) == true) continue;
            if (Content.SameContent(baseline.A, baseline.B) != true ||
                a.ObjectId != baseline.A.ObjectId || b.ObjectId != baseline.B.ObjectId ||
                !Planner.Unchanged(a, baseline.A) || !Planner.Unchanged(b, baseline.B)) return false;
        }
        return found ? true : null;
    }

    internal void SaveFailedRetry(PairDef pair, string path, string? reason, SyncRetry? old,
        SyncAction? conflictSuccessor = null, ActionResult? failure = null, bool countAttempt = true)
    {
        if (old?.Kind == "decision") return;
        // The evidence probe owns restored-row counting; the shared owner preserves that counter.
        var retry = FailureRetryOwner.SaveFailedRetry(_store, pair.PairId, path, reason, old, _time.GetUtcNow(),
            failure?.FailureKind, failure?.RetryNotBefore, countAttempt);
        var attempts = retry.Attempts;
        // A frozen conflict successor owns its dedicated Retry-only explanation from its first failure, however
        // that failure arrives: the generic sync-retry notice (with Ignore) must never stand in for it. The
        // existing notice fold keeps an already-open, more specific explanation (for example an occupied copy
        // name) and closes any generic notice at this path.
        if (conflictSuccessor is { ConflictTransitionKey: not null, SuppressReconciledBaseline: false } tagged)
            WriteNotices([ConflictCopyBlockedNotice(pair.PairId, tagged, retry.LastError)]);
        else if (retry.DeleteBlockedDetail is null && !retry.ReprobeEvidence && retry.LastError != DropboxQuota.Detail && attempts >= 1 && retry.Kind is not ("planning" or "recovery"))
            WriteNotices([FailureRetryOwner.RetryNotice(pair.PairId, path, retry.LastError, attempts,
                folderInspection: _store.Live(pair.A.RootId, [path]).Concat(_store.Live(pair.B.RootId, [path]))
                    .Any(item => item.Kind == ItemKind.Folder))]);
    }

    /// <summary>The one Retry-only notice a tagged conflict successor carries until it is verified or retired.</summary>
    private static Notice ConflictCopyBlockedNotice(string pairId, SyncAction successor, string? reason)
    {
        var cause = string.IsNullOrWhiteSpace(reason) ? "the copy could not be completed" : reason;
        return new Notice(pairId, successor.Rule, NoticeKind.Info, successor.Path,
            $"The keep-both copy could not be placed yet: {cause} " +
            "Both original versions remain protected; Retry checks this copy again and never overwrites.",
            ExecutionReasons.ConflictCopyBlocked, ExecutionReasons.ConflictCopyNoticeId(pairId, successor));
    }

}
