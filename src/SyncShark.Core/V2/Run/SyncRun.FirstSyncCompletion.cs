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
        // Pending includes failed resolved effects too. An executable action proves that evidence
        // already reached planning; its failure retains the ordinary effect retry explanation.
        var evidencePending = pending.Where(path => held.ContainsKey(path) && !reviewed[path].Any())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in pending.Concat(unconfirmed).Concat(blocked)
            .Concat(plan.Actions.Where(action => action.Kind != ActionKind.Hold && !tally.WasSettled(action.ActionId))
                .Select(action => action.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Where(tally.CompleteRetry))
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
            var reason = reasons.GetValueOrDefault(path)?.Reason ?? prior?.LastError ??
                "The reviewed effect could not be completed in this run.";
            var conflictSuccessor = reviewed[path].FirstOrDefault(action =>
                action.ConflictTransitionKey is not null && !action.SuppressReconciledBaseline);
            // Parking adds reviewed authority, not another failure occurrence or a renewed deadline.
            var failure = ParkedFailureState(prior, reasons.GetValueOrDefault(path), evidenceUnavailable && held.ContainsKey(path) || tally.DuplicatePaths.Contains(path));
            _store.SaveSyncRetry(pair.PairId, new(path, "first-sync", failure.Attempts, reason, failure.RetryAfter,
                Held: held.GetValueOrDefault(path), ReviewedActions: reviewed[path].ToArray(),
                HeldReviewed: held.ContainsKey(path), HeldExpected: held.TryGetValue(path, out var heldItem)
                    ? _store.Live(heldItem.Local == Side.A ? pair.A.RootId : pair.B.RootId, [path]).FirstOrDefault() : null,
                PendingNotice: prior?.PendingNotice, Match: prior?.Match,
                ReprobeEvidence: evidenceUnavailable && held.ContainsKey(path))
                { FailureKind = reasons.GetValueOrDefault(path)?.FailureKind ?? prior?.FailureKind ?? FailureRetryOwner.ClassifyCause(reason) });
            WriteParkedWorkNotice(pair.PairId, path, reason, failure.Attempts, conflictSuccessor,
                evidencePending.Contains(path), evidenceUnavailable);
        }
    }
}
