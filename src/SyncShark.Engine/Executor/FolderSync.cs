using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

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
        PrepareFolderRemovalCoreAsync(folder, deletingFrom, restored, restoredAncestors, ct, completed, RemovalRoute.Ordinary, null, null);

    private async Task<string?> PrepareFolderRemovalCoreAsync(Row folder, Side deletingFrom,
        IReadOnlySet<string> restored, IReadOnlySet<string> restoredAncestors, CancellationToken ct,
        Action<Row, SyncOutcome>? completed, RemovalRoute route, Exclusion? exclusion, FolderPlan? captured)
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
                observed = await _items.ReobserveAsync(folder, new Observer(_context, _parents), ct);
                RequireAncestorsClear(observed);
                var justified = route switch
                {
                    RemovalRoute.SelectedReplacement => observed.Local == folder.Local && observed.Dropbox == folder.Dropbox,
                    RemovalRoute.PlannedReplacement => PlannedReplacementJustified(observed, folder, deletingFrom),
                    _ => FolderDeletes.PlanJustified(observed.EntryOn(deletingFrom), observed.EntryOn(PathNames.Other(deletingFrom)),
                        exclusion is not null || route == RemovalRoute.Chosen || restoredAncestors.Contains(folder.Key))
                };
                if (justified) return await BuildAndFreezeAsync(observed, deletingFrom, restored, false, ct, route, exclusion, captured);
                if (exclusion is not null || route != RemovalRoute.Ordinary) return null;
            }
            catch (Exception error) when (exclusion is null && route == RemovalRoute.Ordinary && ActionExecutor.IsItemError(error))
            {
                // Replacement and resolution callers retain their own attempt; ordinary Repair owns this row here.
                var failed = await _executor.FailAsync(observed, null, error, ct);
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
        if (_folderDelete.FollowUpsReady(operation, ct))
        {
            foreach (var item in PendingFollowUps(operationId, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (!FollowUpReleased(item)) continue;
                SyncOutcome outcome;
                try
                {
                    outcome = await _ports.Recovery.ExecuteAsync(operation, (execution, token) =>
                        _items.SyncFollowUpAsync(operation.DeletingSide, item.Expected!.Path, token, execution,
                            retainUnfinished, completed), ct);
                }
                catch (LiveAdmissionStaleException error) when (ItemSync.LiveWriteRefused(error))
                {
                    // The refused survivor stays pending for the pair's next wake; later survivors still run (R6-6).
                    _context.Events(RunEvent.Failed(error, path: item.Expected!.Path));
                    continue;
                }
                if (outcome.Succeeded())
                    _context.Store.CompleteFolderFollowUp(_context.Pair.PairId, operationId, item.Ordinal, item.Generation);
            }
        }
        operation = _context.Store.FolderRemoval(_context.Pair.PairId, operationId);
        await _folderDelete.ResumeAsync(operation, selection, ct);
        operation = _context.Store.FolderRemoval(_context.Pair.PairId, operationId);
        if (!operation.Active) ForgetUnfinished(operation.DeletingSide, operation.RootEntry.Path);
        return new FolderRemovalResult(operationId, operation.RootEntry.Path,
            FolderDeleteRunner.Status(operation.Phase));
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
        observed.Synced == folder.Synced && ItemSync.RequiresFolderReplacement(Decide.For(observed,
            _context.Pair.FirstRun, _context.Time.GetUtcNow(), _items.LocalContentDirty(observed) == true));

    internal async Task<FolderRemovalResult?> RemoveLiveReplacementAsync(SyncAction action, WorkSelection selection, CancellationToken ct)
    {
        var footprint = LockKeys.ForLiveAction(action, _context.Pair, null);
        var coverage = (await _ports.Live.CaptureCoverageAsync(selection, footprint, ct)) with { Peer = selection.Peer };
        var id = await _ports.Live.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
            async (execution, token) =>
            {
                execution.RequireScopes(_context.Pair.PairId, selection.PairGeneration, LockKeys.ForScopes(_context.Pair, footprint));
                var observed = await _items.ReobserveAsync(action.Row, new Observer(_context, _parents), token);
                // A live selection applies only its admitted evidence; a later acquisition waits for its own record.
                execution.Revalidate();
                RequireAncestorsClear(observed);
                if (!PlannedReplacementJustified(observed, action.Row, Side.Dropbox)) throw new ChangedUnderneathException(Row.Path(observed));
                return await BuildAndFreezeAsync(observed, Side.Dropbox, NoRestoredItems, true, token, RemovalRoute.PlannedReplacement, null, null);
            }, ct);
        await RunPreparedFolderRemovalsAsync([id], ct);
        return await FinishFolderRemovalAsync(id, selection, ct);
    }

    internal Task<FolderRemovalResult?> RemovePlannedReplacementAsync(Row folder, CancellationToken ct) =>
        RemoveFolderCoreAsync(folder, Side.Dropbox, ct, RemovalRoute.PlannedReplacement, null, null);

    internal Task<FolderRemovalResult?> RemoveSelectedReplacementAsync(Row folder, CancellationToken ct) =>
        RemoveFolderCoreAsync(folder, Side.Dropbox, ct, RemovalRoute.SelectedReplacement, null, null);

    private async Task<FolderRemovalResult?> RemoveFolderCoreAsync(Row folder, Side deletingFrom,
        CancellationToken ct, RemovalRoute route, Exclusion? exclusion, FolderPlan? captured)
    {
        var id = await PrepareFolderRemovalCoreAsync(folder, deletingFrom, NoRestoredItems, NoRestoredItems, ct, null, route, exclusion, captured);
        if (id is null) return null;
        await RunPreparedFolderRemovalsAsync([id], ct);
        return await FinishFolderRemovalAsync(id, null, ct);
    }

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
                    observed = await _items.ReobserveAsync(folder, new Observer(_context, _parents), token);
                    // A live selection applies only its admitted evidence; a later acquisition waits for its own record.
                    execution.Revalidate();
                    if (!FolderDeletes.PlanJustified(observed.EntryOn(deletingFrom), observed.EntryOn(PathNames.Other(deletingFrom)),
                        false))
                    {
                        fallback = await _items.RunAdmittedAsync(observed, token, execution: execution,
                            selection: selection, retainUnfinished: retainUnfinished);
                        return (string?)null;
                    }
                    ItemSync.RequireLiveWrite(_context, deletingFrom == Side.Local);
                    RequireAncestorsClear(observed);
                    return await BuildAndFreezeAsync(observed, deletingFrom, NoRestoredItems, true, token, RemovalRoute.Ordinary, null, null);
                }
                catch (Exception error) when (error is not ChangedUnderneathException && ActionExecutor.IsItemError(error))
                {
                    fallback = await _executor.FailAsync(observed, null, error, token,
                        issue => retainUnfinished(observed, issue), selection: selection);
                    return (string?)null;
                }
            }, ct);
        if (id is null) return fallback?.Succeeded() == true;
        await RunPreparedFolderRemovalsAsync([id], ct);
        var failed = false;
        var result = await FinishFolderRemovalAsync(id, selection, ct, (_, outcome) => failed |= !outcome.Succeeded(), retainUnfinished);
        // Preserved survivors settle the selection without claiming that the whole folder was removed.
        var operation = _context.Store.FolderRemoval(_context.Pair.PairId, id);
        return !failed && (result.Status == FolderRemovalStatus.Complete ||
            !operation.Active && operation.Cause is null && operation.RetirementDisposition is not null);
    }
}
