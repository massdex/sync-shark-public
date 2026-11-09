using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

// One folder-continuation lifetime: capture, grouped removal and survivor continuation share the unfinished-operation map under _unfinishedGate.
/// <summary>Owns folder-plan capture, grouped removal and survivor continuation.</summary>
internal sealed class FolderSync(ExecutorContext _context, Parents _parents, ActionExecutor _executor,
    FolderDeleteRunner _folderDelete, SyncPorts _ports, ItemSync _items, AncestorFence _ancestors)
{
    private readonly Dictionary<(Side Side, string Key), string> _unfinished = [];
    private readonly object _unfinishedGate = new();
    private bool _unfinishedLoaded;
    private static readonly IReadOnlySet<string> NoRestoredItems = new HashSet<string>();
    private enum RemovalRoute { Ordinary, PlannedReplacement, SelectedReplacement, Chosen }

    /// <summary>Freezes one justified operation and releases scratch before returning its durable identifier.</summary>
    internal Task<string?> PrepareFolderRemovalAsync(Row folder, Side deletingFrom,
        IReadOnlySet<string> restored, IReadOnlySet<string> restoredAncestors, CancellationToken ct,
        Action<Row, SyncOutcome>? completed = null) =>
        PrepareFolderRemovalCoreAsync(folder, deletingFrom, restored, restoredAncestors, ct, completed, RemovalRoute.Ordinary, null, null, false);

    private async Task<string?> PrepareFolderRemovalCoreAsync(Row folder, Side deletingFrom,
        IReadOnlySet<string> restored, IReadOnlySet<string> restoredAncestors, CancellationToken ct,
        Action<Row, SyncOutcome>? completed, RemovalRoute route, Exclusion? exclusion, FolderPlan? captured, bool live)
    {
        Row observed = folder;
        await using (var work = await _context.Admission.EnterWorkAsync(false, ct))
        await using (var permit = await _context.EnterOperationAsync(ct))
        {
            var paths = Row.Paths(folder);
            var scopes = LockKeys.ForPaths(_context.Pair, [(Side.Local, paths.Local), (Side.Dropbox, paths.Dropbox)])
                .Select(key => new PathLockScope(key, true)).ToArray();
            await using var hold = await PathLocks.Shared.EnterAsync(scopes, ct);
            try
            {
                observed = await _items.Preparation.ReobserveAsync(folder, new Observer(_context, _parents), ct);
                RequireAncestorsClear(observed);
                var justified = route switch
                {
                    RemovalRoute.SelectedReplacement => observed.Local == folder.Local && observed.Dropbox == folder.Dropbox,
                    RemovalRoute.PlannedReplacement => PlannedReplacementJustified(observed, folder, deletingFrom),
                    _ => FolderDeletes.PlanJustified(observed.EntryOn(deletingFrom), observed.EntryOn(PathNames.Other(deletingFrom)),
                        exclusion is not null || route == RemovalRoute.Chosen || restoredAncestors.Contains(folder.Key))
                };
                if (justified) return await BuildAndFreezeAsync(observed, deletingFrom, restored, live, ct, route, exclusion, captured);
                if (exclusion is not null || route != RemovalRoute.Ordinary) return null;
            }
            catch (Exception error) when (exclusion is null && route == RemovalRoute.Ordinary && ItemFailures.IsItemError(error))
            {
                // Replacement and resolution callers retain their own attempt; ordinary Repair owns this row here.
                var failed = await _executor.Failures.FailAsync(observed, null, error, ct);
                completed?.Invoke(observed, failed);
                return null;
            }
        }
        await _items.RunRowAttemptAsync(observed, ct, completed: completed);
        return null;
    }

    // The folder-plan ancestor refusal has one home; each caller's custody and plan-failure policy stay outside.
    private void RequireAncestorsClear(Row observed)
    {
        if (!_ancestors.AncestorsClear(observed, null, null))
            throw new ChangedUnderneathException(Row.Path(observed));
    }

    private async Task<string> BuildAndFreezeAsync(Row observed, Side deletingFrom, IReadOnlySet<string> restored,
        bool live, CancellationToken ct, RemovalRoute route, Exclusion? exclusion, FolderPlan? captured)
    {
        if (UnfinishedAt(observed, deletingFrom, ct) is { } unfinished)
        {
            captured?.Dispose();
            return unfinished;
        }
        captured?.PrepareRemoval(observed);
        var builder = new FolderPlanBuilder(_context);
        var operationId = FreezePlan(exclusion is not null
            ? await builder.BuildExcludedAsync(captured ?? throw new InvalidOperationException(), exclusion, ct)
            : route == RemovalRoute.SelectedReplacement
                ? await builder.BuildSelectedReplacementAsync(observed, restored, ct)
                : await builder.BuildAsync(observed, deletingFrom, restored, live, ct), deletingFrom, ct);
        RememberUnfinished(deletingFrom, observed, operationId);
        return operationId;
    }

    // One root keeps one operation: every route that would plan this root continues the unfinished one instead, so
    // repeated Repairs and resolutions add none (3.8).
    private string? UnfinishedAt(Row folder, Side deletingFrom, CancellationToken ct)
    {
        var root = PathNames.KeyFor(deletingFrom, folder.EntryOn(deletingFrom)!.Path);
        lock (_unfinishedGate)
        {
            // One active-set read per FolderSync, not one scan per planned root; freeze and finish keep it current (3.7).
            if (!_unfinishedLoaded)
            {
                foreach (var operation in _context.Store.ActiveFolderRemovals(_context.Pair.PairId, ct))
                    _unfinished[(operation.DeletingSide,
                        PathNames.KeyFor(operation.DeletingSide, operation.RootEntry.Path))] = operation.OperationId;
                _unfinishedLoaded = true;
            }
            return _unfinished.GetValueOrDefault((deletingFrom, root));
        }
    }

    private void RememberUnfinished(Side deletingFrom, Row observed, string operationId)
    {
        var key = (deletingFrom, PathNames.KeyFor(deletingFrom, observed.EntryOn(deletingFrom)!.Path));
        lock (_unfinishedGate) _unfinished[key] = operationId;
    }

    private void ForgetUnfinished(Side side, string rootPath)
    {
        var key = (side, PathNames.KeyFor(side, rootPath));
        lock (_unfinishedGate) _unfinished.Remove(key);
    }

    private string FreezePlan(FolderPlan plan, Side deletingFrom, CancellationToken ct)
    {
        string operationId;
        try
        {
            // A frozen plan becomes recovery work, so the live authority is read after its capture, here (R6-4).
            ItemSync.RequireLiveWrite(_context, deletingFrom == Side.Local);
            operationId = _context.Store.BeginFolderRemoval(_context.Pair.PairId, plan, _context.Settings.StoragePageSize, ct);
        }
        catch (Exception primary)
        {
            try { plan.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
            throw;
        }
        plan.Dispose();
        return operationId;
    }

    /// <summary>Reaches released survivors on an ordinary work opportunity without replaying unresolved effects.</summary>
    internal async Task ContinueReleasedFollowUpsAsync(CancellationToken ct)
    {
        foreach (var operation in _context.Store.ActiveFolderRemovals(_context.Pair.PairId, ct))
        {
            if (!PendingFollowUps(operation.OperationId, ct).Any(FollowUpReleased)) continue;
            if (_folderDelete.FollowUpsReady(operation, ct))
                await FinishFolderRemovalAsync(operation.OperationId, null, ct);
        }
    }

    private bool FollowUpReleased(FolderRemovalItem item) => item.Expected is not null &&
        !_context.Store.HeldCovers(_context.Pair.PairId, item.PolicyKey, null);

    private IEnumerable<FolderRemovalItem> PendingFollowUps(string operationId, CancellationToken ct)
    {
        var after = 0 - 1L;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = _context.Store.PendingFolderFollowUps(_context.Pair.PairId, operationId, after,
                _context.Settings.StoragePageSize).ToArray();
            if (page.Length == 0) yield break;
            after = page[^1].Ordinal;
            foreach (var item in page) yield return item;
        }
    }

    /// <summary>Runs independently admitted survivor reconciliation and returns actual durable aggregate state.</summary>
    internal async Task<FolderRemovalResult> FinishFolderRemovalAsync(string operationId, WorkSelection? selection, CancellationToken ct,
        Action<Row, SyncOutcome>? completed = null, Action<Row, Issue>? retainUnfinished = null)
    {
        var operation = _context.Store.FolderRemoval(_context.Pair.PairId, operationId);
        await _folderDelete.ResumeAsync(operation, selection, ct);
        operation = _context.Store.FolderRemoval(_context.Pair.PairId, operationId);
        if (_folderDelete.FollowUpsReady(operation, ct) &&
            _context.Store.RenameRemovalWork(_context.Pair.PairId, operationId) is null)
        {
            var cycles = new Dictionary<long, (WorkSelection Blocker, Side Side, List<(string Path, long Ordinal, long Generation)>? Members)>();
            foreach (var item in PendingFollowUps(operationId, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (FollowUpReleased(item))
                    await FinishSurvivorAsync(operation, item, selection, cycles, ct, completed, retainUnfinished);
            }
            foreach (var group in cycles.Values)
                if (group.Members is { } members)
                    await ReportCyclesAsync(operation, group.Blocker, group.Side, members, selection!, ct, completed, retainUnfinished);
        }
        operation = _context.Store.FolderRemoval(_context.Pair.PairId, operationId);
        await _folderDelete.ResumeAsync(operation, selection, ct);
        operation = _context.Store.FolderRemoval(_context.Pair.PairId, operationId);
        if (!operation.Active) ForgetUnfinished(operation.DeletingSide, operation.RootEntry.Path);
        return new FolderRemovalResult(operationId, operation.RootEntry.Path,
            FolderDeleteRunner.Status(operation.Phase));
    }

    private async Task FinishSurvivorAsync(FolderRemovalState operation, FolderRemovalItem item,
        WorkSelection? selection,
        Dictionary<long, (WorkSelection Blocker, Side Side, List<(string Path, long Ordinal, long Generation)>? Members)> cycles, CancellationToken ct,
        Action<Row, SyncOutcome>? completed, Action<Row, Issue>? retainUnfinished)
    {
        SyncOutcome outcome;
        try
        {
            _ancestors.RequireClear(null, item.Saved, null);
            Row? deferred = null;
            outcome = await _ports.Recovery.ExecuteAsync(operation, (execution, token) =>
                _items.Observation.SyncFollowUpAsync(operation.DeletingSide, item.Expected!.Path, token, execution,
                    retainUnfinished, (row, result) =>
                    {
                        if (result.Deferred) deferred = row;
                        else completed?.Invoke(row, result);
                    }), ct);
            // Each nested plan starts after its parent's lease is disposed, and descends one path level.
            if (deferred is not null)
                outcome = await FinishDeferredAsync(deferred, selection, ct, completed, retainUnfinished);
        }
        catch (LiveAdmissionStaleException error) when (ItemSync.LiveWriteRefused(error) ||
            error.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged)
        {
            _context.Events(RunEvent.Failed(error, path: item.Expected!.Path));
            if (!ItemSync.LiveWriteRefused(error)) _ancestors.ReportContainedRefusal();
            RememberCycle(item, selection, error, cycles);
            return;
        }
        if (outcome.Succeeded())
            _context.Store.CompleteFolderFollowUp(_context.Pair.PairId, operation.OperationId, item.Ordinal, item.Generation);
    }

    private async Task<SyncOutcome> FinishDeferredAsync(Row row, WorkSelection? selection, CancellationToken ct,
        Action<Row, SyncOutcome>? completed, Action<Row, Issue>? retainUnfinished)
    {
        var outcome = new SyncOutcome(ActionKind.DeferFolder, null, null, null, true);
        var failed = false;
        void Completed(Row effective, SyncOutcome result)
        {
            outcome = result;
            failed |= !result.Succeeded();
            completed?.Invoke(effective, result);
        }
        var id = await PrepareFolderRemovalCoreAsync(row, FolderDeletes.DeletingSide(row),
            NoRestoredItems, NoRestoredItems, ct, Completed, RemovalRoute.Ordinary, null, null, _context.Live);
        if (id is null) return outcome;
        await RunPreparedFolderRemovalsAsync([id], ct);
        var result = await FinishFolderRemovalAsync(id, selection, ct, Completed, retainUnfinished);
        outcome = !failed && SettlementReady(result) ? SyncOutcome.InSync :
            new SyncOutcome(ActionKind.DeferFolder, null, null, null, true);
        completed?.Invoke(row, outcome);
        return outcome;
    }

    private void RememberCycle(FolderRemovalItem item, WorkSelection? selection, LiveAdmissionStaleException error,
        Dictionary<long, (WorkSelection Blocker, Side Side, List<(string Path, long Ordinal, long Generation)>? Members)> cycles)
    {
        if (selection is null || error.AncestorBlocker is not { } blocker) return;
        if (!cycles.TryGetValue(blocker.Selection.AnchorWorkId, out var group))
        {
            group = (blocker.Selection, blocker.Side,
                _ancestors.ReachesSelection(blocker.Selection, selection, _parents) ? [] : null);
            cycles.Add(blocker.Selection.AnchorWorkId, group);
        }
        group.Members?.Add((item.Path, item.Ordinal, item.Generation));
    }

    private async Task ReportCyclesAsync(FolderRemovalState operation, WorkSelection blocker, Side side,
        List<(string Path, long Ordinal, long Generation)> members, WorkSelection selection, CancellationToken ct,
        Action<Row, SyncOutcome>? completed, Action<Row, Issue>? retainUnfinished)
    {
        if (!_context.Store.FolderRemoval(_context.Pair.PairId, operation.OperationId).Active) return;
        // Recheck members first, then the shared blocker immediately before publishing this group.
        var current = new List<(string Key, Entry? Expected, SyncedEntry? Saved)>();
        foreach (var item in members)
        {
            ct.ThrowIfCancellationRequested();
            var member = _context.Store.FolderRemovalItemByKey(_context.Pair.PairId, operation.OperationId,
                PathNames.KeyFor(operation.DeletingSide, item.Path));
            if (member is not null && member.Ordinal == item.Ordinal && member.Generation == item.Generation &&
                member.FollowUpState == FolderRemovalItem.FollowUpStatus.Pending)
                current.Add((member.PolicyKey, member.Expected, member.Saved));
        }
        if (current.Count == 0 || !_context.Store.PendingAncestors(_context.Pair.PairId, _context.ExpectedGeneration,
                side, [blocker.Key]).Any(owner => owner.AnchorWorkId == blocker.AnchorWorkId) ||
            !_ancestors.ReachesSelection(blocker, selection, _parents)) return;
        foreach (var item in current)
        {
            var row = Row.ForOneSide(operation.DeletingSide, item.Key, item.Expected, item.Saved,
                [], null, RowContext.Neutral);
            var failed = await _executor.Failures.FailAsync(row, null,
                new ItemFailedException(item.Expected!.Path, TextKey.FailFolderDependencyCycle, null), ct,
                retainUnfinished is null ? null : issue => retainUnfinished(row, issue));
            completed?.Invoke(row, failed);
        }
    }

    private bool SettlementReady(FolderRemovalResult result)
    {
        // Preserved survivors settle the selection without claiming that the whole folder was removed.
        var operation = _context.Store.FolderRemoval(_context.Pair.PairId, result.OperationId);
        return result.Status == FolderRemovalStatus.Complete ||
            !operation.Active && operation.Cause is null && operation.RetirementDisposition is not null;
    }

    internal Task RunPreparedFolderRemovalsAsync(IEnumerable<string> operationIds, CancellationToken ct) =>
        _folderDelete.RunPreparedAsync(Pages(operationIds), ct);

    // Supplies ready effects one stored page at a time; the batch owner streams them into provider-legal batches.
    private IEnumerable<IReadOnlyList<FolderRemovalEffect>> Pages(IEnumerable<string> operationIds)
    {
        foreach (var id in operationIds)
        {
            var after = 0 - 1L;
            while (true)
            {
                var page = _context.Store.ReadyFolderEffects(_context.Pair.PairId, id, after,
                    _context.Settings.StoragePageSize).ToArray();
                if (page.Length == 0) break;
                after = page[^1].ItemOrdinal;
                yield return page;
            }
        }
    }

    internal Task<FolderRemovalResult?> RemoveExcludedAsync(Row folder, Exclusion exclusion, FolderPlan captured,
        CancellationToken ct) => RemoveFolderCoreAsync(folder, exclusion.Side, ct, RemovalRoute.Ordinary, exclusion, captured);

    internal Task<FolderRemovalResult?> RemoveChosenAsync(SyncAction action, IReadOnlyList<SyncAction> actions,
        CancellationToken ct)
    {
        var side = action.WrittenSide();
        var source = PathNames.Other(side);
        // The other selected action's observed folder supplies the preserved counterpart for replacement.
        var incoming = actions.Where(other => !ReferenceEquals(other, action))
            .Select(other => other.Row.EntryOn(source))
            .FirstOrDefault(entry => entry is { IsFolder: true });
        if (side == Side.Dropbox)
            return RemoveSelectedReplacementAsync(action.Row with { Local = incoming }, ct);
        return RemoveFolderCoreAsync(action.Row, side, ct,
            incoming is null ? RemovalRoute.Ordinary : RemovalRoute.Chosen, null, null);
    }

    private bool PlannedReplacementJustified(Row observed, Row folder, Side deletingFrom) =>
        deletingFrom == Side.Dropbox && observed.Local == folder.Local && observed.Dropbox == folder.Dropbox &&
        observed.Synced == folder.Synced && ItemReplacement.RequiresFolderReplacement(Decide.For(observed,
            _context.Pair.FirstRun, _context.Time.GetUtcNow(), _items.Preparation.LocalContentDirty(observed) == true));

    internal async Task<FolderRemovalResult?> RemoveLiveReplacementAsync(SyncAction action, WorkSelection selection, CancellationToken ct)
    {
        var footprint = LockKeys.ForLiveAction(action, _context.Pair, null);
        var coverage = (await _ports.Live.CaptureCoverageAsync(selection, footprint, ct)) with { Peer = selection.Peer };
        var id = await _ports.Live.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
            async (execution, token) =>
            {
                execution.RequireScopes(_context.Pair.PairId, selection.PairGeneration, LockKeys.ForScopes(_context.Pair, footprint));
                var observed = await _items.Preparation.ReobserveAsync(action.Row, new Observer(_context, _parents), token);
                // A live selection applies only its admitted evidence; a later acquisition waits for its own record.
                execution.Revalidate();
                RequireAncestorsClear(observed);
                if (!PlannedReplacementJustified(observed, action.Row, Side.Dropbox)) throw new ChangedUnderneathException(Row.Path(observed));
                return await BuildAndFreezeAsync(observed, Side.Dropbox, NoRestoredItems, true, token, RemovalRoute.PlannedReplacement, null, null);
            }, ct);
        await RunPreparedFolderRemovalsAsync([id], ct);
        return await FinishFolderRemovalAsync(id, selection, ct);
    }

    internal async Task<FolderRemovalResult?> RemoveVacatedAsync(Row destination, RenameCandidate incoming,
        WorkSelection selection, CancellationToken ct)
    {
        var deletingFrom = PathNames.Other(incoming.Side);
        if (_context.Store.RenameRemoval(selection) is { } existing)
        {
            var operation = _context.Store.FolderRemoval(_context.Pair.PairId, existing);
            if (operation.SavedRoot != destination.Synced) return null;
            if (operation.Phase == FolderRemovalPhase.Complete)
                return new FolderRemovalResult(existing, operation.RootEntry.Path, FolderRemovalStatus.Complete);
            if (operation.Active && operation.Phase == FolderRemovalPhase.Prepared)
                await RunPreparedFolderRemovalsAsync([existing], ct);
            return await FinishFolderRemovalAsync(existing, selection, ct);
        }
        var paths = Row.Paths(destination);
        var footprint = LockKeys.ForLivePaths(_context.Pair,
            [(Side.Local, paths.Local), (Side.Dropbox, paths.Dropbox)], true)
            .Concat(LockKeys.ForRenamePaths(_context.Pair, [(incoming.Side, incoming.Moved.Path)], true)).ToArray();
        string? operationId;
        if (_context.Live)
        {
            var coverage = (await _ports.Live.CaptureCoverageAsync(selection, footprint, ct)) with { Peer = selection.Peer };
            operationId = await _ports.Live.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
                (execution, token) => PrepareAsync(execution, token), ct);
        }
        else
        {
            await using var work = await _context.Admission.EnterWorkAsync(false, ct);
            await using var permit = await _context.EnterOperationAsync(ct);
            await using var hold = await PathLocks.Shared.EnterAsync(LockKeys.ForScopes(_context.Pair, footprint), ct);
            operationId = await PrepareAsync(null, ct);
        }
        if (operationId is null) return null;
        await RunPreparedFolderRemovalsAsync([operationId], ct);
        return await FinishFolderRemovalAsync(operationId, selection, ct);

        async Task<string?> PrepareAsync(IAdmittedExecution? execution, CancellationToken ct)
        {
            execution?.RequireScopes(_context.Pair.PairId, _context.ExpectedGeneration,
                LockKeys.ForScopes(_context.Pair, footprint));
            var saved = destination.Synced ?? throw new ArgumentException(null, nameof(destination));
            var freshIncoming = await _context.For(incoming.Side).ReadOneAsync(incoming.Moved.Path, ct);
            var occupant = await _context.For(deletingFrom).ReadOneAsync(saved.PathOn(deletingFrom), ct);
            if (freshIncoming != incoming.Moved || occupant != destination.EntryOn(deletingFrom) ||
                _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, incoming.Synced.LocalKey) != incoming.Synced ||
                _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, saved.LocalKey) != saved ||
                _context.For(incoming.Side) is not IIdentitySide identities) return null;
            var after = SyncedEntries.RenameTarget(incoming, _parents.Destination(deletingFrom, incoming.Moved.Path));
            var blockers = _context.Store.RenameDestinationOwners(_context.Pair.PairId, incoming.Synced, after);
            if (blockers.Count != 1 || blockers[0].Binding != saved) return null;
            var location = await identities.ReadByIdAsync(saved.IdOn(incoming.Side), ct);
            if (location is { InScope: true }) return null;
            var observed = new Row(saved.LocalKey,
                incoming.Side == Side.Local ? freshIncoming : occupant,
                incoming.Side == Side.Dropbox ? freshIncoming : occupant,
                saved, [], null, destination.Context);
            if (_context.Scope.Excludes(saved.LocalPath, saved.DropboxPath, true) ||
                _context.Scope.HoldsNestedRoot(Side.Local, saved.LocalPath) ||
                _context.Scope.HoldsNestedRoot(Side.Dropbox, saved.DropboxPath) || occupant?.Skip is not null) return null;
            var clash = _context.Store.ReadRenameContinuation(selection)?.Clash;
            var currentClash = clash is null ? null : _context.Store.IssueById(_context.Pair.PairId, clash.Id);
            if (clash is not null && (currentClash is null || !currentClash.Matches(clash) || currentClash.Ignored || currentClash.Dismissed)) return null;
            if (clash is null ? _context.Store.HoldingIssue(_context.Pair.PairId, observed) is not null :
                _context.Store.HeldByOtherIssue(_context.Pair.PairId, observed, clash.Id)) return null;
            RequireAncestorsClear(observed);
            using var plan = await new FolderPlanBuilder(_context).BuildAsync(observed, deletingFrom, NoRestoredItems, true, ct);
            if (!plan.DeleteWhole) return null;
            execution?.Revalidate();
            ItemSync.RequireLiveWrite(_context, deletingFrom == Side.Local);
            var id = _context.Store.BeginFolderRemoval(_context.Pair.PairId, plan,
                _context.Settings.StoragePageSize, ct, selection);
            RememberUnfinished(deletingFrom, observed, id);
            return id;
        }
    }

    internal Task<FolderRemovalResult?> RemovePlannedReplacementAsync(Row folder, CancellationToken ct) =>
        RemoveFolderCoreAsync(folder, Side.Dropbox, ct, RemovalRoute.PlannedReplacement, null, null);

    internal Task<FolderRemovalResult?> RemoveSelectedReplacementAsync(Row folder, CancellationToken ct) =>
        RemoveFolderCoreAsync(folder, Side.Dropbox, ct, RemovalRoute.SelectedReplacement, null, null);

    private async Task<FolderRemovalResult?> RemoveFolderCoreAsync(Row folder, Side deletingFrom,
        CancellationToken ct, RemovalRoute route, Exclusion? exclusion, FolderPlan? captured)
    {
        var id = await PrepareFolderRemovalCoreAsync(folder, deletingFrom, NoRestoredItems, NoRestoredItems, ct, null, route, exclusion, captured, false);
        if (id is null) return null;
        await RunPreparedFolderRemovalsAsync([id], ct);
        return await FinishFolderRemovalAsync(id, null, ct);
    }
    // One live plan: admission, reobserve and the freeze decision run in one admitted execution and return a durable operation id; removal and finish run against that id after the execution ends.
    internal async Task<bool> RunLiveFolderPlanAsync(Row folder, Action<Row, Issue> retainUnfinished,
        WorkSelection selection, LiveCoverage coverage, IReadOnlyList<LiveEffectScope> footprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retainUnfinished);
        var deletingFrom = FolderDeletes.DeletingSide(folder);
        Row observed = folder;
        SyncOutcome? fallback = null;
        var id = await _ports.Live.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
            async (execution, token) =>
            {
                execution.RequireScopes(_context.Pair.PairId, selection.PairGeneration, LockKeys.ForScopes(_context.Pair, footprint));
                try
                {
                    observed = await _items.Preparation.ReobserveAsync(folder, new Observer(_context, _parents), token);
                    // A live selection applies only its admitted evidence; a later acquisition waits for its own record.
                    execution.Revalidate();
                    if (!FolderDeletes.PlanJustified(observed.EntryOn(deletingFrom), observed.EntryOn(PathNames.Other(deletingFrom)),
                        false))
                    {
                        fallback = await _items.Execution.RunAdmittedAsync(observed, token, execution: execution,
                            selection: selection, retainUnfinished: retainUnfinished);
                        return (string?)null;
                    }
                    RequireAncestorsClear(observed);
                    return await BuildAndFreezeAsync(observed, deletingFrom, NoRestoredItems, true, token, RemovalRoute.Ordinary, null, null);
                }
                catch (Exception error) when (error is not ChangedUnderneathException && ItemFailures.IsItemError(error))
                {
                    fallback = await _executor.Failures.FailAsync(observed, null, error, token,
                        issue => retainUnfinished(observed, issue), selection: selection);
                    return (string?)null;
                }
            }, ct);
        if (id is null) return fallback?.Succeeded() == true;
        await RunPreparedFolderRemovalsAsync([id], ct);
        var failed = false;
        var result = await FinishFolderRemovalAsync(id, selection, ct, (_, outcome) => failed |= !outcome.Succeeded(), retainUnfinished);
        return !failed && SettlementReady(result);
    }
}
