using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Prepares, observes and executes item decisions through the shared action executor.</summary>
internal sealed class ItemSync
{
    private readonly ExecutorContext _context;
    private readonly Parents _parents;
    private readonly ActionExecutor _executor;
    private readonly KeepBothRunner _keepBoth;
    private readonly AncestorFence _ancestors;
    private readonly ILiveExecutionBoundary _live;
    internal FolderSync Folders { get; }

    internal ItemSync(ExecutorContext context, Parents parents, ActionExecutor executor, KeepBothRunner keepBoth,
        FolderDeleteRunner folderDelete, SyncPorts ports, AncestorFence ancestors)
    {
        _context = context;
        _parents = parents;
        _executor = executor;
        _keepBoth = keepBoth;
        _ancestors = ancestors;
        _live = ports.Live;
        Folders = new FolderSync(context, parents, executor, folderDelete, ports, this, ancestors);
    }

    private RowContexts? _rows;
    private readonly AsyncLocal<RowContexts?> _selectionRows = new();
    internal RowContexts Rows => _selectionRows.Value ?? (_rows ??= RowContexts.ForSelection(_context));
    internal void SelectRows(RowContexts rows) => _selectionRows.Value = rows;

    /// <summary>A prepared live row with the label set its action needs and the phase its action takes.</summary>
    internal readonly record struct LivePreparation(SyncAction Action, KeepBothLabels? Labels, bool TakeReplacementPhase,
        bool Retried = false, bool Unchanged = false);

    /// <summary>One execution's run-local facts; a session runs one scan, or its selections in sequence.</summary>
    internal RowContexts ScanContexts(bool firstRun, IReadOnlySet<long>? retries,
        IReadOnlyDictionary<string, IssueSubject>? proofs, Func<string, bool>? deletedName) =>
        Rows.ForScan(firstRun, retries, proofs, deletedName);

    internal async ValueTask<(SyncAction Action, SyncOutcome? Completed)> PrepareRowAsync(
        Row row, CancellationToken ct,
        Action<Row, SyncOutcome>? completed = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, LocalEvidence.DirtyProof? evidence = null, Guid? listedScanId = null)
    {
        var action = await PrepareAsync(row, ct, evidence);
        var owned = action.Row.LocalHash is null ? null : _context.Evidence.Capture(action.Row);
        var transferred = false;
        try
        {
            if (action.Kind is ActionKind.None or ActionKind.DeferFolder)
            {
                var outcome = await _executor.ExecuteAsync(action, null, ct,
                    attemptId: attemptId, expectedAttempt: expectedAttempt);
                Finish(action, outcome, completed);
                return (action, outcome);
            }
            if (action.Kind == ActionKind.Record &&
                await TryRecordAsync(action, ct, attemptId, expectedAttempt, listedScanId) is { } recorded)
            {
                Finish(action, recorded, completed);
                return (action, recorded);
            }
            transferred = true;
            return (action, null);
        }
        finally
        {
            if (!transferred) _context.Evidence.Release(action.Row, owned);
        }
    }

    // One dispatch of the prepared action: folder replacement, ordinary admission and upload admission each route the same prepared action; the upload reservation is released through staging, and a stage without an outcome falls back to ordinary admission after reobserving the row.
    internal async Task<SyncOutcome> DispatchPreparedAsync(SyncAction prepared, CancellationToken ct, Action? releaseOrdinaryCapacity = null,
        Action<Row, SyncOutcome>? completed = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, WorkSelection? selection = null)
    {
        var owned = prepared.Row.LocalHash is null ? null : _context.Evidence.Capture(prepared.Row);
        try
        {
            if (RequiresFolderReplacement(prepared))
                return await ReplaceDropboxFolderRegisteredAsync(prepared, ct, attemptId, expectedAttempt, completed, selection, null);
            await using var work = await _context.Admission.EnterWorkAsync(prepared.Kind == ActionKind.Upload, ct);
            if (prepared.Kind != ActionKind.Upload)
            {
                await using var permit = await _context.EnterOperationAsync(ct);
                return await RunAdmittedAsync(prepared.Row, ct, preparedAction: prepared,
                    completed: completed, attemptId: attemptId, expectedAttempt: expectedAttempt,
                    reobserve: prepared.Kind == ActionKind.Record);
            }
            var uploaded = await AdmitUploadAsync<SyncOutcome?>(releaseOrdinaryCapacity, async staged =>
            {
                var keys = LockKeys.For(prepared, _context.Pair, null);
                await using (await PathLocks.Shared.EnterAsync(keys, ct))
                {
                    (SyncOutcome? Outcome, bool Reobserve, Entry? ObservedLocal) result;
                    try
                    {
                        result = await ExecuteLockedAsync(prepared, null, keys, keys, ct, staged, null,
                            attemptId: attemptId, expectedAttempt: expectedAttempt);
                    }
                    catch (ChangedUnderneathException error) when (IssuePolicy.CanRetryChanged(error)) { result = (null, true, null); }
                    catch (Exception error) when (ActionExecutor.IsItemError(error))
                    {
                        // A freshness read's item error is this row's issue through the one executor mapping.
                        var failed = await _executor.FailAsync(prepared.Row, prepared, error, ct,
                            attemptId: attemptId, expectedAttempt: expectedAttempt);
                        Finish(prepared, failed, completed);
                        return failed;
                    }
                    if (result.Outcome is { } outcome)
                    {
                        Finish(prepared, outcome, completed);
                        return outcome;
                    }
                }
                return null;
            }, ct);
            if (uploaded is not null) return uploaded;
            _context.Admission.DisallowDirectUpload();
            await using var ordinary = await _context.EnterOperationAsync(ct);
            return await RunAdmittedAsync(prepared.Row, ct, completed: completed,
                attemptId: attemptId, expectedAttempt: expectedAttempt, reobserve: true);
        }
        finally { _context.Evidence.Release(prepared.Row, owned); }
    }

    internal async Task<T> AdmitUploadAsync<T>(Action? release, Func<Func<ValueTask>, Task<T>> run,
        CancellationToken ct)
    {
        release?.Invoke();
        IAsyncDisposable? upload = await _context.Admission.AdmitUploadAsync(_context.UploadAdmissionIdentity, ct);
        try
        {
            async ValueTask Staged()
            {
                var owned = upload;
                upload = null;
                if (owned is null) return;
                _context.Admission.UploadStaged();
                await owned.DisposeAsync();
            }
            return await run(Staged);
        }
        finally
        {
            if (upload is not null) await upload.DisposeAsync();
        }
    }

