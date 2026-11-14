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

    /// <summary>Freezes one justified operation and releases scratch before returning its durable identifier.</summary>
    internal Task<string?> PrepareFolderRemovalAsync(Row folder, Side deletingFrom,
        IReadOnlySet<string> restored, IReadOnlySet<string> restoredAncestors, CancellationToken ct,
        Action<Row, SyncOutcome>? completed = null) =>
        PrepareOrdinaryAsync(new FolderRemovalRequest(folder, deletingFrom, FolderRemovalRoute.Ordinary)
            { Restored = restored, RestoredAncestors = restoredAncestors, Completed = completed }, ct);

    // The Ordinary route owns its row: an unjustified folder runs its row attempt after the admission ends (D11).
    private async Task<string?> PrepareOrdinaryAsync(FolderRemovalRequest request, CancellationToken ct)
    {
        var (operationId, check, observed) = await PrepareCheckedAsync(request, ct);
        if (check == FolderRemovalCheck.NotJustified)
            await _items.RunRowAttemptAsync(observed, ct, completed: request.Completed);
        return operationId;
    }

    /// <summary>The one checked folder-removal entry: duties D1-D9 under one admission, then the run after it ends
    /// (D10). Every refusal names its check; item errors before the freeze reach the caller (D11).</summary>
    internal async Task<CheckedFolderRemoval> RemoveCheckedAsync(FolderRemovalRequest request, CancellationToken ct)
    {
        var (operationId, check, observed) = await PrepareCheckedAsync(request, ct);
        return operationId is null ? new CheckedFolderRemoval(check, null, observed)
            : new CheckedFolderRemoval(check, await RunAndFinishAsync(operationId, request.Selection, ct), observed);
    }

    // D1: a live selection runs under its own admitted execution; otherwise work, permit and shared path locks over the
    // footprint, or both row paths. The caller holds no lease.
    private async Task<(string? OperationId, FolderRemovalCheck Check, Row Observed)> PrepareCheckedAsync(
        FolderRemovalRequest request, CancellationToken ct)
    {
        if (request.Selection is { } selection && _context.Live)
        {
            var footprint = request.Footprint ?? throw new ArgumentException(null, nameof(request));
            var coverage = (await _ports.Live.CaptureCoverageAsync(selection, footprint, ct)) with { Peer = selection.Peer };
            return await _ports.Live.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
                (execution, token) =>
                {
                    execution.RequireScopes(_context.Pair.PairId, selection.PairGeneration,
                        LockKeys.ForScopes(_context.Pair, footprint));
                    return CheckAsync(request, execution, token);
                }, ct);
        }
        await using var work = await _context.Admission.EnterWorkAsync(false, ct);
        await using var permit = await _context.EnterOperationAsync(ct);
        await using var hold = await PathLocks.Shared.EnterAsync(request.Footprint is { } scopes
            ? LockKeys.ForScopes(_context.Pair, scopes) : RowScopes(request.Folder), ct);
        return await CheckAsync(request, null, ct);
    }

    private PathLockScope[] RowScopes(Row folder)
    {
        var paths = Row.Paths(folder);
        return LockKeys.ForPaths(_context.Pair, [(Side.Local, paths.Local), (Side.Dropbox, paths.Dropbox)])
            .Select(key => new PathLockScope(key, true)).ToArray();
    }

    // D2, D3, D5 and D4 inside the admission, then the shared tail.
    private async Task<(string? OperationId, FolderRemovalCheck Check, Row Observed)> CheckAsync(
        FolderRemovalRequest request, IAdmittedExecution? execution, CancellationToken ct)
    {
        var observed = request.Folder;
        try
        {
            observed = request.CounterpartPath is { } path ? await ObserveAtCounterpartAsync(request, path, ct)
                : await _items.Preparation.ReobserveAsync(request.Folder, new Observer(_context, _parents), ct);
            // A live selection applies only its admitted evidence; a later acquisition waits for its own record.
            execution?.Revalidate();
            if (!AncestorsClear(observed, request.Route))
                return OwnsItemErrors(request)
                    ? await FailOwnAsync(request, observed, new ChangedUnderneathException(Row.Path(observed)), ct)
                    : (null, FolderRemovalCheck.Waiting, observed);
            if (request.ExpectedRootId is { } expected)
            {
                // Repair's D4 arm and D4p own an absent endpoint, its state-only settle (-8 1).
                var root = observed.EntryOn(request.DeletingFrom);
                if (root is null && request.Route != FolderRemovalRoute.Repair)
                    return (null, FolderRemovalCheck.Absent, observed);
                if (root is not null && root.Id != expected) return (null, FolderRemovalCheck.Replaced, observed);
            }
            if (!await JustifiedAsync(request, observed, ct)) return (null, FolderRemovalCheck.NotJustified, observed);
            return await FreezeCheckedTailAsync(request, observed, execution, ct);
        }
        catch (Exception error) when (OwnsItemErrors(request) && ItemFailures.IsItemError(error))
        {
            return await FailOwnAsync(request, observed, error, ct);
        }
    }

    // D2 for vacate (-8 2): the incoming folder at its moved path and the occupant at the destination binding's
    // deleting-side path; nothing reads the binding's obsolete counterpart path.
    private async Task<Row> ObserveAtCounterpartAsync(FolderRemovalRequest request, string path, CancellationToken ct)
    {
        var saved = request.Folder.Synced ?? throw new ArgumentException(null, nameof(request));
        var counterpartSide = PathNames.Other(request.DeletingFrom);
        var counterpart = await _context.For(counterpartSide).ReadOneAsync(path, ct);
        var occupant = await _context.For(request.DeletingFrom).ReadOneAsync(saved.PathOn(request.DeletingFrom), ct);
        return new Row(saved.LocalKey, counterpartSide == Side.Local ? counterpart : occupant,
            counterpartSide == Side.Dropbox ? counterpart : occupant, saved, [], null, request.Folder.Context);
    }

    // Replacement, resolution and exclusion callers retain their own attempt; ordinary Repair owns this row here.
    private static bool OwnsItemErrors(FolderRemovalRequest request) =>
        request is { Route: FolderRemovalRoute.Ordinary, Exclusion: null };

    private async Task<(string? OperationId, FolderRemovalCheck Check, Row Observed)> FailOwnAsync(
        FolderRemovalRequest request, Row observed, Exception error, CancellationToken ct)
    {
        var failed = await _executor.Failures.FailAsync(observed, null, error, ct);
        request.Completed?.Invoke(observed, failed);
        return (null, FolderRemovalCheck.Declined, observed);
    }

    // D3: pending ancestor work. Vacate and Repair also read the fence's own stale refusal as waiting; every other route
    // keeps that exception, which survivor continuation turns into its dependency-cycle report.
    private bool AncestorsClear(Row observed, FolderRemovalRoute route)
    {
        try { return _ancestors.AncestorsClear(observed, null, null); }
        catch (LiveAdmissionStaleException error) when (error.AncestorBlocker is not null &&
            route is FolderRemovalRoute.Vacate or FolderRemovalRoute.Repair)
        {
            return false;
        }
    }

    // The folder-plan ancestor refusal has one home; each caller's custody and plan-failure policy stay outside.
    private void RequireAncestorsClear(Row observed)
    {
        if (!_ancestors.AncestorsClear(observed, null, null))
            throw new ChangedUnderneathException(Row.Path(observed));
    }

    // D4: the route's counterpart justification, plus the B-213 identity check for automatic plans.
    private async Task<bool> JustifiedAsync(FolderRemovalRequest request, Row observed, CancellationToken ct)
    {
        var (folder, deletingFrom) = (request.Folder, request.DeletingFrom);
        var justified = request.Route switch
        {
            FolderRemovalRoute.SelectedReplacement => observed.Local == folder.Local && observed.Dropbox == folder.Dropbox,
            FolderRemovalRoute.PlannedReplacement => PlannedReplacementJustified(observed, folder, deletingFrom),
            FolderRemovalRoute.Vacate => observed.EntryOn(deletingFrom) == folder.EntryOn(deletingFrom),
            FolderRemovalRoute.Repair => RepairJustified(observed, folder, deletingFrom),
            _ => FolderDeletes.PlanJustified(observed.EntryOn(deletingFrom), observed.EntryOn(PathNames.Other(deletingFrom)),
                request.Exclusion is not null || request.Route == FolderRemovalRoute.Chosen ||
                request.RestoredAncestors?.Contains(folder.Key) == true)
        };
        return justified && !(AuthorityOf(request.Route, request.Exclusion) is FolderRemovalAuthority.Inferred
            or FolderRemovalAuthority.Vacate && await FolderIdentity.KeptElsewhereAsync(_context, observed.Synced,
                deletingFrom, ct));
    }

    // D4's Repair arm (-7 1.2): the reobserved binding is the request's, X is absent or itself at its shared-side path
    // (P8), and the endpoint is absent or a plain folder (P4; its id is D5's).
    private static bool RepairJustified(Row observed, Row folder, Side deletingFrom)
    {
        var shared = PathNames.Other(deletingFrom);
        return observed.Synced is { } saved && saved == folder.Synced &&
            (observed.EntryOn(shared) is not { } present || present.Id == saved.IdOn(shared)) &&
            observed.EntryOn(deletingFrom) is null or { IsFolder: true, Skip: null };
    }

    // The route decides the authority once (-6 2.3); an exclusion is the user's scope choice on any route.
    private static FolderRemovalAuthority AuthorityOf(FolderRemovalRoute route, Exclusion? exclusion) =>
        exclusion is not null ? FolderRemovalAuthority.Excluded : route switch
        {
            FolderRemovalRoute.Ordinary or FolderRemovalRoute.PlannedReplacement => FolderRemovalAuthority.Inferred,
            FolderRemovalRoute.SelectedReplacement or FolderRemovalRoute.Chosen => FolderRemovalAuthority.Chosen,
            FolderRemovalRoute.Vacate => FolderRemovalAuthority.Vacate,
            FolderRemovalRoute.Repair => FolderRemovalAuthority.Repair,
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };

    // D4p and D6-D9, shared by the entry and the retained live plan, which owns D1-D4 itself.
    private async Task<(string? OperationId, FolderRemovalCheck Check, Row Observed)> FreezeCheckedTailAsync(
        FolderRemovalRequest request, Row observed, IAdmittedExecution? execution, CancellationToken ct)
    {
        var deletingFrom = request.DeletingFrom;
        var authority = AuthorityOf(request.Route, request.Exclusion);
        if (request.Preconditions is { } preconditions && !await preconditions(observed, execution, ct))
            return (null, FolderRemovalCheck.Declined, observed);
        if (observed.EntryOn(deletingFrom) is null) return (null, FolderRemovalCheck.NotJustified, observed);
        FolderRemovalState? superseded = null;
        if (UnfinishedAt(observed, deletingFrom, ct) is { } unfinished)
        {
            var found = request.ExpectedRootId is null ? null : _context.Store.FolderRemoval(_context.Pair.PairId, unfinished);
            // An expected root continues only its own operation; another identity's is never run here (D6).
            if (found is not null && found.RootEntry.Id != request.ExpectedRootId)
                return (null, FolderRemovalCheck.ForeignOperation, observed);
            if (found is not null && Supersedes(found, observed)) superseded = found;
            else
            {
                request.Captured?.Dispose();
                return (unfinished, FolderRemovalCheck.Passed, observed);
            }
        }
        // D7: a removal the user did not choose keeps a shared-folder mount; an inferred one says so and settles the
        // branch with the existing notice action (D-1113-8). Vacate and Repair only refuse.
        if (FolderDeletes.KeepsMount(observed.EntryOn(deletingFrom), authority))
        {
            if (authority == FolderRemovalAuthority.Inferred)
            {
                var kept = await _executor.ExecuteAsync(FolderDeletes.SharedFolderKept(observed), null, ct,
                    selection: request.Selection);
                request.Completed?.Invoke(observed, kept);
            }
            return (null, FolderRemovalCheck.Mount, observed);
        }
        request.Captured?.PrepareRemoval(observed);
        var builder = new FolderPlanBuilder(_context);
        var restored = request.Restored ?? NoRestoredItems;
        var plan = request.Exclusion is { } exclusion
            ? await builder.BuildExcludedAsync(request.Captured ?? throw new InvalidOperationException(), exclusion, ct)
            : request.Route == FolderRemovalRoute.SelectedReplacement
                ? await builder.BuildSelectedReplacementAsync(observed, restored, ct)
                : await builder.BuildAsync(observed, deletingFrom, restored, request.Live, request.Temporary, authority, ct);
        if (request.Accepts is { } accepts && !accepts(plan, ct))
        {
            plan.Dispose();
            return (null, FolderRemovalCheck.NotAccepted, observed);
        }
        // Only Vacate links its rename work, the same fact as the Vacate backfill.
        var operationId = FreezePlan(plan, authority,
            request.Route == FolderRemovalRoute.Vacate ? request.Selection : null, superseded, ct, execution);
        RememberUnfinished(deletingFrom, observed, operationId);
        return (operationId, FolderRemovalCheck.Passed, observed);
    }

    // D6's one exception: an unsent inferred operation that recorded its counterpart absent where this request observed it
    // present is superseded by this request's plan once that plan is accepted (D9).
    private bool Supersedes(FolderRemovalState found, Row observed)
    {
        var other = PathNames.Other(found.DeletingSide);
        return found is { Authority: FolderRemovalAuthority.Inferred } &&
            observed.EntryOn(other) is { } present &&
            PathNames.KeyFor(other, present.Path) == PathNames.KeyFor(other, found.CounterpartPath) &&
            FolderRetirement.For(Recovery.Members(_context.Store, _context.Pair.PairId, found.OperationId,
                _context.Settings.StoragePageSize)) == FolderRetirementDisposition.NoEffect;
    }

    // D10: the one continuation, after the admission has ended.
    private async Task<FolderRemovalResult> RunAndFinishAsync(string operationId, WorkSelection? selection,
        CancellationToken ct)
    {
        await RunPreparedFolderRemovalsAsync([operationId], ct);
        return await FinishFolderRemovalAsync(operationId, selection, ct);
    }

    // One root keeps one operation: every route that would plan this root continues the unfinished one instead, so
    // repeated Repairs and resolutions add none (3.8). The one exception is D6's superseded operation (Supersedes):
    // once the request's own plan is accepted it retires with no effect and no notice, and the root takes the new one.
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

    private string FreezePlan(FolderPlan plan, FolderRemovalAuthority authority, WorkSelection? renameWork,
        FolderRemovalState? superseded, CancellationToken ct, IAdmittedExecution? execution)
    {
        string operationId;
        try
        {
            // Retire before begin, so the root never holds two active operations; the store re-checks the generation
            // and every member (fail closed).
            if (superseded is not null)
            {
                _context.Store.RetireFolderRemoval(_context.Pair.PairId, superseded.OperationId, superseded.Generation,
                    FolderRetirementDisposition.NoEffect, StateChange.Empty);
                ForgetUnfinished(superseded.DeletingSide, superseded.RootEntry.Path);
            }
            var pageSize = _context.Settings.StoragePageSize;
            operationId = renameWork is null
                ? _context.Store.BeginFolderRemoval(_context.Pair.PairId, plan, authority, pageSize, ct, Admission(execution))
                : _context.Store.BeginFolderRemoval(_context.Pair.PairId, plan, authority, pageSize, ct, renameWork,
                    Admission(execution));
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

    // A frozen plan becomes recovery work, so its admitted evidence and the live authority are read after its
    // asynchronous capture by the store, inside the publishing critical section: no acquisition commits between this
    // final validation and the publication (R6-4, B-153).
    private static Action Admission(IAdmittedExecution? execution) => () => execution?.Revalidate();

    /// <summary>Reaches released survivors on an ordinary work opportunity without replaying unresolved effects. True
    /// when one of them completed its operation, which may be the blocker a waiting duplicate-folder repair needs.</summary>
    internal async Task<bool> ContinueReleasedFollowUpsAsync(CancellationToken ct,
        Func<Side, string, string?, bool>? temporary = null)
    {
        var completed = false;
        foreach (var operation in _context.Store.ActiveFolderRemovals(_context.Pair.PairId, ct))
        {
            // An operation or survivor whose own selection ended temporarily this run waits for the next run (§6.3).
            if (temporary is not null && TemporaryThisRun(operation.SavedRoot, null, temporary)) continue;
            if (!PendingFollowUps(operation.OperationId, ct).Any(FollowUpReleased)) continue;
            if (_folderDelete.FollowUpsReady(operation, ct) &&
                (await FinishFolderRemovalAsync(operation.OperationId, null, ct, temporary: temporary)).Status ==
                FolderRemovalStatus.Complete)
                completed = true;
        }
        return completed;
    }

    // Exact keys and identities on both sides: the saved binding, and the expected entry's own identity at its path
    // on the deleting side (a replacement there is another identity than the saved one).
    internal static bool TemporaryThisRun(SyncedEntry? saved, (Side Side, Entry Entry)? expected,
        Func<Side, string, string?, bool> temporary)
    {
        if (saved is not null && (temporary(Side.Local, saved.LocalKey, saved.LocalId) ||
            temporary(Side.Dropbox, saved.DropboxKey, saved.DropboxId))) return true;
        return expected is { } item &&
            temporary(item.Side, PathNames.KeyFor(item.Side, item.Entry.Path), item.Entry.Id);
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
        Action<Row, SyncOutcome>? completed = null, Action<Row, Issue>? retainUnfinished = null,
        Func<Side, string, string?, bool>? temporary = null)
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
                if (FollowUpReleased(item) && (temporary is null ||
                    !TemporaryThisRun(item.Saved, (operation.DeletingSide, item.Expected!), temporary)))
                    await FinishSurvivorAsync(operation, item, selection, cycles, ct, completed, retainUnfinished, temporary);
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
        Action<Row, SyncOutcome>? completed, Action<Row, Issue>? retainUnfinished,
        Func<Side, string, string?, bool>? temporary)
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
                outcome = await FinishDeferredAsync(deferred, selection, ct, completed, retainUnfinished, temporary);
        }
        catch (LiveAdmissionStaleException error) when (ItemSync.LiveWriteRefused(error) ||
            error.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged)
        {
            // A Local-write refusal carries the owed-Repair explanation; a survivor has no durable work row of its
            // own, so it names only its path and never invents a work id.
            var held = ItemSync.LiveWriteRefused(error);
            _context.Events(held
                ? RunEvent.Failed(error, TextKey.LocalWriteHeld, path: item.Expected!.Path)
                : RunEvent.Failed(error, path: item.Expected!.Path));
            if (!held) _ancestors.ReportContainedRefusal();
            RememberCycle(item, selection, error, cycles);
            return;
        }
        if (outcome.Succeeded())
            _context.Store.CompleteFolderFollowUp(_context.Pair.PairId, operation.OperationId, item.Ordinal, item.Generation);
    }

    private async Task<SyncOutcome> FinishDeferredAsync(Row row, WorkSelection? selection, CancellationToken ct,
        Action<Row, SyncOutcome>? completed, Action<Row, Issue>? retainUnfinished,
        Func<Side, string, string?, bool>? temporary)
    {
        var outcome = new SyncOutcome(ActionKind.DeferFolder, null, null, null, true);
        var failed = false;
        void Completed(Row effective, SyncOutcome result)
        {
            outcome = result;
            failed |= !result.Succeeded();
            completed?.Invoke(effective, result);
        }
        // The nested plan classifies under the same run exclusion, so an excluded child is kept unread (§6.3).
        var id = await PrepareOrdinaryAsync(new FolderRemovalRequest(row, FolderDeletes.DeletingSide(row),
            FolderRemovalRoute.Ordinary) { Completed = Completed, Live = _context.Live, Temporary = temporary }, ct);
        if (id is null) return outcome;
        await RunPreparedFolderRemovalsAsync([id], ct);
        // A nested survivor is reached from the same continuation, so it keeps the run's exclusion (§6.3).
        var result = await FinishFolderRemovalAsync(id, selection, ct, Completed, retainUnfinished, temporary);
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
        CancellationToken ct) => RemoveFolderCoreAsync(new FolderRemovalRequest(folder, exclusion.Side,
            FolderRemovalRoute.Ordinary) { Exclusion = exclusion, Captured = captured }, ct);

    // The user's choice is the authority on either side (B-216 2.6 step 3).
    internal Task<FolderRemovalResult?> RemoveChosenAsync(SyncAction action, IReadOnlyList<SyncAction> actions,
        CancellationToken ct)
    {
        var side = action.WrittenSide();
        if (side == Side.Local)
            return RemoveFolderCoreAsync(new FolderRemovalRequest(action.Row, side, FolderRemovalRoute.Chosen), ct);
        // The other selected action's observed folder supplies the preserved counterpart for replacement; a
        // delete-only plan's own two-sided row carries it.
        var incoming = actions.Where(other => !ReferenceEquals(other, action))
            .Select(other => other.Row.EntryOn(Side.Local))
            .FirstOrDefault(entry => entry is { IsFolder: true }) ?? action.Row.EntryOn(Side.Local);
        return RemoveSelectedReplacementAsync(action.Row with { Local = incoming }, ct);
    }

    /// <summary>The root's one unresolved operation (-7 5.2): none, inactive custody reported as it stands, or the
    /// active operation continued.</summary>
    internal async Task<FolderRemovalResult?> ContinueUnfinishedAsync(Side side, string rootPath, CancellationToken ct)
    {
        var key = PathNames.KeyFor(side, rootPath);
        var found = _context.Store.UnresolvedFolderRemovals(_context.Pair.PairId, ct).FirstOrDefault(operation =>
            operation.DeletingSide == side && PathNames.Comparer.Equals(PathNames.KeyFor(side, operation.RootEntry.Path), key));
        if (found is null) return null;
        if (!found.Active)
            return new FolderRemovalResult(found.OperationId, found.RootEntry.Path, FolderDeleteRunner.Status(found.Phase));
        return await ContinueOwnAsync(found, null, ct);
    }

    // An earlier opportunity's operation, as its caller just read it, continued without a new check: a Complete one
    // reports, a Prepared one first sends its Ready effects, then the shared finish.
    internal async Task<FolderRemovalResult> ContinueOwnAsync(FolderRemovalState operation, WorkSelection? selection,
        CancellationToken ct)
    {
        var id = operation.OperationId;
        if (operation.Phase == FolderRemovalPhase.Complete)
            return new FolderRemovalResult(id, operation.RootEntry.Path, FolderRemovalStatus.Complete);
        if (operation.Active && operation.Phase == FolderRemovalPhase.Prepared)
            await RunPreparedFolderRemovalsAsync([id], ct);
        return await FinishFolderRemovalAsync(id, selection, ct);
    }

    private bool PlannedReplacementJustified(Row observed, Row folder, Side deletingFrom) =>
        deletingFrom == Side.Dropbox && observed.Local == folder.Local && observed.Dropbox == folder.Dropbox &&
        observed.Synced == folder.Synced && ItemReplacement.RequiresFolderReplacement(Decide.For(observed,
            _context.Pair.FirstRun, _context.Time.GetUtcNow(), _items.Preparation.LocalContentDirty(observed) == true));

    internal async Task<FolderRemovalResult?> RemoveLiveReplacementAsync(SyncAction action, WorkSelection selection, CancellationToken ct)
    {
        var removal = await RemoveCheckedAsync(new FolderRemovalRequest(action.Row, Side.Dropbox,
            FolderRemovalRoute.PlannedReplacement)
        {
            Selection = selection, Footprint = LockKeys.ForLiveAction(action, _context.Pair, null), Live = true
        }, ct);
        return removal.Check is FolderRemovalCheck.NotJustified or FolderRemovalCheck.Waiting
            ? throw new ChangedUnderneathException(Row.Path(removal.Observed)) : removal.Removal;
    }

    internal async Task<FolderRemovalResult?> RemoveVacatedAsync(Row destination, RenameCandidate incoming,
        WorkSelection selection, CancellationToken ct)
    {
        var deletingFrom = PathNames.Other(incoming.Side);
        if (_context.Store.RenameRemoval(selection) is { } existing)
        {
            var operation = _context.Store.FolderRemoval(_context.Pair.PairId, existing);
            return operation.SavedRoot != destination.Synced ? null : await ContinueOwnAsync(operation, selection, ct);
        }
        // No captured occupant: no root to remove and no identity to gate (-8 1).
        if (destination.EntryOn(deletingFrom) is not { } occupant) return null;
        var paths = Row.Paths(destination);
        var footprint = LockKeys.ForLivePaths(_context.Pair,
            [(Side.Local, paths.Local), (Side.Dropbox, paths.Dropbox)], true)
            .Concat(LockKeys.ForRenamePaths(_context.Pair, [(incoming.Side, incoming.Moved.Path)], true)).ToArray();
        var removal = await RemoveCheckedAsync(new FolderRemovalRequest(destination, deletingFrom, FolderRemovalRoute.Vacate)
        {
            ExpectedRootId = occupant.Id, CounterpartPath = incoming.Moved.Path, Selection = selection,
            Footprint = footprint, Live = true, Accepts = static (plan, _) => plan.DeleteWhole,
            Preconditions = (observed, _, _) => Task.FromResult(VacatePreconditions(observed, incoming, selection))
        }, ct);
        return removal.Removal;
    }

    // Vacate's own checks under the entry's admission (-7 1.5, -8 2): the counterpart read is the incoming folder,
    // both bindings are unchanged, the destination binding is the rename's one blocker, the root is in scope and not
    // skipped, and the captured clash still stands (or no issue holds the row).
    private bool VacatePreconditions(Row observed, RenameCandidate incoming, WorkSelection selection)
    {
        var saved = observed.Synced!;
        var deletingFrom = PathNames.Other(incoming.Side);
        if (observed.EntryOn(incoming.Side) != incoming.Moved ||
            _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, incoming.Synced.LocalKey) != incoming.Synced ||
            _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, saved.LocalKey) != saved) return false;
        var after = SyncedEntries.RenameTarget(incoming, _parents.Destination(deletingFrom, incoming.Moved.Path));
        var blockers = _context.Store.RenameDestinationOwners(_context.Pair.PairId, incoming.Synced, after);
        if (blockers.Count != 1 || blockers[0].Binding != saved) return false;
        if (FolderDeletes.OutOfScope(_context.Scope, saved) || observed.EntryOn(deletingFrom)?.Skip is not null)
            return false;
        var clash = _context.Store.ReadRenameContinuation(selection)?.Clash;
        var currentClash = clash is null ? null : _context.Store.IssueById(_context.Pair.PairId, clash.Id);
        if (clash is not null && (currentClash is null || !currentClash.Matches(clash) || currentClash.Ignored ||
            currentClash.Dismissed)) return false;
        return clash is null ? _context.Store.HoldingIssue(_context.Pair.PairId, observed) is null
            : !_context.Store.HeldByOtherIssue(_context.Pair.PairId, observed, clash.Id);
    }

    internal Task<FolderRemovalResult?> RemovePlannedReplacementAsync(Row folder, CancellationToken ct) =>
        RemoveFolderCoreAsync(new FolderRemovalRequest(folder, Side.Dropbox, FolderRemovalRoute.PlannedReplacement), ct);

    internal Task<FolderRemovalResult?> RemoveSelectedReplacementAsync(Row folder, CancellationToken ct) =>
        RemoveFolderCoreAsync(new FolderRemovalRequest(folder, Side.Dropbox, FolderRemovalRoute.SelectedReplacement), ct);

    // The Ordinary fallback keeps its own row attempt; every other route maps pending ancestors to today's exception.
    private async Task<FolderRemovalResult?> RemoveFolderCoreAsync(FolderRemovalRequest request, CancellationToken ct)
    {
        if (OwnsItemErrors(request))
            return await PrepareOrdinaryAsync(request, ct) is { } id ? await RunAndFinishAsync(id, null, ct) : null;
        var removal = await RemoveCheckedAsync(request, ct);
        return removal.Check == FolderRemovalCheck.Waiting
            ? throw new ChangedUnderneathException(Row.Path(removal.Observed)) : removal.Removal;
    }
    // One live plan: admission, reobserve and the freeze decision run in one admitted execution and return a durable operation id; removal and finish run against that id after the execution ends.
    internal async Task<bool> RunLiveFolderPlanAsync(Row folder, Action<Row, Issue> retainUnfinished,
        WorkSelection selection, LiveCoverage coverage, IReadOnlyList<LiveEffectScope> footprint, CancellationToken ct,
        Action? temporary = null)
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
                    // A kept mount's notice outcome settles the selection as the fallback's would (D7).
                    var request = new FolderRemovalRequest(folder, deletingFrom, FolderRemovalRoute.Ordinary)
                        { Live = true, Completed = (_, outcome) => fallback = outcome };
                    if (!await JustifiedAsync(request, observed, token))
                    {
                        // The plan's descendant permission is not the fallback's: it must pass ordinary evidence (B-153).
                        _context.Store.ValidateLiveCoverage(selection, coverage, LockKeys.Ordinary(footprint));
                        fallback = await _items.Execution.RunAdmittedAsync(observed, token, execution: execution,
                            selection: selection, retainUnfinished: retainUnfinished);
                        return (string?)null;
                    }
                    RequireAncestorsClear(observed);
                    return (await FreezeCheckedTailAsync(request, observed, execution, token)).OperationId;
                }
                catch (Exception error) when (error is not ChangedUnderneathException && ItemFailures.IsItemError(error))
                {
                    fallback = await _executor.Failures.FailAsync(observed, null, error, token,
                        issue => retainUnfinished(observed, issue), selection: selection);
                    return (string?)null;
                }
            }, ct);
        if (id is null)
        {
            if (fallback?.None == NoneReason.Unfinished) temporary?.Invoke();
            return fallback?.Succeeded() == true;
        }
        await RunPreparedFolderRemovalsAsync([id], ct);
        var failed = false;
        var result = await FinishFolderRemovalAsync(id, selection, ct, (_, outcome) =>
        {
            failed |= !outcome.Succeeded();
            if (outcome.None == NoneReason.Unfinished) temporary?.Invoke();
        }, retainUnfinished);
        return !failed && SettlementReady(result);
    }
}
