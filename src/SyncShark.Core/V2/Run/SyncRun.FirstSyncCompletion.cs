namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal Action? AfterReviewedRemovalObservationsForTest { get; set; }

    private void RetainReviewedRemovalObservations(PairDef pair, BothScan scan,
        IReadOnlySet<string> scopes, CancellationToken ct)
    {
        if (scopes.Count == 0 || _store is not SqliteStateStore store) return;
        bool covered(string path) => scopes.Contains(path) || PairPath.Ancestors(path).Any(scopes.Contains);
        foreach (var (root, side) in new[] { (pair.A.RootId, scan.A), (pair.B.RootId, scan.B) })
        {
            IEnumerable<ItemState> changed = side.AllItemsChanged
                ? _store.Live(root, scopes.ToArray())
                : side.ChangeObjectIds.Select(id => _store.ByObject(root, id)).OfType<ItemState>();
            // A deleted child's own live row is gone. Its surviving reviewed folder remains dirty
            // so the next due validation sees the changed tree, without inventing a child identity.
            var parents = side.Deleted.Keys.SelectMany(PairPath.Ancestors).Where(scopes.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            store.RetainPlanningObservations(root, changed.Where(item => covered(item.Path))
                .Concat(parents.SelectMany(path => _store.Live(root, [path]).Where(item =>
                    string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))), ct);
        }
        // Marks are committed above. Execution checkpoints advance later in AdvanceCursors;
        // interruption here must replay the feed rather than lose the planning obligation.
        AfterReviewedRemovalObservationsForTest?.Invoke();
    }

    // The initial review authorizes reconciliation of the state it saw, not ongoing changes.
    // Keep the compact prepare-time comparison on the item until ordinary planning establishes a baseline.
    internal Plan DeferPostPreviewChanges(PairDef pair, Plan plan,
        IReadOnlyDictionary<string, PrepareMatch> matches)
    {
        // Hash failures can create sleeping retry debt during prepare. Its protection removes the
        // row before apply planning, so an action-only handoff would lose the historical comparison.
        // Attach only the frozen prepare evidence; retain the retry's deadline, attempts and authority.
        foreach (var retry in _store.ReadRetriesMissingPrepareMatch(pair.PairId))
            if (matches.TryGetValue(retry.Path, out var prepared))
                _store.SaveSyncRetry(pair.PairId, retry with { Match = prepared });
        var deferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in plan.Actions)
        {
            if (!matches.TryGetValue(action.Path, out var match)) continue;
            var a = _store.Live(pair.A.RootId, [action.Path]).FirstOrDefault();
            var b = _store.Live(pair.B.RootId, [action.Path]).FirstOrDefault();
            if (a is not null && b is not null && match.Unchanged(Side.A, a) && match.Unchanged(Side.B, b)) continue;
            deferred.Add(action.Path);
            if (_store.SyncRetryAt(pair.PairId, action.Path)?.Kind != "decision")
                _store.SaveSyncRetry(pair.PairId, new(action.Path, "post-preview", 0, "", DateTimeOffset.MinValue, match));
        }
        foreach (var held in plan.UnreviewedHeldDeletions)
            if (_store.SyncRetryAt(pair.PairId, held.Path) is not { ReprobeEvidence: true } &&
                _store.SyncRetryAt(pair.PairId, held.Path)?.Kind != "decision")
                _store.SaveSyncRetry(pair.PairId, new(held.Path, "post-preview", 0, "", DateTimeOffset.MinValue, Held: held));
        if (deferred.Count == 0) return plan;
        var guard = ActionPredicates(pair, LeftAlone(pair).WithPending(deferred));
        return ApplySafetyFilter(pair, guard, plan) with
        {
            // A changed file is not an issue merely because it changed after the preview.
            Notices = plan.Notices.Where(notice => !deferred.Contains(notice.Path)).ToArray()
        };
    }

    internal Plan DeferUnreviewedDeletes(PairDef pair, Plan plan, IReadOnlySet<string> reviewed)
    {
        var unreviewed = FolderDelete.SplitVictims(_store, pair, plan, reviewed).Guarded;
        if (unreviewed.Count == 0) return plan;
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in unreviewed)
        {
            blocked.Add(key);
            foreach (var ancestor in PairPath.Ancestors(key[2..])) blocked.Add(key[..2] + ancestor);
        }
        var grownChoices = plan.Actions.Where(action => FolderDelete.Expands(action) && action.ChoiceRemove &&
            reviewed.Contains(FolderDelete.VictimKey(action.Target, action.Path)) &&
            blocked.Contains(FolderDelete.VictimKey(action.Target, action.Path))).ToArray();
        var notices = new List<Notice>(plan.Notices);
        foreach (var choice in grownChoices)
        {
            var notice = new Notice(pair.PairId, 40, NoticeKind.UserDecision, choice.Path,
                "This reviewed folder removal gained an unreviewed child. The whole folder choice was held; nothing under it was deleted." + NoticeChoices.RemovalAttentionHelp,
                "reviewed-folder-grew", RemovalSides: [choice.Target]);
            notices.Add(notice);
            WriteNotices([notice]);
        }
        var grownGuard = LinkExclusions.None.WithPending(grownChoices.Select(choice => choice.Path));
        bool touchesGrowth(string path) => grownGuard.Covers(path) || grownGuard.Contains(path);
        var actions = new List<SyncAction>(plan.Actions.Count);
        foreach (var action in plan.Actions)
        {
            if (touchesGrowth(action.Path) || action.FromPath is { } from && touchesGrowth(from) ||
                action.Source is { } source && touchesGrowth(source.Path)) continue;
            // A remaining unreviewed delete is never silently dropped: the run's single hold owner below keeps it on
            // its item with durable retry input and exactly one explanation, while the rest of the plan proceeds.
            actions.Add(action);
        }
        plan = plan with { Actions = actions, Notices = notices };
        // Grown choices retain their own notice; every remaining victim uses the shared apply hold.
        return HoldUnreviewedAtApply(pair, plan, reviewed);
    }

    internal Plan HoldGrownRemoveChoices(PairDef pair, Plan plan, IReadOnlyList<OneSidedChoice> choices,
        IReadOnlyList<OneSidedChoice> grown)
    {
        var normalized = OneSidedChoiceResolver.Normalize(choices);
        var changed = grown.ToHashSet();
        bool affected(Side side, string path) => OneSidedChoiceResolver.Resolve(normalized, side, path) is { } owner && changed.Contains(owner);
        var paths = plan.Actions.Where(action => action.ChoiceRemove && affected(action.Target, action.Path))
            .Select(action => action.Path).Concat(plan.HeldDeletions.Where(item => affected(item.Local, item.Path)).Select(item => item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filtered = ApplySafetyFilter(pair, ActionPredicates(pair, LeftAlone(pair).WithPending(paths)), plan);
        return filtered with
        {
            HeldDeletions = filtered.HeldDeletions.Where(item => !paths.Contains(item.Path)).ToArray(),
            Notices = filtered.Notices.Concat(paths.Select(path => new Notice(pair.PairId, 40, NoticeKind.UserDecision, path,
                "This reviewed removal now covers more content than was approved, so it was held without deleting anything." +
                NoticeChoices.RemovalAttentionHelp, "reviewed-folder-grew", RemovalSides: normalized.Where(choice => affected(choice.Present, path))
                    .Select(choice => choice.Present).Distinct().ToArray()))).ToArray()
        };
    }

    private void UpdateRetryWork(PairDef pair, Plan plan, Tally tally, IReadOnlyList<SyncRetry> previous,
        IReadOnlyList<string> pending, Func<string, bool> evaluated, bool scanComplete)
    {
        var byPath = previous.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var leftAlone = LeftAlone(pair);
        var includes = BuildPredicates(pair, PairInclude.For);
        var ignoreRules = PairInclude.IgnoreRules(pair);
        var decisions = _store.UnresolvedNotices(pair.PairId).Where(PlanNotices.HasPathScope).Where(notice => notice.Kind == NoticeKind.UserDecision)
            .Select(notice => notice.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uncertain = OutstandingFootprints(pair.PairId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reasons = FailureReasons(plan, tally);
        // An unresolved tagged conflict successor keeps its frozen explanation even when this pass fails it in
        // the apply phase rather than in reconciliation.
        var conflictSuccessors = plan.Actions
            .Where(action => action.ConflictTransitionKey is not null && !action.SuppressReconciledBaseline)
            .GroupBy(action => action.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
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
        foreach (var item in previous)
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
            var excluded = !rebuildable && pair.Excluded.Any(path => PairPath.IsSelfOrUnder(item.Path, path));
            var a = _store.Live(pair.A.RootId, [item.Path]).FirstOrDefault();
            var b = _store.Live(pair.B.RootId, [item.Path]).FirstOrDefault();
            var ignored = SyncIgnorePolicy.IsIgnored(item.Path, (a ?? b)?.Kind == ItemKind.Folder, ignoreRules);
            // A hold is not an exclusion or proof of success. In particular, inventory narrowed by
            // a root/quarantine/grant hold may look absent or equal without evaluating this retry.
            // Only the user's actual exclusion/ignore can retire debt in a protected scope.
            if (!excluded && !ignored && ((rebuildable ? leftAlone.CoversExceptLocalLinks(item.Path) : leftAlone.Covers(item.Path)) ||
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
            var equal = evaluated(item.Path) && a is not null && b is not null && Content.SameContent(a, b) == true;
            if (item.Held is not null)
                RunLog.Trace($"Held retry completion evidence: path={item.Path}; kind={item.Kind}; reprobe={item.ReprobeEvidence}; due={item.RetryAfter:O}; current={_store.SyncRetryAt(pair.PairId, item.Path)?.Kind}; remaining={remaining.Contains(item.Path)}; pending={pending.Contains(item.Path, StringComparer.OrdinalIgnoreCase)}; settled={settled.Contains(item.Path)}; equal={equal}; evaluated={evaluated(item.Path)}; absent={absent}; decision={decisions.Contains(item.Path)}; excluded={excluded}; ignored={ignored}");
            if (excluded || ignored || decisions.Contains(item.Path) || refused || absent || !remaining.Contains(item.Path) && (settled.Contains(item.Path) || equal ||
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
        foreach (var path in remaining)
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
            SaveFailedRetry(pair, path, reasons.GetValueOrDefault(path), old,
                conflictSuccessors.GetValueOrDefault(path));
        }
        _store.RefreshInterruptedDeleteNotice(pair.PairId);
        _store.RefreshBlockedDeleteNotice(pair.PairId);
    }

    private void SaveFailedRetry(PairDef pair, string path, string? reason, SyncRetry? old,
        SyncAction? conflictSuccessor = null)
    {
        if (old?.Kind == "decision") return;
        // The evidence probe owns restored-row counting; the shared owner preserves that counter.
        var retry = FailureRetryOwner.SaveFailedRetry(_store, pair.PairId, path, reason, old, _time.GetUtcNow());
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

    private static Dictionary<string, string> FailureReasons(Plan plan, Tally tally)
    {
        var failed = tally.Failures.GroupBy(result => result.ActionId).ToDictionary(group => group.Key, group => group.Last().Reason);
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in plan.Actions)
            if (tally.WasQuotaBlocked(action.ActionId)) reasons[action.Path] = DropboxQuota.Detail;
            else if (failed.GetValueOrDefault(action.ActionId) is { } reason) reasons[action.Path] = reason;
        return reasons;
    }

    internal static (int Count, IReadOnlyList<string> Examples) SummarizeContentUnconfirmed(
        IEnumerable<string> currentPaths, IEnumerable<SyncRetry> retries)
    {
        // Only the current plan is materialized. Persisted debt is unique by path and streams in
        // bounded pages; the sample never holds more than fifty paths, even for a million retries.
        var current = currentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sample = new SortedSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var retry in retries)
        {
            if (FailureRetryOwner.KindOf(retry) != FailureRetryOwner.FailureKind.ContentProof) continue;
            current.Remove(retry.Path);
            Add(retry.Path);
        }
        foreach (var path in current) Add(path);
        return (count, sample.ToArray());

        void Add(string path)
        {
            if (count < int.MaxValue) count++;
            sample.Add(path);
            if (sample.Count > 50) sample.Remove(sample.Max!);
        }
    }

    private void ParkFirstSyncWork(PairDef pair, Plan plan, Tally tally, IEnumerable<string> pending,
        IEnumerable<string> unconfirmed, IEnumerable<string> blocked) =>
        ParkFirstSyncWorkWithEvidence(pair, plan, tally, pending, unconfirmed, blocked, false);

    private void ParkFirstSyncWorkWithEvidence(PairDef pair, Plan plan, Tally tally, IEnumerable<string> pending,
        IEnumerable<string> unconfirmed, IEnumerable<string> blocked, bool evidenceUnavailable)
    {
        var held = plan.HeldDeletions.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var reviewed = plan.Actions.Concat(_store.UnresolvedIntents(pair.PairId))
            .Where(action => action.Kind != ActionKind.Hold && !tally.WasSettled(action.ActionId))
            .GroupBy(action => action.ActionId).Select(group => group.Last())
            .ToLookup(action => action.Path, StringComparer.OrdinalIgnoreCase);
        var reasons = FailureReasons(plan, tally);
        foreach (var path in pending.Concat(unconfirmed).Concat(blocked)
            .Concat(plan.Actions.Where(action => action.Kind != ActionKind.Hold && !tally.WasSettled(action.ActionId))
                .Select(action => action.Path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_store.SyncRetryAt(pair.PairId, path) is { ReprobeEvidence: true }) continue;
            // Recovery holds already retained exact reviewed authority before filtering effects.
            if (plan.RecoveryPending.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            // A decision supersedes prepare authority. Preserve its proposal and dormant scheduling.
            if (_store.SyncRetryAt(pair.PairId, path)?.Kind is "decision" or "reviewed-removal" or "reviewed-choice") continue;
            if (_store.Live(pair.A.RootId, [path]).Concat(_store.Live(pair.B.RootId, [path]))
                .Any(item => item.Flags.HasFlag(ItemFlags.ProviderNameRefused)))
            { _store.ClearSyncRetry(pair.PairId, path); continue; }
            // A durable effect remains owned by pending_effect. This row owns the path's retry input,
            // including pre-effect failures and unresolved deletion evidence that have no intent yet.
            if (reviewed[path].Any() && reviewed[path].All(action => tally.WasSkipped(action.ActionId)) &&
                !reviewed[path].Any(action => action.RebuildableRemove))
            {
                var saved = _store.SyncRetryAt(pair.PairId, path);
                _store.SaveSyncRetry(pair.PairId, new(path, "post-preview", 0, "", DateTimeOffset.MinValue, Match: saved?.Match));
                continue; // A changed precondition is ordinary work, not a new issue by itself.
            }
            var prior = _store.SyncRetryAt(pair.PairId, path);
            var reason = reasons.GetValueOrDefault(path) ?? prior?.LastError ??
                "The reviewed effect could not be completed in this run.";
            var conflictSuccessor = reviewed[path].FirstOrDefault(action =>
                action.ConflictTransitionKey is not null && !action.SuppressReconciledBaseline);
            // Parking adds reviewed authority, not another failure occurrence or a renewed deadline.
            var failure = FailureRetryOwner.ReportedFailure(prior, _time.GetUtcNow(), retainSleeping: true,
                countAttempt: !(evidenceUnavailable && held.ContainsKey(path)));
            _store.SaveSyncRetry(pair.PairId, new(path, "first-sync", failure.Attempts, reason, failure.RetryAfter,
                Held: held.GetValueOrDefault(path), ReviewedActions: reviewed[path].ToArray(),
                HeldReviewed: held.ContainsKey(path), HeldExpected: held.TryGetValue(path, out var heldItem)
                    ? _store.Live(heldItem.Local == Side.A ? pair.A.RootId : pair.B.RootId, [path]).FirstOrDefault() : null,
                PendingNotice: prior?.PendingNotice, Match: prior?.Match,
                ReprobeEvidence: evidenceUnavailable && held.ContainsKey(path))
                { FailureKind = FailureRetryOwner.ClassifyCause(reason) });
            // A tagged conflict successor gets only its dedicated Retry-only explanation, even for a pair-wide
            // quota condition: the generic notice's Ignore cannot resolve the frozen original authority.
            if (conflictSuccessor is not null)
                WriteNotices([ConflictCopyBlockedNotice(pair.PairId, conflictSuccessor, reason)]);
            else if (reason != DropboxQuota.Detail && !(evidenceUnavailable && held.ContainsKey(path)))
                WriteNotices([FailureRetryOwner.RetryNotice(pair.PairId, path, reason, failure.Attempts)]);
        }
    }
}
