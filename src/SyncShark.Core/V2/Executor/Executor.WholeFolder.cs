using System.Collections.Concurrent;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

// a Dropbox folder Delete over at least FolderDelete.WholeFolderMinimum live descendants is sent as
// one delete_batch entry instead of one conditional delete per file. Its victims and child actions are exactly the
// per-item expansion's; only the effect differs. Each victim is Done only when the endpoint's post-check proved it
// deleted as planned; any post-check failure leaves the whole folder Uncertain for reconcile, never Failed or replayed.
public sealed partial class Executor
{
    private const int NoticePathLimit = 20;
    private const string WholeFolderGroupKind = "whole-folder-group";
    private const string WholeFolderMemberKind = "whole-folder-member";

    // One snapshot, on the last-to-resolve anchor. Members carry only its id, not N copies of the victim set.
    private sealed record WholeFolderSnapshot(SyncAction[] Actions);
    private sealed record WholeFolderRecovery(string PairId, string ScanCursor, WholeFolderSnapshot[] Trees,
        string ProviderState);

    internal static void RequestFolderRestoreAgain(IStateStore store, string pairId, Notice notice, IFolderTreeDeleter endpoint)
    {
        var anchor = notice.FolderRecoveryAnchor is { } id ? store.IntentById(pairId, id) : null;
        if (anchor?.ProviderEffect?.Kind != WholeFolderGroupKind)
            throw new SyncRunRefusedException("Recovery evidence is no longer pending. Verify or inspect Dropbox history; nothing was changed.");
        var recovery = JsonSerializer.Deserialize<WholeFolderRecovery>(anchor.ProviderEffect.Id)
            ?? throw new InvalidDataException("Missing folder recovery checkpoint.");
        if (recovery.PairId != pairId || !recovery.Trees.Any(tree => PairPath.Same(tree.Actions[^1].Path, notice.Path)))
            throw new SyncRunRefusedException("The notice no longer matches its recovery episode.");
        recovery = recovery with { ProviderState = endpoint.AuthorizeRestoreAgain(recovery.ProviderState, notice.Path) };
        store.RecordIntent(pairId, anchor with { ProviderEffect = anchor.ProviderEffect with { Id = JsonSerializer.Serialize(recovery) } });
    }

    private static FolderTree RecoveryTree(WholeFolderSnapshot snapshot) => new(
        snapshot.Actions[^1].ExpectedTarget!, snapshot.Actions[..^1].Select(action => action.ExpectedTarget!).ToArray());

    /// <summary>One folder Delete taken off the per-item path: its child actions (folder last) and its frozen victims.</summary>
    private sealed class WholeFolder(List<SyncAction> actions, IReadOnlyList<ItemState> victims)
    {
        public IReadOnlyList<SyncAction> Actions => actions;
        public SyncAction Folder => actions[^1];
        public IReadOnlyList<ItemState> Victims => victims;
        public bool Handled { get; set; }
    }

    // A folder qualifies when it is large enough, its side can delete whole folders and has the plan's scan cursor,
    // its victims were frozen at plan time, and no other action on that side works under it or moves out of it.
    private static Func<SyncAction, int, bool> WholeFolderTarget(Plan plan, IEndpointWriter a, IEndpointWriter b) =>
        (folder, count) =>
            folder.AfterFolderMove is null && count >= FolderDelete.WholeFolderMinimum &&
            (folder.Target == Side.A ? a : b) is IFolderTreeDeleter &&
            !string.IsNullOrEmpty(folder.Target == Side.A ? plan.ScanCursorA : plan.ScanCursorB) &&
            folder.FrozenVictimSignature is not null &&
            !plan.Actions.Any(other => other.Kind != ActionKind.Hold &&
                !string.Equals(other.ActionId, folder.ActionId, StringComparison.Ordinal) &&
                ((other.Target == folder.Target &&
                  (PairPath.IsSelfOrUnder(other.Path, folder.Path) ||
                   (other.FromPath is not null && PairPath.IsSelfOrUnder(other.FromPath, folder.Path)))) ||
                 (other.Target != folder.Target && other.Source is not null &&
                  PairPath.IsSelfOrUnder(other.Source.Path, folder.Path))));