    internal static bool RequiresFolderReplacement(SyncAction action) =>
        action.Kind == ActionKind.ReplaceKind && action.Side == Side.Dropbox && action.Row.Dropbox is { IsFolder: true };

    /// <summary>Prepares a live row and returns the label set its action needs, so no caller chooses labels.
    /// The replacement phase is the owner's answer too; the caller only sequences live admission.</summary>
    internal async ValueTask<LivePreparation> PrepareLiveAsync(Row row, CancellationToken ct, LocalEvidence.DirtyProof? evidence = null,
        LivePreparation? previous = null)
    {
        var retried = previous?.Retried == true;
        var unchanged = previous?.Unchanged == true;
        var reobserve = false;
        var owned = row.LocalHash is null ? null : _context.Evidence.Capture(row);
        while (true)
        {
            try
            {
                if (reobserve)
                {
                    var fresh = await ReobserveAsync(row, new Observer(_context, _parents), ct);
                    unchanged = IssuePolicy.SameStaleEvidence(row, fresh,
                        previousHash: _context.Evidence.CurrentHash(row), currentHash: _context.Evidence.CurrentHash(fresh));
                    _context.Evidence.Release(row, owned);
                    row = fresh;
                    owned = null;
                    evidence = null;
                }
                var action = await PrepareAsync(row, ct, evidence);
                row = action.Row;
                owned = row.LocalHash is null ? null : _context.Evidence.Capture(row);
                var labels = action.Kind == ActionKind.KeepBoth ? await _keepBoth.ChooseLabelsAsync(action.Row, ct) : null;
                return new(action, labels, RequiresFolderReplacement(action), retried, unchanged);
            }
            catch (ChangedUnderneathException error) when (IssuePolicy.CanRetryChanged(error))
            {
                _context.Evidence.Release(row, owned);
                if (IssuePolicy.StaleRetryWaits(retried, liveSelection: true)) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
                _context.Events(RunEvent.RetryChanged(Row.Path(row)));
                retried = reobserve = true;
            }
            catch
            {
                _context.Evidence.Release(row, owned);
                throw;
            }
        }
    }

    /// <summary>Runs one live folder-replacement phase, registering its attempt exactly as the live item path does.</summary>
    internal Task<SyncOutcome> RunLiveReplacementAsync(LivePreparation prepared, WorkSelection selection, IssueSubject subject,
        ItemRetryCandidate? expected, Action<Row, Issue> retainUnfinished, CancellationToken ct) =>
        _context.ItemAttempts.ExecuteAsync(selection, expected, subject, async (attemptId, token) =>
        {
            try
            {
                return await ReplaceDropboxFolderRegisteredAsync(prepared.Action, token, attemptId, expected, null,
                    selection, retainUnfinished, prepared);
            }
            catch (ChangedUnderneathException)
            {
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
            }
            catch (Exception error) when (ActionExecutor.IsItemError(error))
            {
                return await _executor.FailAsync(prepared.Action.Row, prepared.Action, error, token,
                    issue => retainUnfinished(prepared.Action.Row, issue), attemptId: attemptId, expectedAttempt: expected, selection: selection);
            }
        }, ct);