    // Runs every whole-folder delete before the phased apply. A folder its endpoint did not submit goes back to the
    // per-item path (returned with the other actions); every other folder's results are committed and recorded here.
    private async Task<IReadOnlyList<SyncAction>> DeleteWholeFoldersAsync(Plan plan, List<WholeFolder> trees,
        IEndpointWriter a, IEndpointWriter b, StoreCommitter committer, ConcurrentDictionary<string, Outcome> outcomes,
        List<ActionResult> results, IReadOnlyList<SyncAction> actions, CancellationToken ct)
    {
        var remaining = new List<SyncAction>(actions);
        var replacements = DeleteSourceGuard.Replacements(plan.Actions);
        foreach (var side in new[] { Side.A, Side.B })
        {
            PairPause.Check();
            var group = trees.Where(tree => tree.Folder.Target == side).ToArray();
            if (group.Length == 0) continue;
            var source = side == Side.A ? b : a;
            var allowed = new List<WholeFolder>();
            foreach (var whole in group)
            {
                var blocked = 0;
                // Bounded metadata pages retain delete_batch for large safe trees. Never infer identity
                // absence from a parent listing: the endpoint batch proves every historical ID globally.
                foreach (var page in whole.Actions.Chunk(128))
                {
                    var requests = new List<(ItemState Historical, string Path)>();
                    var indices = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var action in page.Where(DeleteSourceGuard.RequiresAbsence))
                    {
                        ItemState? historical;
                        try { historical = committer.DeleteSource(action); }
                        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, isStateDamage: _isStateDamage) == FailureRetryOwner.FailureScope.Item) { continue; }
                        if (historical is null || string.IsNullOrWhiteSpace(historical.ObjectId)) continue;
                        indices[action.ActionId] = requests.Count;
                        requests.Add((historical, action.Path));
                    }
                    IReadOnlyList<AbsenceObservation> observations = new AbsenceObservation[requests.Count];
                    if (requests.Count > 0 && source is ISourceAbsenceProbe probe)
                    {
                        try { observations = await SourceAbsenceBatch.ObserveAsync(probe, requests, ct, _isStateDamage).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, isStateDamage: _isStateDamage) == FailureRetryOwner.FailureScope.Item) { /* Retry debt, never absence. */ }
                    }
                    foreach (var action in page)
                    {
                        var indexed = indices.TryGetValue(action.ActionId, out var index);
                        var result = await DeleteSourceGuard.CheckAsync(action, source, committer, plan.PairId, ct, isStateDamage: _isStateDamage,
                            DeleteSourceGuard.Replacement(action, replacements), preprobed: true,
                            absence: indexed && observations.Count == requests.Count ? observations[index].Absent : null,
                            absenceReason: indexed && observations.Count == requests.Count ? observations[index].Reason : null)
                            .ConfigureAwait(false);
                        if (result is not null) blocked = 1;
                    }
                }
                // A whole-folder call must not remove even one child whose source still exists or is unknown.
                // Fall back to ordinary guarded actions; their failed dependencies keep the parent untouched.
                if (blocked != 0) remaining.AddRange(whole.Actions);
                else allowed.Add(whole);
            }
            group = allowed.ToArray();
            if (group.Length == 0) continue;
            var target = side == Side.A ? a : b;
            var byTree = new Dictionary<FolderTree, WholeFolder>(ReferenceEqualityComparer.Instance);
            foreach (var whole in group)
            {
                var evidence = whole.Actions.Select(action => (Action: action,
                    Historical: DeleteSourceGuard.RequiresAbsence(action) ? committer.DeleteSource(action) : null,
                    RestorePending: committer.DeleteRestorePending(action))).ToArray();
                byTree[new FolderTree(whole.Folder.ExpectedTarget!, whole.Victims)
                {
                    RevalidateDelete = async () =>
                    {
                        var batching = new AdmissionAbsenceBatch(_isStateDamage);
                        foreach (var page in evidence.Chunk(128))
                        {
                            var checks = await Task.WhenAll(page.Select(item =>
                                DeleteSourceGuard.CheckEvidenceAsync(item.Action, source, item.Historical,
                                    item.RestorePending, ct, isStateDamage: _isStateDamage, DeleteSourceGuard.Replacement(item.Action, replacements),
                                    admissionBatch: batching))).ConfigureAwait(false);
                            if (checks.Any(result => result is not null)) return false;
                        }
                        return true;
                    }
                }] = whole;
            }
            // Freeze ordinary intents and capture source evidence before provider admission. After the
            // pre-feed read, source-only probes precede the one group checkpoint (the only store wait).
            // No destructive effect exists before that checkpoint.
            await Task.WhenAll(group.SelectMany(tree => tree.Actions)
                .Select(action => committer.RecordIntentAsync(action, ct))).ConfigureAwait(false);
            await StoreAsync(() => _store.AddNotices(group.Select(whole =>
                FolderHistoryNotice(plan.PairId, whole.Folder)).ToArray()), ct).ConfigureAwait(false);
            SyncAction? anchor = null;
            WholeFolderRecovery? recovery = null;
            IReadOnlyList<FolderTreeOutcome> ended;
            try
            {
                ended = await ((IFolderTreeDeleter)target).DeleteFolderTreesAsync(
                    (side == Side.A ? plan.ScanCursorA : plan.ScanCursorB)!, byTree.Keys.ToArray(),
                    async (submit, providerState) =>
                    {
                        if (recovery is null)
                        {
                            var frozen = submit.Select(tree => new WholeFolderSnapshot(byTree[tree].Actions.ToArray())).ToArray();
                            if (frozen.Length == 0) return;
                            anchor = frozen[^1].Actions[^1];
                            recovery = new WholeFolderRecovery(plan.PairId,
                                (side == Side.A ? plan.ScanCursorA : plan.ScanCursorB)!, frozen, providerState);
                            await committer.RecordProviderCheckpointsAsync(frozen.SelectMany(tree => tree.Actions)
                                .Select(action => (action, action.ActionId == anchor.ActionId
                                    ? new ProviderEffectCheckpoint(WholeFolderGroupKind, JsonSerializer.Serialize(recovery))
                                    : new ProviderEffectCheckpoint(WholeFolderMemberKind, anchor.ActionId))).ToArray())
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            recovery = recovery with { ProviderState = providerState };
                            await committer.RecordProviderCheckpointsAsync([(anchor!,
                                new ProviderEffectCheckpoint(WholeFolderGroupKind, JsonSerializer.Serialize(recovery)))])
                                .ConfigureAwait(false);
                        }
                    }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && recovery is null)
            {
                remaining.AddRange(group.SelectMany(whole => whole.Actions));
                continue;
            }
            catch (Exception e) when (recovery is not null && FailureRetryOwner.ScopeOf(e, ct, isStateDamage: _isStateDamage) == FailureRetryOwner.FailureScope.Item)
            {
                // A timeout/cancellation after the checkpoint must never fall back to replaying per-item effects.
                ended = byTree.Keys.Select(tree => FolderTreeOutcome.Of(tree, FolderTreeStatus.Uncertain,
                    "The whole-folder operation needs recovery: " + e.Message)).ToArray();
            }
            var notices = new List<Notice>();
            var groupResults = new List<(SyncAction Action, ActionResult Result)>();
            foreach (var outcome in ended)
            {
                var whole = byTree[outcome.Tree];
                if (outcome.Status == FolderTreeStatus.NotSubmitted)
                {
                    await StoreAsync(() => CloseUnsubmittedFolderHistory(plan.PairId, whole.Folder),
                        CancellationToken.None).ConfigureAwait(false);
                    remaining.AddRange(whole.Actions);
                    continue;
                }
                whole.Handled = true;
                notices.Add(FolderHistoryNotice(plan.PairId, whole.Folder));
                if (outcome.Status == FolderTreeStatus.Completed && outcome.Reason is not null)
                    notices.Add(new Notice(plan.PairId, whole.Folder.Rule, NoticeKind.Info, whole.Folder.Path,
                        outcome.Reason, "folder-delete-refused"));
                var folderResults = new List<(SyncAction Action, ActionResult Result)>(whole.Actions.Count);
                foreach (var action in whole.Actions)
                    folderResults.Add((action, await WholeFolderResultAsync(action, outcome, target, folderResults, isStateDamage: _isStateDamage)
                        .ConfigureAwait(false)));
                groupResults.AddRange(folderResults);
                if (outcome.Restored.Count > 0)
                    notices.Add(FolderNotice(plan, whole.Folder, ExecutionReasons.FolderDeleteRestored, outcome.Restored));
                if (outcome.Unrestored.Count > 0)
                    notices.Add(FolderNotice(plan, whole.Folder, ExecutionReasons.FolderDeleteUnrestored,
                        outcome.Unrestored) with { FolderRecoveryAnchor = outcome.Status == FolderTreeStatus.Uncertain ? anchor?.ActionId : null });
            }
            if (notices.Count > 0)
                await StoreAsync(() => _store.AddNotices(notices), CancellationToken.None).ConfigureAwait(false);
            // Notices precede every resolving commit. The anchor stays unresolved until every member is durable.
            await Task.WhenAll(groupResults.Where(entry => entry.Action.ActionId != anchor?.ActionId)
                .Select(entry => committer.CommitAsync(entry.Action, entry.Result))).ConfigureAwait(false);
            var anchorIndex = groupResults.FindIndex(entry => entry.Action.ActionId == anchor?.ActionId);
            if (anchorIndex >= 0)
            {
                var entry = groupResults[anchorIndex];
                if (entry.Result.Outcome != Outcome.Uncertain &&
                    groupResults.Any(other => other.Result.Outcome == Outcome.Uncertain))
                    entry = (entry.Action, Uncertain(entry.Action, "a whole-folder group member still needs recovery"));
                groupResults[anchorIndex] = entry;
                await committer.CommitAsync(entry.Action, entry.Result).ConfigureAwait(false);
            }
            foreach (var (action, result) in groupResults)
            {
                outcomes[ActionRunner.OutcomeKey(action)] = result.Outcome;
                results.Add(result);
            }
        }
        return remaining;
    }

    // Never let a group member reach the ordinary per-path absence resolver: absence cannot prove that
    // an unexpected child was restored. The anchor retains even members whose results already committed.
    private async Task<Dictionary<string, ActionResult>> ReconcileWholeFoldersAsync(
        IReadOnlyList<SyncAction> intents, long gen, IEndpointWriter a, IEndpointWriter b, CancellationToken ct)
    {
        var scope = ReconcileRunScope.Current!;
        var resolved = new Dictionary<string, ActionResult>(StringComparer.Ordinal);
        var pending = intents.ToDictionary(action => action.ActionId, StringComparer.Ordinal);
        foreach (var action in intents.Where(action => action.ProviderEffect?.Kind is
                     WholeFolderGroupKind or WholeFolderMemberKind))
            resolved[action.ActionId] = Uncertain(action, "the whole-folder recovery anchor is unavailable");
        var anchors = intents.Where(action => action.ProviderEffect?.Kind == WholeFolderGroupKind)
            .ToDictionary(action => action.ActionId, StringComparer.Ordinal);
        // A streamed page may start with a member. Resolve its durable anchor, rather than
        // requiring the entire group to fit the caller's page or retiring the anchor early.
        foreach (var member in intents.Where(action => action.ProviderEffect?.Kind == WholeFolderMemberKind))
        {
            var anchorId = member.ProviderEffect!.Id;
            if (anchors.ContainsKey(anchorId) || scope.Anchors.Contains(anchorId) ||
                scope.MissingAnchors.Contains(anchorId)) continue;
            await StoreAsync(() =>
            {
                var pair = _store.PairForIntent(member.ActionId);
                if (pair is not null && _store.IntentById(pair, anchorId) is { } anchor &&
                    anchor.ProviderEffect?.Kind == WholeFolderGroupKind) anchors.TryAdd(anchorId, anchor);
                else scope.MissingAnchors.Add(anchorId);
            }, ct).ConfigureAwait(false);
        }
        foreach (var anchor in anchors.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (!scope.Anchors.Add(anchor.ActionId)) continue;
            WholeFolderRecovery recovery;
            try
            {
                recovery = JsonSerializer.Deserialize<WholeFolderRecovery>(anchor.ProviderEffect!.Id)
                    ?? throw new InvalidDataException("Missing recovery snapshot.");
                if (recovery.Trees.Length == 0 || recovery.Trees.Any(tree => tree.Actions.Length == 0))
                    throw new InvalidDataException("Empty recovery snapshot.");
            }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            {
                resolved[anchor.ActionId] = Uncertain(anchor, e.Message);
                continue;
            }
            var target = anchor.Target == Side.A ? a : b;
            if (target is not IFolderTreeDeleter deleter) continue;
            // The anchor already owns this frozen snapshot. Fetch only still-unresolved members
            // by identity, so all members commit before the anchor even across page boundaries.
            foreach (var page in recovery.Trees.SelectMany(tree => tree.Actions).Chunk(256))
            {
                await StoreAsync(() =>
                {
                    foreach (var action in page)
                        if (_store.IntentById(recovery.PairId, action.ActionId) is { } current)
                            pending[action.ActionId] = current;
                }, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            ReconcileRunScope.LogExpansion(intents, recovery.Trees.SelectMany(tree => tree.Actions));
            var committer = new StoreCommitter(_store, _storeGate, recovery.PairId, gen);
            var trees = recovery.Trees.Select(RecoveryTree).ToArray();
            IReadOnlyList<FolderTreeOutcome> ended;
            try
            {
                ended = await deleter.ReconcileFolderTreesAsync(recovery.ScanCursor, trees, recovery.ProviderState,
                    async state =>
                    {
                        recovery = recovery with { ProviderState = state };
                        await committer.RecordProviderCheckpointsAsync([(anchor,
                            new ProviderEffectCheckpoint(WholeFolderGroupKind, JsonSerializer.Serialize(recovery)))])
                            .ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (FailureRetryOwner.ScopeOf(e, CancellationToken.None, isStateDamage: _isStateDamage) == FailureRetryOwner.FailureScope.Item)
            {
                ended = trees.Select(tree => FolderTreeOutcome.Of(tree, FolderTreeStatus.Uncertain, e.Message)).ToArray();
            }
            var allResults = new List<(SyncAction Action, ActionResult Result)>();
            var notices = new List<Notice>();
            for (var i = 0; i < trees.Length; i++)
            {
                var tree = trees[i];
                var outcome = ended.FirstOrDefault(item => PairPath.Same(item.Tree.Folder.Path, tree.Folder.Path))
                    ?? FolderTreeOutcome.Of(tree, FolderTreeStatus.Uncertain, "The group check returned no result.");
                var snapshot = recovery.Trees[i];
                var folder = snapshot.Actions[^1];
                if (outcome.Status == FolderTreeStatus.NotSubmitted)
                    await StoreAsync(() => CloseUnsubmittedFolderHistory(recovery.PairId, folder),
                        CancellationToken.None).ConfigureAwait(false);
                else notices.Add(FolderHistoryNotice(recovery.PairId, folder));
                if (outcome.Status == FolderTreeStatus.Completed && outcome.Reason is not null)
                    notices.Add(new Notice(recovery.PairId, folder.Rule, NoticeKind.Info, folder.Path,
                        outcome.Reason, "folder-delete-refused"));
                var folderResults = new List<(SyncAction Action, ActionResult Result)>();
                foreach (var action in snapshot.Actions)
                {
                    var result = outcome.Status == FolderTreeStatus.NotSubmitted
                        ? new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, EffectResolver.Superseded)
                        : await WholeFolderResultAsync(action, outcome, target, folderResults, isStateDamage: _isStateDamage).ConfigureAwait(false);
                    folderResults.Add((action, result));
                }
                allResults.AddRange(folderResults);
                if (outcome.Restored.Count > 0)
                    notices.Add(FolderNotice(recovery.PairId, folder, ExecutionReasons.FolderDeleteRestored, outcome.Restored));
                if (outcome.Unrestored.Count > 0)
                    notices.Add(FolderNotice(recovery.PairId, folder, ExecutionReasons.FolderDeleteUnrestored, outcome.Unrestored)
                        with { FolderRecoveryAnchor = outcome.Status == FolderTreeStatus.Uncertain ? anchor.ActionId : null });
                else if (outcome.Status == FolderTreeStatus.Completed)
                    await StoreAsync(() =>
                    {
                        foreach (var notice in _store.UnresolvedNotices(recovery.PairId).Where(n =>
                            n.ReasonKey == "folder-delete-unrestored" && n.FolderRecoveryAnchor == anchor.ActionId &&
                            PairPath.Same(n.Path, folder.Path)))
                            _store.CloseNotice(notice.NoticeId!, "restored");
                    }, CancellationToken.None).ConfigureAwait(false);
            }
            if (notices.Count > 0)
                await StoreAsync(() => _store.AddNotices(notices), CancellationToken.None).ConfigureAwait(false);
            // Recheck the entire original snapshot, but never duplicate completed results/recovery rows.
            var memberResults = allResults.Where(entry => entry.Action.ActionId != anchor.ActionId &&
                pending.ContainsKey(entry.Action.ActionId)).ToArray();
            await Task.WhenAll(memberResults.Select(entry =>
                committer.CommitAsync(pending[entry.Action.ActionId], entry.Result))).ConfigureAwait(false);
            foreach (var (action, result) in memberResults) resolved[action.ActionId] = result;
            var anchorResult = allResults.FirstOrDefault(entry => entry.Action.ActionId == anchor.ActionId).Result;
            if (anchorResult is null || allResults.Any(entry => entry.Result.Outcome == Outcome.Uncertain))
                anchorResult = Uncertain(anchor, "a whole-folder group member still needs recovery");
            await committer.CommitAsync(anchor, anchorResult).ConfigureAwait(false);
            resolved[anchor.ActionId] = anchorResult;
            if (anchorResult.Outcome == Outcome.Uncertain)
            {
                if (StuckJobNotice(recovery.PairId, anchor) is { } notice)
                    await StoreAsync(() => _store.AddNotices([notice]), CancellationToken.None).ConfigureAwait(false);
            }
            else
                await StoreAsync(() => CloseStuckJobNotice(recovery.PairId, anchor), CancellationToken.None).ConfigureAwait(false);
        }
        return resolved;
    }

    // One child's result from its folder's outcome. The folder's own action comes last and is Done only when every
    // victim under it is confirmed.
    private static async Task<ActionResult> WholeFolderResultAsync(SyncAction action, FolderTreeOutcome outcome,
        IEndpointWriter target, IReadOnlyList<(SyncAction Action, ActionResult Result)> earlier, Func<Exception, bool> isStateDamage)
    {
        ArgumentNullException.ThrowIfNull(isStateDamage);
        if (outcome.Status == FolderTreeStatus.Uncertain)
            return Uncertain(action, outcome.Reason ?? "the whole-folder delete was not checked");
        if (outcome.Kept.Contains(action.Path))
            return new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, EffectResolver.Superseded);
        if (outcome.Unconfirmed.Contains(action.Path))
            return Uncertain(action, "the change feed did not report this item deleted");
        if (PairPath.Same(action.Path, outcome.Tree.Folder.Path))
            return earlier.Any(entry => entry.Result.Outcome == Outcome.Uncertain)
                ? Uncertain(action, "an item under the folder was not confirmed deleted")
                : new ActionResult(action.ActionId, Outcome.Done, null, null, null, null);
        RecoveryRef? recovery = null;
        if (action.RecoveryFirst && action.ExpectedTarget is not null)
        {
            // Dropbox keeps the deleted revision in its own history; the reference names it, with no provider call.
            try { recovery = await target.PreserveAsync(action.ExpectedTarget, action.ActionId, CancellationToken.None); }
            catch (Exception e) when (FailureRetryOwner.ScopeOf(e, CancellationToken.None, isStateDamage: isStateDamage) == FailureRetryOwner.FailureScope.Item)
            {
                return Uncertain(action, string.Format(ExecutionReasons.RecoveryUnverified.Detail, e.Message));
            }
        }
        return new ActionResult(action.ActionId, Outcome.Done, null, null, recovery, null);
    }

    private static ActionResult Uncertain(SyncAction action, string detail) =>
        new(action.ActionId, Outcome.Uncertain, null, null, null, string.Format(ExecutionReasons.Unverified.Detail, detail));

    private void CloseUnsubmittedFolderHistory(string pairId, SyncAction folder)
    {
        foreach (var notice in _store.UnresolvedNotices(pairId).Where(notice => notice.ReasonKey == "folder-delete-history" &&
                     notice.Rule == folder.Rule && PairPath.Same(notice.Path, folder.Path)))
            _store.CloseNotice(notice.NoticeId!, "not-submitted");
    }

    private static Notice FolderHistoryNotice(string pairId, SyncAction folder) =>
        new(pairId, folder.Rule, NoticeKind.Info, folder.Path,
            "Prepared folder deletion (submission may still be pending or refused): " + folder.Path +
            ". Content is recoverable from Dropbox deleted files and version history. " +
            "An edit landing during the delete itself, if any, is in that history.", "folder-delete-history");

    private static Notice FolderNotice(Plan plan, SyncAction folder, NoticeReason reason, IReadOnlyList<string> paths) =>
        FolderNotice(plan.PairId, folder, reason, paths);

    private static Notice FolderNotice(string pairId, SyncAction folder, NoticeReason reason, IReadOnlyList<string> paths)
    {
        var named = string.Join(", ", paths.Take(NoticePathLimit));
        if (paths.Count > NoticePathLimit) named += $" and {paths.Count - NoticePathLimit} more";
        return new Notice(pairId, folder.Rule, NoticeKind.Info, folder.Path,
            string.Format(reason.Detail, folder.Path, paths.Count, named), reason.Key, FolderDeleteEpisode: folder.ActionId);
    }
}