    private async Task<SyncOutcome> ReplaceDropboxFolderRegisteredAsync(SyncAction action, CancellationToken ct,
        Guid? attemptId, ItemRetryCandidate? expectedAttempt, Action<Row, SyncOutcome>? completed,
        WorkSelection? selection, Action<Row, Issue>? retainUnfinished, LivePreparation? preparation = null)
    {
        var row = action.Row;
        if (OutsideBatchScope(action, null)) throw new ChangedUnderneathException(Row.Path(row));
        var result = selection is null ? await Folders.RemovePlannedReplacementAsync(row, ct) :
            await Folders.RemoveLiveReplacementAsync(action, selection, ct);
        var (fresh, failed) = await AfterReplacementRemovalAsync(result, row, null, ct, selection, attemptId, expectedAttempt, retainUnfinished);
        if (failed is not null)
        {
            Finish(action, failed, completed);
            return failed;
        }
        if (selection is not null)
        {
            var prepared = await PrepareLiveAsync(fresh, ct, previous: preparation);
            var footprint = LockKeys.ForLiveAction(prepared.Action, _context.Pair, prepared.Labels);
            var coverage = (await _live.CaptureCoverageAsync(selection, footprint, ct)) with { Peer = selection.Peer };
            return await _live.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
                (execution, token) => RunAdmittedAsync(prepared.Action.Row, token, preparation: prepared,
                    execution: execution, selection: selection, retainUnfinished: retainUnfinished, completed: completed,
                    preparedLabels: prepared.Labels, attemptId: attemptId, expectedAttempt: expectedAttempt), ct);
        }
        if (attemptId is { } registered)
        {
            var prepared = await PrepareRowAsync(fresh, ct, completed,
                attemptId: registered, expectedAttempt: expectedAttempt);
            return prepared.Completed ?? await DispatchPreparedAsync(prepared.Action, ct,
                completed: completed, attemptId: registered, expectedAttempt: expectedAttempt);
        }
        return await RunRowAttemptAsync(fresh, ct, completed: completed);
    }

    internal async Task<SyncOutcome?> ReplacementRemovalFailureAsync(
        FolderRemovalResult? result, Row row, SyncAction? action, CancellationToken ct,
        WorkSelection? selection, Guid? attemptId, ItemRetryCandidate? expectedAttempt, Action<Row, Issue>? retainUnfinished)
    {
        if (result?.Status == FolderRemovalStatus.Complete) return null;
        Exception error = result is null || action is null
            ? new ChangedUnderneathException(Row.Path(row))
            : new ItemFailedException(Row.Path(row),
                _context.Store.FolderRemoval(_context.Pair.PairId, result.OperationId).Cause ?? TextKey.FailReplacementRetained, null);
        return await _executor.FailAsync(row, action, error, ct,
            retainUnfinished is null ? null : issue => retainUnfinished(row, issue),
            attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection);
    }

    internal async Task<(Row Fresh, SyncOutcome? Failed)> AfterReplacementRemovalAsync(
        FolderRemovalResult? result, Row row, SyncAction? failureAction, CancellationToken ct,
        WorkSelection? selection, Guid? attemptId, ItemRetryCandidate? expectedAttempt, Action<Row, Issue>? retainUnfinished)
    {
        if (await ReplacementRemovalFailureAsync(result, row, failureAction, ct, selection, attemptId, expectedAttempt, retainUnfinished) is { } failed)
            return (row, failed);
        var fresh = await ReobserveAsync(row, new Observer(_context, _parents), ct);
        if (fresh.Dropbox is not null || fresh.Local != row.Local)
            return (fresh, await _executor.FailAsync(fresh, failureAction,
                new ChangedUnderneathException(Row.Path(fresh)), ct,
                retainUnfinished is null ? null : issue => retainUnfinished(fresh, issue),
                attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection));
        return (fresh, null);
    }

    // The pre-check and the captured attempt Prepare must read the same dirty answer; splitting would let them judge different evidence.
    internal async Task<SyncOutcome> RunRowAttemptAsync(Row row, CancellationToken ct, Action? releaseOrdinaryCapacity = null,
        Action<Row, SyncOutcome>? completed = null,
        PrefixSet? itemRetries = null, WorkSelection? selection = null, Guid? listedScanId = null)
    {
        // One dirty answer per row, shared by the route pre-check and Prepare; rows settled before content
        // (out of scope or skipped) read none, as Prepare never did.
        var settled = Decide.WithoutContent(row);
        var evidence = settled is null ? ReadDirtyIfApplicable(row) : null;
        var initial = settled ?? Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(),
            evidence?.Dirty == true || _context.Evidence.CurrentHash(row, evidence) is not null);
        // A row with an open in-use episode goes through the attempt path even when it looks in sync, so its
        // completion retires the episode instead of leaving the notice held.
        var inUse = itemRetries is not null && IssuePolicy.CoveredKey(row, itemRetries) is not null;
        if (!inUse && initial.Kind is ActionKind.None or ActionKind.DeferFolder && row.LocalHash is null)
        {
            if (initial.Kind == ActionKind.DeferFolder)
            {
                // A deferral is never executed from the listing: the admitted path re-observes it first.
                var deferred = await PrepareAsync(row, ct);
                return await DispatchPreparedAsync(deferred, ct, releaseOrdinaryCapacity,
                    completed, selection: selection);
            }
            var prepared = await PrepareRowAsync(row, ct, completed, evidence: evidence, listedScanId: listedScanId);
            if (prepared.Completed is { } done) return done;
        }
        var (subject, expected) = CaptureAttempt(row, inUse);
        return await _context.ItemAttempts.ExecuteAsync(_context.Opportunity, expected,
            subject, async (attempt, token) =>
            {
                try
                {
                    (SyncAction Action, SyncOutcome? Completed) prepared;
                    try { prepared = await PrepareRowAsync(row, token, completed, attempt, expected, evidence, listedScanId); }
                    catch (ChangedUnderneathException error) when (IssuePolicy.CanRetryChanged(error))
                    {
                        await using var permit = await _context.EnterOperationAsync(token);
                        return await RunAdmittedAsync(row, token, completed: completed,
                            attemptId: attempt, expectedAttempt: expected, reobserve: true);
                    }
                    return prepared.Completed ?? await DispatchPreparedAsync(prepared.Action, token,
                        releaseOrdinaryCapacity, completed, attempt, expected, selection);
                }
                catch (Exception error) when (ActionExecutor.IsItemError(error))
                {
                    var failed = await _executor.FailAsync(row, null, error, token, attemptId: attempt, expectedAttempt: expected);
                    Finish(initial, failed, completed);
                    return failed;
                }
            }, ct);
    }

    // Capture before preparation/admission: a later retry candidate must not replace this row's expected episode.
    internal (IssueSubject Subject, ItemRetryCandidate? Expected) CaptureAttempt(Row row, bool inUse = false) =>
        ActionExecutor.CaptureAttempt(_context.Store, _context.Pair.PairId, _context.ExpectedGeneration,
            row.Local?.Path ?? (inUse ? row.Synced?.LocalPath : null), ActionExecutor.AttemptSubject(row));

    /// <summary>Observes a standalone path without treating it as completed inventory.</summary>
    internal async Task<SyncOutcome> SyncPathAsync(Side side, string sidePath, CancellationToken ct,
        IReadOnlySet<long>? retryIssues = null, Action<Row, SyncOutcome>? completed = null)
    {
        await using var work = await _context.Admission.EnterWorkAsync(false, ct);
        await using var permit = await _context.EnterOperationAsync(ct);
        var previous = _rows;
        if (retryIssues is not null)
            _rows = new RowContexts(_context, !_context.Store.FirstRepairDone(_context.Pair.PairId),
                false, retryIssues, null, null);
        try { return await SyncFollowUpAsync(side, sidePath, ct, completed: completed); }
        finally { if (retryIssues is not null) _rows = previous; }
    }

    /// <summary>Observes one follow-up or survivor and runs it. An observation or context read that fails is that item's
    /// Failure and the caller carries on; cancellation and authority refusals still escape.</summary>
    internal async Task<SyncOutcome> SyncFollowUpAsync(Side side, string path, CancellationToken ct,
        IAdmittedExecution? execution = null,
        Action<Row, Issue>? retainUnfinished = null, Action<Row, SyncOutcome>? completed = null)
    {
        var observer = new Observer(_context, _parents);
        Row row;
        try { row = await ObserveFollowUpAsync(observer, side, path, ct); }
        catch (ItemFailedException error)
        {
            // Keep the requested path for callbacks; the failure names the exception's path.
            var unobserved = new Row(path, null, null, null, [], null, RowContext.Neutral);
            Action<Issue>? committed = retainUnfinished is null ? null : issue => retainUnfinished(unobserved, issue);
            var failed = await _executor.FailAsync(unobserved, null, error, ct, committed);
            completed?.Invoke(unobserved, failed);
            return failed;
        }
        if (row.Context.Skipped) await ObserveSkippedAsync(row, execution, ct);
        return await RunAdmittedAsync(row, ct, observer: observer, execution: execution,
            retainUnfinished: retainUnfinished, completed: completed);
    }

    // A follow-up or survivor belongs to no listing: its facts are the Store's at this moment, on no run's sets.
    private async Task<Row> ObserveFollowUpAsync(Observer observer, Side side, string path, CancellationToken ct)
    {
        Row row;
        try { row = await observer.ObserveAsync(side, path, ct); }
        catch (InventoryIncompleteException error)
        {
            throw ActionExecutor.InventoryFailure(error, path, side);
        }
        return row with { Context = await Rows.ForFollowUpAsync(row, ct) };
    }

    internal async Task<SyncOutcome> ExecuteSelectedAsync(IAdmittedExecution execution, 
        SyncAction action, KeepBothLabels? labels, CancellationToken ct)
    {
        var owned = action.Row.LocalHash is null ? null : _context.Evidence.Capture(action.Row);
        try
        {
            var keys = LockKeys.For(action, _context.Pair, labels);
            LockKeys.Require(execution, _context.Pair, _context.ExpectedGeneration, action, labels, keys);
            var result = await ExecuteLockedAsync(action, labels, keys, keys, ct, null, null, closeIssues: false);
            return result.Outcome ?? throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
        }
        finally { _context.Evidence.Release(action.Row, owned); }
    }

    /// <summary>Reconciles ordinary rename work inside one outer admission scope.</summary>
    internal async Task<RenameDecision> RenameAsync(IReadOnlyList<RenameCandidate> group, Entry? localById, CancellationToken ct,
        bool completedScan = false)
    {
        await using var work = await _context.Admission.EnterWorkAsync(false, ct);
        if (completedScan)
        {
            var listed = await Renamer(true).RunAsync(group, localById, null, ct);
            if (listed.Kind != RenameKind.NeedsFresh) return listed;
            _context.Store.Commit(_context.Pair.PairId, StateChange.Empty);
        }
        await using var permit = await _context.EnterOperationAsync(ct);
        return await Renamer().RunAsync(group, localById, null, ct);
    }

    internal Task<RenameDecision> RenameAdmittedAsync(IAdmittedExecution execution, WorkSelection? selection, object opportunity,
        IReadOnlyList<RenameCandidate> group, Entry? localById, CancellationToken ct,
        Action<Issue>? retainUnfinished = null) =>
        Renamer().RunAdmittedAsync(execution, selection, opportunity, group, localById, retainUnfinished, ct);

    internal async Task<bool> RemoveVacatedAsync(RenameCandidate incoming, SyncedEntry destination,
        WorkSelection selection, CancellationToken ct)
    {
        if (!destination.IsFolder) return await RemoveVacatedFileAsync(incoming, destination, selection, ct);
        var side = PathNames.Other(incoming.Side);
        var occupant = await _context.For(side).ReadOneAsync(destination.PathOn(side), ct);
        var row = Row.ForOneSide(side, destination.LocalKey, occupant, destination, [], null, RowContext.Neutral);
        var result = await Folders.RemoveVacatedAsync(row, incoming, selection, ct);
        return result?.Status == FolderRemovalStatus.Complete;
    }

    internal async Task<bool> RemoveVacatedFileAsync(RenameCandidate incoming, SyncedEntry destination,
        WorkSelection selection, CancellationToken ct, IAdmittedExecution? execution = null)
    {
        var paths = new[] { (Side.Local, destination.LocalPath), (Side.Dropbox, destination.DropboxPath),
            (incoming.Side, incoming.Moved.Path) };
        if (_context.Live && execution is null)
        {
            var footprint = LockKeys.ForLivePaths(_context.Pair, paths, false);
            var coverage = (await _live.CaptureCoverageAsync(selection, footprint, ct)) with { Peer = selection.Peer };
            return await _live.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
                (admitted, token) => RemoveVacatedFileAsync(incoming, destination, selection, token, admitted), ct);
        }
        var side = PathNames.Other(incoming.Side);
        await using var work = execution is null ? await _context.Admission.EnterWorkAsync(false, ct) : null;
        await using var permit = execution is null ? await _context.EnterOperationAsync(ct) : null;
        var keys = LockKeys.ForPaths(_context.Pair, paths).ToArray();
        execution?.Require(_context.Pair.PairId, _context.ExpectedGeneration, keys);
        await using var hold = execution is null ? await PathLocks.Shared.EnterAsync(keys, ct) : null;
        var row = await ObserveVacatedFileAsync(incoming, destination, selection, side, ct);
        if (row is null) return false;
        var occupant = row.EntryOn(side)!;
        var removal = await _executor.SelectRemovalAsync(row, side, occupant, ct);
        execution?.Revalidate();
        RequireLiveWrite(_context, side == Side.Local);
        var change = StateChange.Empty with { ForgetSelf = [(side, destination.KeyOn(side))] };
        ActionExecutor.RequireReadable(_context.Store, _context.Pair.PairId, occupant.Path,
            paths.Select(path => (path.Item1, PathNames.KeyFor(path.Item1, path.Item2))).ToArray(), false, ct);
        var result = await _executor.RemoveAsync(side, occupant, removal, change, ct);
        return result.Outcome == RemovalOutcome.Removed;
    }

    // Binding, alias, scope and content checks jointly prove this exact occupant obsolete; a failed check returns no removal row.
    private async Task<Row?> ObserveVacatedFileAsync(RenameCandidate incoming, SyncedEntry destination,
        WorkSelection selection, Side side, CancellationToken ct)
    {
        if (_context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, incoming.Synced.LocalKey) != incoming.Synced ||
            _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, destination.LocalKey) != destination ||
            await _context.For(incoming.Side).ReadOneAsync(incoming.Moved.Path, ct) != incoming.Moved ||
            _context.For(incoming.Side) is not IIdentitySide identities) return null;
        var after = SyncedEntries.RenameTarget(incoming, _parents.Destination(side, incoming.Moved.Path));
        var blockers = _context.Store.RenameDestinationOwners(_context.Pair.PairId, incoming.Synced, after);
        if (blockers.Count != 1 || blockers[0].Binding != destination) return null;
        if (await identities.ReadByIdAsync(destination.IdOn(incoming.Side), ct) is { InScope: true }) return null;
        var occupant = await _context.For(side).ReadOneAsync(destination.PathOn(side), ct);
        if (occupant is not { IsFolder: false, Skip: null } || occupant.Id != destination.IdOn(side)) return null;
        var row = Row.ForOneSide(side, destination.LocalKey, occupant, destination, [], null, RowContext.Neutral);
        if (_context.Scope.Excludes(destination.LocalPath, destination.DropboxPath, false) ||
            _context.Scope.HoldsNestedRoot(Side.Local, destination.LocalPath) ||
            _context.Scope.HoldsNestedRoot(Side.Dropbox, destination.DropboxPath)) return null;
        var clash = _context.Store.ReadRenameContinuation(selection)?.Clash;
        if (clash is not null)
        {
            var current = _context.Store.IssueById(_context.Pair.PairId, clash.Id);
            if (current is null || current.Ignored || current.Dismissed || !current.Matches(clash)) return null;
        }
        if (clash is null ? _context.Store.HoldingIssue(_context.Pair.PairId, row) is not null :
            _context.Store.HeldByOtherIssue(_context.Pair.PairId, row, clash.Id)) return null;
        var dirty = false;
        if (side == Side.Local)
        {
            var proof = _context.Evidence.Read(occupant, destination);
            dirty = proof.Dirty;
            if (Decide.LocalChanged(occupant, destination, null, dirty) is null)
                row = row with { LocalHash = (await _context.Evidence.HashAsync(_context.Local, occupant, destination, proof, ct)).Hash };
        }
        if (Decide.Changed(side, occupant, destination, row.LocalHash, dirty)) return null;
        return row;
    }

    internal RenameRunner Renamer(bool completedScan = false) => new(_context, _parents, Rows, _ancestors, completedScan);

    /// <summary>Refuses a live effect that writes Local while Local skipped-coverage or lost-interval debt is owed;
    /// the work stays pending and Repair's completion wakes the pair.</summary>
    internal static void RequireLiveWrite(ExecutorContext context, bool writesLocal)
    {
        if (writesLocal && context.Live && context.Store.ReadLiveRefusal(context.Pair.PairId) == LiveRefusal.LocalWrites)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged, nameof(LiveRefusal.LocalWrites));
    }

    /// <summary>Whether a stale answer is this Local-write refusal, which its caller contains to the one effect.</summary>
    internal static bool LiveWriteRefused(LiveAdmissionStaleException error) =>
        error.Detail == nameof(LiveRefusal.LocalWrites);

    // The loop holds the re-observation decision and the prepared action it must re-run in one admission lifetime.
    internal async Task<SyncOutcome> RunAdmittedAsync(Row row, CancellationToken ct, Observer? observer = null, SyncAction? preparedAction = null,
        IAdmittedExecution? execution = null, WorkSelection? selection = null,
        Action<Row, Issue>? retainUnfinished = null,
        Action<Row, SyncOutcome>? completed = null, KeepBothLabels? preparedLabels = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, bool reobserve = false,
        LivePreparation? preparation = null, Func<ValueTask>? uploadStaged = null)
    {
        var action = preparation?.Action ?? preparedAction;
        var unchanged = preparation?.Unchanged == true;
        var retried = reobserve || preparation?.Retried == true;
        var mayAdopt = preparation?.Retried != true;
        var adopted = false;
        var transferred = false;
        var owned = row.LocalHash is null ? null : _context.Evidence.Capture(row);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Action<Issue>? committed = retainUnfinished is null ? null : issue => retainUnfinished(row, issue);
                try
                {
                    if (!adopted && (reobserve || action?.Kind is ActionKind.Record or ActionKind.DeferFolder))
                    {
                        if (reobserve) _context.Events(RunEvent.RetryChanged(Row.Path(row)));
                        // A live selection runs with no work scope of its own, so an unrelated sole scope must not let the
                        // replacement action stage direct; the limiter keeps deciding, this only withdraws it for the turn.
                        _context.Admission.DisallowDirectUpload();
                        observer ??= new Observer(_context, _parents);
                        var fresh = await ReobserveAsync(row, observer, ct);
                        unchanged = reobserve && IssuePolicy.SameStaleEvidence(row, fresh,
                            previousHash: _context.Evidence.CurrentHash(row), currentHash: _context.Evidence.CurrentHash(fresh));
                        _context.Evidence.Release(row, owned);
                        row = fresh;
                        owned = null;
                        // A live selection applies only its admitted evidence; a later acquisition waits for its own record.
                        if (execution is not null && selection is not null) execution.Revalidate();
                        action = null;
                    }
                    action ??= await PrepareAsync(row, ct);
                    row = action.Row;
                    owned = row.LocalHash is null ? null : _context.Evidence.Capture(row);
                    if (RequiresFolderReplacement(action))
                        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
                    if (action.Kind is ActionKind.None or ActionKind.DeferFolder)
                    {
                        var outcome = await _executor.ExecuteAsync(action, null, ct, committedIssue: committed,
                            attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection);
                        Finish(action, outcome, completed);
                        return outcome;
                    }
                    var labels = action.Kind == ActionKind.KeepBoth
                        ? preparedLabels ?? await _keepBoth.ChooseLabelsAsync(row, ct) : null;
                    var keys = LockKeys.For(action, _context.Pair, labels);
                    if (execution is not null)
                    {
                        try { LockKeys.Require(execution, _context.Pair, _context.ExpectedGeneration, action, labels, keys); }
                        catch (LiveAdmissionStaleException error) when (adopted &&
                            error.Reason == LiveAdmissionStaleReason.FootprintChanged)
                        {
                            transferred = true;
                            throw new LiveAdmissionStaleException(error.Reason) { HandoffRow = row };
                        }
                    }
                    Entry? observed = null;
                    await using (var hold = execution is null ? await PathLocks.Shared.EnterAsync(keys, ct) : null)
                    {
                        var result = await ExecuteLockedAsync(action, labels, keys, keys, ct, uploadStaged, committed,
                            selection: selection, attemptId: attemptId, expectedAttempt: expectedAttempt);
                        if (result.Outcome is { } outcome)
                        {
                            Finish(action, outcome, completed);
                            return outcome;
                        }
                        if (!result.Reobserve)
                        {
                            if (execution is not null) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
                            preparedLabels = null;
                            action = null;
                            reobserve = false;
                            continue;
                        }
                        observed = result.ObservedLocal;
                    }
                    if (mayAdopt && observed is not null)
                    {
                        var previousHash = _context.Evidence.CurrentHash(row);
                        observer ??= new Observer(_context, _parents);
                        var fresh = await ReobserveAsync(row, observer, ct);
                        var rebound = _context.Evidence.Rebind(row, fresh, observed);
                        var currentHash = _context.Evidence.CurrentHash(rebound.Row, rebound.Evidence);
                        unchanged = IssuePolicy.SameStaleEvidence(row, rebound.Row,
                            previousHash: previousHash, currentHash: currentHash);
                        _context.Evidence.Release(row, owned);
                        row = rebound.Row;
                        owned = row.LocalHash is null ? null : _context.Evidence.Capture(row);
                        if (execution is not null && selection is not null) execution.Revalidate();
                        if (currentHash is not null && !unchanged)
                        {
                            action = await PrepareAsync(row, ct, rebound.Evidence);
                            row = action.Row;
                            owned = row.LocalHash is null ? null : _context.Evidence.Capture(row);
                            adopted = true;
                            mayAdopt = false;
                            if (!retried) _context.Events(RunEvent.RetryChanged(Row.Path(row)));
                            retried = true;
                            reobserve = false;
                            preparedLabels = null;
                            continue;
                        }
                    }
                    adopted = false;
                }
                catch (ChangedUnderneathException error) when (selection is not null && !IssuePolicy.CanRetryChanged(error))
                {
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
                }
                catch (ChangedUnderneathException error) when (IssuePolicy.CanRetryChanged(error))
                {
                    // A thrown race proves new change even when the preceding observation matched.
                    unchanged = false;
                }
                catch (Exception error) when (ActionExecutor.IsItemError(error))
                {
                    return await FailAndFinishAsync(row, action, error, ct, committed, completed,
                        attemptId, expectedAttempt, selection);
                }
                if (IssuePolicy.StaleRetryFailed(retried, unchanged, selection is not null))
                {
                    return await FailAndFinishAsync(row, action, new ChangedUnderneathException(Row.Path(row)), ct,
                        committed, completed, attemptId, expectedAttempt, selection);
                }
                // A live item awaits its own next acquisition; a batch child fails without holding its siblings.
                if (IssuePolicy.StaleRetryWaits(retried, selection is not null))
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
                retried = reobserve = true;
            }
        }
        finally
        {
            if (!transferred) _context.Evidence.Release(row, owned);
        }
    }

    private async ValueTask<SyncOutcome> FailAndFinishAsync(Row row, SyncAction? action, Exception error,
        CancellationToken ct, Action<Issue>? committed, Action<Row, SyncOutcome>? completed,
        Guid? attemptId, ItemRetryCandidate? expectedAttempt, WorkSelection? selection)
    {
        var failed = await _executor.FailAsync(row, action, error, ct, committed,
            attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection);
        if (action is not null) Finish(action, failed, completed);
        else completed?.Invoke(row, failed);
        return failed;
    }

    // Ordered admission gates return one execution-or-reobserve answer and pass their observed target to the effect without another read.
    internal async ValueTask<(SyncOutcome? Outcome, bool Reobserve, Entry? ObservedLocal)> ExecuteLockedAsync(
        SyncAction action, KeepBothLabels? labels, IReadOnlyCollection<string> requiredKeys,
        IReadOnlyCollection<string> heldKeys, CancellationToken ct,
        Func<ValueTask>? uploadStaged, Action<Issue>? committedIssue, Row? firstRunFolder = null, WorkSelection? selection = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, bool closeIssues = true, Guid? listedScanId = null)
    {
        ArgumentNullException.ThrowIfNull(requiredKeys);
        ArgumentNullException.ThrowIfNull(heldKeys);
        if (!ReferenceEquals(requiredKeys, heldKeys) && requiredKeys.Any(key => !heldKeys.Contains(key, PathLocks.KeyComparer)))
            return (null, true, null);
        if (OutsideBatchScope(action, labels))
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection,
                outsideBatchScope: true);
        var news = await _ancestors.BeforeExecutionAsync(action, _parents, ct);
        if (news == AncestorNews.Changed)
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        (bool Fresh, (Side Side, string Path, Entry? Entry)? ObservedTarget) fresh;
        try { fresh = await IsFreshAsync(action.Row, action, ct, listedScanId); }
        catch (PathOccupiedException) when (news == AncestorNews.Restated)
        {
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        }
        if (news == AncestorNews.Restated && (!fresh.Fresh || fresh.ObservedTarget is not { Entry: null }))
        {
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        }
        if (!fresh.Fresh || !ReferenceEquals(Rows.RefreshInventory(action.Row), action.Row))
        {
            return (null, true, null);
        }
        if (labels is not null && !await _keepBoth.AreUsableAsync(action.Row, labels, ct)) return (null, false, null);
        // Refuse known debt before preparation; effect owners recheck after inputs may change asynchronously.
        RequireLiveWrite(_context, action.WritesLocal());
        try
        {
            return (await _executor.ExecuteAsync(action, labels, ct, closeIssues, uploadStaged,
                observedTarget: fresh.ObservedTarget, committedIssue: committedIssue, firstRunFolder: firstRunFolder,
                attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection, listedScanId: listedScanId), false, null);
        }
        catch (ChangedUnderneathException error) when (IssuePolicy.CanRetryChanged(error))
        {
            return (null, true, error.ObservedLocal);
        }
    }

    private bool OutsideBatchScope(SyncAction action, KeepBothLabels? labels) =>
        _context.BatchScope is { } scope && LockKeys.ForLiveAction(action, _context.Pair, labels)
            .Any(item => (item.Side == Side.Local
                ? PathNames.RelativeUnderLocalRoot(_context.Pair.LocalRoot, item.NativePath)
                : PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(_context.Pair.DropboxRoot), item.NativePath)) is not { } relative ||
                !PathNames.IsSelfOrUnder(relative, scope));

    // A private method, not a local function: a local would capture action and ct in a heap closure on every call.
    private async ValueTask<(SyncOutcome? Outcome, bool Reobserve, Entry? ObservedLocal)> RefusedAsync(SyncAction action, CancellationToken ct,
        Action<Issue>? committedIssue, Row? firstRunFolder, Guid? attemptId, ItemRetryCandidate? expectedAttempt,
        WorkSelection? selection, bool outsideBatchScope = false)
    {
        if (_context.BatchScope is not null && !outsideBatchScope)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
        return (await _executor.FailAsync(action.Row, action, new ChangedUnderneathException(Row.Path(action.Row)),
            ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection), false, null);
    }

    private async ValueTask<SyncOutcome?> TryRecordAsync(SyncAction action, CancellationToken ct,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, Guid? listedScanId = null)
    {
        var keys = LockKeys.For(action, _context.Pair, null);
        await using var hold = await PathLocks.Shared.EnterAsync(keys, ct);
        return (await ExecuteLockedAsync(action, null, keys, keys, ct, null, null, attemptId: attemptId, expectedAttempt: expectedAttempt, listedScanId: listedScanId)).Outcome;
    }

    // Preparation transfers one proof to its returned action; failed preparation must revoke any digest it owned.
    internal async ValueTask<SyncAction> PrepareAsync(Row row, CancellationToken ct, LocalEvidence.DirtyProof? evidence = null)
    {
        var owned = row.LocalHash is null ? null : _context.Evidence.Capture(row);
        try
        {
            ct.ThrowIfCancellationRequested();
            row = Rows.RefreshDeletionEligibility(row);
            if (Decide.WithoutContent(row) is { } settled) return AdmitInBatch(settled);
            if (row.Synced is null && row.Local is { } local && row.Dropbox is null)
                row = row with { Context = row.Context with { Destination = _parents.Destination(Side.Dropbox, local.Path) } };
            else if (row.Synced is null && row.Dropbox is { } remote && row.Local is null)
                row = row with { Context = row.Context with { Destination = _parents.Destination(Side.Local, remote.Path) } };
            if (evidence is null && LocalEvidence.AppliesTo(row)) evidence = ReadDirty(row);
            var currentHash = _context.Evidence.CurrentHash(row, evidence);
            if (currentHash is null && (evidence?.Dirty == true || owned?.Hash is not null))
            {
                _context.Evidence.Release(row, owned);
                owned = null;
                row = row with { LocalHash = null };
            }
            var dirty = evidence?.Dirty == true || currentHash is not null;
            var action = AdmitInBatch(Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(), dirty));
            if (action.Kind == ActionKind.NeedDeletionEvidence)
            {
                row = row with { Deletion = await _context.ReadDeletionEvidenceAsync(action.Target!, row.IsFolder(), ct) };
                if (row.Deletion is DeletionEvidence.LiveAtTarget live)
                    row = IssuePolicy.ReobserveFirstRun(row, row with { Dropbox = live.Location.Entry, Deletion = null });
                else if (row.Deletion is DeletionEvidence.LiveElsewhere)
                    throw new DeletionEvidenceRefusedException(action.Target!);
                action = Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(), dirty);
            }
            // A held row settles without a hash; a validated current digest needs no second read.
            if (action.Kind == ActionKind.NeedLocalHash ||
                evidence?.Dirty == true && currentHash is null && action.None != NoneReason.Held)
            {
                evidence ??= ReadDirty(row);
                var hashed = await _context.Evidence.HashAsync(_context.Local, row.Local!, row.Synced,
                    (evidence.Dirty, evidence.Boundary, evidence.Version), ct);
                row = row with { LocalHash = hashed.Hash };
                evidence = new LocalEvidence.DirtyProof(hashed.Proof.Dirty, hashed.Proof.Boundary, hashed.Proof.Version);
                owned = _context.Evidence.Remember(row, evidence, hashed.Hash);
                action = Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(),
                    evidence.Dirty || _context.Evidence.CurrentHash(row, evidence) is not null);
            }
            else if (currentHash is null && evidence is not null && action.WritesLocal() && LocalEvidence.AppliesTo(row))
                owned = _context.Evidence.Remember(row, evidence);
            return AdmitInBatch(action);
        }
        catch
        {
            _context.Evidence.Release(row, owned);
            throw;
        }
    }

    internal SyncAction AdmitInBatch(SyncAction action) => _context.BatchScope is null ? action :
        Decide.AdmitInBatch(action, action.Row.Synced is null && BoundById(action.Row),
            _context.BatchUnlisted, _context.BatchSettlesFirstRun);

    private bool BoundById(Row row) =>
        row.Local is { } local && _context.Store.SyncedById(_context.Pair.PairId, Side.Local, local.Id) is not null ||
        row.Dropbox is { } remote && _context.Store.SyncedById(_context.Pair.PairId, Side.Dropbox, remote.Id) is not null ||
        row.Extra.Any(entry => _context.Store.SyncedById(_context.Pair.PairId, Side.Dropbox, entry.Id) is not null);

    // One freshness decision: the currently read sides, their bindings and the row's dirty proof are compared with the prepared row, and the verdict returns the target it observed so no caller re-derives freshness.
    private async ValueTask<(bool Fresh, (Side Side, string Path, Entry? Entry)? ObservedTarget)> IsFreshAsync(
        Row row, SyncAction action, CancellationToken ct, Guid? listedScanId = null)
    {
        if (row.Context.DeletionEligible && !Rows.RefreshDeletionEligibility(row).Context.DeletionEligible)
            return (false, null);
        var paths = Row.Paths(row, action.Target);
        var localKey = PathNames.KeyFor(Side.Local, paths.Local);
        var dropboxKey = PathNames.KeyFor(Side.Dropbox, paths.Dropbox);
        var storeOnly = action.Kind == ActionKind.Record;
        if (storeOnly && listedScanId is { } scan && !_context.Store.CanUseListedEvidence(_context.Pair.PairId,
            scan, localKey, dropboxKey, false, row.Synced)) return (false, null);
        var local = storeOnly ? row.Local : await _context.Local.ReadOneAsync(paths.Local, ct);
        var dropbox = storeOnly ? row.Dropbox : await _context.Dropbox.ReadOneAsync(paths.Dropbox, ct);
        var localBinding = _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, localKey);
        var dropboxBinding = _context.Store.SyncedByKey(_context.Pair.PairId, Side.Dropbox, dropboxKey);
        // A binding of this new row's identity committed since preparation is a move-in: reobserve, the seam leaves it.
        if (_context.BatchScope is not null && row.Synced is null && BoundById(row)) return (false, null);
        if (_context.Evidence.Capture(row) is { Hash: not null } proof &&
            (!LocalEvidence.SameFile((proof.Dirty, proof.Boundary, proof.Version),
                _context.Evidence.Read(row.Local!, row.Synced)) ||
                _context.Evidence.CurrentHash(row, proof) is null)) return (false, null);
        var existingFolder = false;
        // An occupied create-only target has the same D20 outcome before admission as at the adapter.
        if (action.Kind is ActionKind.Upload or ActionKind.Download or ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder)
        {
            var written = action.WrittenSide();
            var source = written == Side.Local ? dropbox : local;
            var target = written == Side.Local ? local : dropbox;
            if (row.EntryOn(written) is null && target is not null && source == row.EntryOn(PathNames.Other(written)))
            {
                existingFolder = target.IsFolder && action.Kind is ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder;
                if (!existingFolder)
                {
                    if (Decide.CrossedFar(local, dropbox, written == Side.Local ? localBinding : dropboxBinding)
                        == PathNames.Other(written)) return (false, null);
                    throw new PathOccupiedException(target.Path);
                }
            }
        }
        // A one-sided unlinked move writes only its own side; the opposite side is not a freshness precondition.
        var fresh = action.Kind == ActionKind.MoveUnlinked
            ? action.WrittenSide() == Side.Local
                ? local == row.Local && localBinding == row.Synced
                : dropbox == row.Dropbox && dropboxBinding == row.Synced
            : row.Synced is null && Decide.IsFreeIncoming(row.Local, row.Dropbox, localBinding, dropboxBinding)
                ? (row.Local is null || local == row.Local) && (row.Dropbox is null || dropbox == row.Dropbox)
                : (local == row.Local || existingFolder && row.Local is null) &&
                  (dropbox == row.Dropbox || existingFolder && row.Dropbox is null) &&
                  (Decide.CrossedFar(row.Local, row.Dropbox, row.Synced) is { } far
                      ? far == Side.Local ? localBinding is null && dropboxBinding == row.Synced
                          : localBinding == row.Synced && dropboxBinding is null
                      : localBinding == row.Synced && dropboxBinding == row.Synced);
        if (!fresh || action.Kind is not (ActionKind.Upload or ActionKind.Download or ActionKind.EnsureDropboxFolder or
            ActionKind.EnsureLocalFolder or ActionKind.ReplaceKind)) return (fresh, null);
        var side = action.WrittenSide();
        return (true, (side, side == Side.Local ? paths.Local : paths.Dropbox, side == Side.Local ? local : dropbox));
    }

    internal async Task<Row> ReobserveAsync(Row row, Observer observer, CancellationToken ct)
    {
        var paths = Row.Paths(row);
        var side = Decide.CrossedFar(row.Local, row.Dropbox, row.Synced) ??
            (row.Local is not null || row.Dropbox is null ? Side.Local : Side.Dropbox);
        var fresh = await observer.ObserveAsync(side, side == Side.Local ? paths.Local : paths.Dropbox, ct);
        fresh = fresh with { Context = row.Context with
        {
            Destination = fresh.Context.Destination,
            Skipped = row.Context.Skipped || fresh.Context.Skipped
        } };
        if (row.Synced is null && Decide.CrossedFar(fresh.Local, fresh.Dropbox, fresh.Synced) is null)
        {
            var local = row.Local is null ? null : fresh.Local;
            var dropbox = row.Dropbox is null ? null : fresh.Dropbox;
            var localBinding = _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, PathNames.KeyFor(Side.Local, paths.Local));
            var dropboxBinding = _context.Store.SyncedByKey(_context.Pair.PairId, Side.Dropbox, PathNames.KeyFor(Side.Dropbox, paths.Dropbox));
            if (Decide.IsFreeIncoming(local, dropbox, localBinding, dropboxBinding))
                fresh = fresh with { Local = local, Dropbox = dropbox, Synced = null };
        }
        return Rows.RefreshDeletionEligibility(IssuePolicy.ReobserveFirstRun(row, fresh));
    }

    // A content answer exists only for a saved same-kind file; a kind change decides before content is read.
    private LocalEvidence.DirtyProof? ReadDirtyIfApplicable(Row row) =>
        LocalEvidence.AppliesTo(row) ? ReadDirty(row) : null;

    /// <summary>The one stored dirty answer for this row, or null when no saved same-kind content answer applies.
    /// The owner is the store's LocalEvidence; callers never supply a literal.</summary>
    internal bool? LocalContentDirty(Row row) => ReadDirtyIfApplicable(row)?.Dirty;

    private LocalEvidence.DirtyProof ReadDirty(Row row)
    {
        var evidence = _context.Evidence.Read(row.Local ?? throw new ArgumentException(nameof(row)), row.Synced);
        return new LocalEvidence.DirtyProof(evidence.Dirty, evidence.Boundary, evidence.Version);
    }

    private async Task ObserveSkippedAsync(Row row, IAdmittedExecution? execution, CancellationToken ct)
    {
        var paths = Row.Paths(row);
        var keys = LockKeys.ForPaths(_context.Pair, [(Side.Local, paths.Local), (Side.Dropbox, paths.Dropbox)]);
        if (execution is null)
        {
            await using var hold = await PathLocks.Shared.EnterAsync(keys, ct);
            UpdateSkipped(row, paths);
            return;
        }
        // A survivor runs inside an outer admission that already holds these keys; PathLocks is not reentrant,
        // so the existing execution is validated instead of taking the keys again.
        execution.Require(_context.Pair.PairId, _context.ExpectedGeneration, keys.ToArray());
        UpdateSkipped(row, paths);
    }

    private void UpdateSkipped(Row row, (string Local, string Dropbox) paths)
    {
        // The row is already observed: maintaining its skipped notice needs no further provider read.
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var entry = row.EntryOn(side);
            var path = side == Side.Local ? paths.Local : paths.Dropbox;
            if (side == Side.Local && !PathNames.IsTransferable(path)) continue;
            _context.Store.ObserveSkipped(_context.Pair.PairId, _context.ExpectedGeneration, side, path,
                entry?.Skip is { } cause
                    ? (Enum.Parse<TextKey>(cause), new IssueSubject([new IssueItem(side, entry.Path, entry.Id)], side)) : null);
        }
    }

    private void Finish(SyncAction action, SyncOutcome outcome, Action<Row, SyncOutcome>? completed)
    {
        Rows.Observe(action, outcome);
        completed?.Invoke(action.Row, outcome);
    }

}
