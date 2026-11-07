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

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Row, DirtyProof> _proofs = new();

    private RowContexts? _rows;
    internal RowContexts Rows => _rows ??= RowContexts.ForSelection(_context);

    internal sealed record DirtyProof(bool Dirty, JournalPosition? Boundary, long Version);

    /// <summary>A prepared live row with the label set its action needs and the phase its action takes.</summary>
    internal readonly record struct LivePreparation(SyncAction Action, KeepBothLabels? Labels, bool TakeReplacementPhase);

    /// <summary>One execution's run-local facts; a session runs one scan, or its selections in sequence.</summary>
    internal RowContexts ScanContexts(bool firstRun, IReadOnlySet<long>? retries,
        IReadOnlyDictionary<string, IssueSubject>? proofs, Func<string, bool>? deletedName) =>
        Rows.ForScan(firstRun, retries, proofs, deletedName);

    internal async ValueTask<(SyncAction Action, SyncOutcome? Completed)> PrepareRowAsync(
        Row row, CancellationToken ct,
        Action<Row, SyncOutcome>? completed = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, DirtyProof? evidence = null)
    {
        var action = await PrepareAsync(row, ct, evidence);
        if (action.Kind is ActionKind.None or ActionKind.DeferFolder)
        {
            var outcome = await _executor.ExecuteAsync(action, null, ct,
                attemptId: attemptId, expectedAttempt: expectedAttempt);
            Finish(action, outcome, completed);
            return (action, outcome);
        }
        if (action.Kind == ActionKind.Record &&
            await TryRecordAsync(action, ct, attemptId, expectedAttempt) is { } recorded)
        {
            Finish(action, recorded, completed);
            return (action, recorded);
        }
        return (action, null);
    }

    internal async Task<SyncOutcome> DispatchPreparedAsync(SyncAction prepared, CancellationToken ct, Action? releaseOrdinaryCapacity = null,
        Action<Row, SyncOutcome>? completed = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, WorkSelection? selection = null)
    {
        if (RequiresFolderReplacement(prepared))
            return await ReplaceDropboxFolderRegisteredAsync(prepared, ct, attemptId, expectedAttempt, completed, selection, null);
        await using var work = await _context.Admission.EnterWorkAsync(prepared.Kind == ActionKind.Upload, ct);
        if (prepared.Kind != ActionKind.Upload)
        {
            await using var permit = await _context.EnterOperationAsync(ct);
            return await RunAdmittedAsync(prepared.Row, ct, preparedAction: prepared,
                completed: completed, attemptId: attemptId, expectedAttempt: expectedAttempt);
        }
        releaseOrdinaryCapacity?.Invoke();
        IAsyncDisposable? upload = await _context.Admission.AdmitUploadAsync(_context.UploadAdmissionIdentity, ct);
        try
        {
            var keys = LockKeys.For(prepared, _context.Pair, null);
            await using (await PathLocks.Shared.EnterAsync(keys, ct))
            {
                async ValueTask Staged()
                {
                    var owned = upload;
                    upload = null;
                    if (owned is null) return;
                    _context.Admission.UploadStaged();
                    await owned.DisposeAsync();
                }
                (SyncOutcome? Outcome, bool Reobserve) result;
                try
                {
                    result = await ExecuteLockedAsync(prepared, null, keys, keys, ct, Staged, null,
                        attemptId: attemptId, expectedAttempt: expectedAttempt);
                }
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
        }
        finally
        {
            if (upload is not null) await upload.DisposeAsync();
        }
        _context.Admission.DisallowDirectUpload();
        await using var ordinary = await _context.EnterOperationAsync(ct);
        var observed = await ReobserveAsync(prepared.Row, new Observer(_context, _parents), ct);
        return await RunAdmittedAsync(observed, ct, completed: completed, attemptId: attemptId, expectedAttempt: expectedAttempt);
    }

    internal static bool RequiresFolderReplacement(SyncAction action) =>
        action.Kind == ActionKind.ReplaceKind && action.Side == Side.Dropbox && action.Row.Dropbox is { IsFolder: true };

    /// <summary>Prepares a live row and returns the label set its action needs, so no caller chooses labels.
    /// The replacement phase is the owner's answer too; the caller only sequences live admission.</summary>
    internal async ValueTask<LivePreparation> PrepareLiveAsync(Row row, CancellationToken ct, DirtyProof? evidence = null)
    {
        var action = await PrepareAsync(row, ct, evidence);
        var labels = action.Kind == ActionKind.KeepBoth ? await _keepBoth.ChooseLabelsAsync(action.Row, ct) : null;
        return new(action, labels, RequiresFolderReplacement(action));
    }

    /// <summary>Runs one live folder-replacement phase, registering its attempt exactly as the live item path does.</summary>
    internal Task<SyncOutcome> RunLiveReplacementAsync(SyncAction prepared, WorkSelection selection, IssueSubject subject,
        ItemRetryCandidate? expected, Action<Row, Issue> retainUnfinished, CancellationToken ct) =>
        _context.ItemAttempts.ExecuteAsync(selection, expected, subject, async (attemptId, token) =>
        {
            try
            {
                return await ReplaceDropboxFolderRegisteredAsync(prepared, token, attemptId, expected, null, selection, retainUnfinished);
            }
            catch (ChangedUnderneathException)
            {
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
            }
            catch (Exception error) when (ActionExecutor.IsItemError(error))
            {
                return await _executor.FailAsync(prepared.Row, prepared, error, token,
                    issue => retainUnfinished(prepared.Row, issue), attemptId: attemptId, expectedAttempt: expected, selection: selection);
            }
        }, ct);

    private async Task<SyncOutcome> ReplaceDropboxFolderRegisteredAsync(SyncAction action, CancellationToken ct,
        Guid? attemptId, ItemRetryCandidate? expectedAttempt, Action<Row, SyncOutcome>? completed,
        WorkSelection? selection, Action<Row, Issue>? retainUnfinished)
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
            var prepared = await PrepareLiveAsync(fresh, ct);
            var footprint = LockKeys.ForLiveAction(prepared.Action, _context.Pair, prepared.Labels);
            var coverage = (await _live.CaptureCoverageAsync(selection, footprint, ct)) with { Peer = selection.Peer };
            return await _live.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
                (execution, token) => RunAdmittedAsync(fresh, token, preparedAction: prepared.Action,
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

    internal async Task<SyncOutcome> RunRowAttemptAsync(Row row, CancellationToken ct, Action? releaseOrdinaryCapacity = null,
        Action<Row, SyncOutcome>? completed = null,
        PrefixSet? itemRetries = null, WorkSelection? selection = null)
    {
        // One dirty answer per row, shared by the route pre-check and Prepare; rows settled before content
        // (out of scope or skipped) read none, as Prepare never did.
        var settled = Decide.WithoutContent(row);
        var evidence = settled is null ? ReadDirtyIfApplicable(row) : null;
        var initial = settled ?? Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(), evidence?.Dirty == true);
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
            var prepared = await PrepareRowAsync(row, ct, completed, evidence: evidence);
            if (prepared.Completed is { } done) return done;
        }
        var (subject, expected) = CaptureAttempt(row, inUse);
        return await _context.ItemAttempts.ExecuteAsync(_context.Opportunity, expected,
            subject, async (attempt, token) =>
            {
                try
                {
                    var prepared = await PrepareRowAsync(row, token, completed, attempt, expected, evidence);
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
        var keys = LockKeys.For(action, _context.Pair, labels);
        LockKeys.Require(execution, _context.Pair, _context.ExpectedGeneration, action, labels, keys);
        var result = await ExecuteLockedAsync(action, labels, keys, keys, ct, null, null, closeIssues: false);
        return result.Outcome ?? throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
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

    internal RenameRunner Renamer(bool completedScan = false) => new(_context, _parents, Rows, _ancestors, completedScan);

    /// <summary>Refuses a live effect that writes Local while Local skipped-coverage or lost-interval debt is owed;
    /// the work stays pending and Repair's completion wakes the pair.</summary>
    internal static void RequireLiveWrite(ExecutorContext context, bool writesLocal)
    {
        if (writesLocal && context.Live && context.Store.ReadLiveRefusal(context.Pair.PairId) == LiveRefusal.LocalWrites)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
    }

    internal async Task<SyncOutcome> RunAdmittedAsync(Row row, CancellationToken ct, Observer? observer = null, SyncAction? preparedAction = null,
        IAdmittedExecution? execution = null, WorkSelection? selection = null,
        Action<Row, Issue>? retainUnfinished = null,
        Action<Row, SyncOutcome>? completed = null, KeepBothLabels? preparedLabels = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null)
    {
        var action = preparedAction;
        var unchanged = false;
        // A folder deferred from an older listing is re-observed before its plan runs; a restored folder is in sync.
        var reobserve = action?.Kind is ActionKind.Record or ActionKind.DeferFolder;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Action<Issue>? committed = retainUnfinished is null ? null : issue => retainUnfinished(row, issue);
            try
            {
                if (reobserve)
                {
                    // A live selection runs with no work scope of its own, so an unrelated sole scope must not let the
                    // replacement action stage direct; the limiter keeps deciding, this only withdraws it for the turn.
                    _context.Admission.DisallowDirectUpload();
                    observer ??= new Observer(_context, _parents);
                    var fresh = await ReobserveAsync(row, observer, ct);
                    unchanged = fresh.Local == row.Local && fresh.Dropbox == row.Dropbox && fresh.Synced == row.Synced;
                    row = fresh;
                    action = null;
                }
                action ??= await PrepareAsync(row, ct);
                row = action.Row;
                if (RequiresFolderReplacement(action))
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
                if (action.Kind is ActionKind.None or ActionKind.DeferFolder)
                {
                    var outcome = await _executor.ExecuteAsync(action, null, ct, committedIssue: committed,
                        attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection);
                    Finish(action, outcome, completed);
                    return outcome;
                }
                RequireLiveWrite(_context, action.WritesLocal());
                var labels = action.Kind == ActionKind.KeepBoth
                    ? preparedLabels ?? await _keepBoth.ChooseLabelsAsync(row, ct) : null;
                var keys = LockKeys.For(action, _context.Pair, labels);
                if (execution is not null)
                    LockKeys.Require(execution, _context.Pair, _context.ExpectedGeneration, action, labels, keys);
                await using (var hold = execution is null ? await PathLocks.Shared.EnterAsync(keys, ct) : null)
                {
                    var result = await ExecuteLockedAsync(action, labels, keys, keys, ct, null, committed,
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
                }
                if (unchanged)
                {
                    // A row that did not change after a required re-observation is that row's own Failure; the run carries on.
                    var failed = await _executor.FailAsync(row, action, new ChangedUnderneathException(Row.Path(row)), ct,
                        committed, attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection);
                    Finish(action, failed, completed);
                    return failed;
                }
                // A live row that changed again waits for its own change record rather than looping inside the lease.
                if (reobserve && selection is not null)
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
                reobserve = true;
            }
            catch (Exception error) when (ActionExecutor.IsItemError(error))
            {
                var failed = await _executor.FailAsync(row, action, error, ct, committed,
                    attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection);
                if (action is not null) Finish(action, failed, completed);
                else completed?.Invoke(row, failed);
                return failed;
            }
        }
    }

    internal async ValueTask<(SyncOutcome? Outcome, bool Reobserve)> ExecuteLockedAsync(
        SyncAction action, KeepBothLabels? labels, IReadOnlyCollection<string> requiredKeys,
        IReadOnlyCollection<string> heldKeys, CancellationToken ct,
        Func<ValueTask>? uploadStaged, Action<Issue>? committedIssue, Row? firstRunFolder = null, WorkSelection? selection = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, bool closeIssues = true)
    {
        ArgumentNullException.ThrowIfNull(requiredKeys);
        ArgumentNullException.ThrowIfNull(heldKeys);
        if (!ReferenceEquals(requiredKeys, heldKeys) && requiredKeys.Any(key => !heldKeys.Contains(key, PathLocks.KeyComparer)))
            return (null, true);
        if (OutsideBatchScope(action, labels))
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        var news = await _ancestors.BeforeExecutionAsync(action, _parents, ct);
        if (news == AncestorNews.Changed)
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        (bool Fresh, (Side Side, string Path, Entry? Entry)? ObservedTarget) fresh;
        try { fresh = await IsFreshAsync(action.Row, action, ct); }
        catch (PathOccupiedException) when (news == AncestorNews.Restated)
        {
            _ancestors.Refuse();
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        }
        if (news == AncestorNews.Restated && (!fresh.Fresh || fresh.ObservedTarget is not { Entry: null }))
        {
            _ancestors.Refuse();
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        }
        if (!fresh.Fresh)
        {
            return (null, true);
        }
        if (labels is not null && !await _keepBoth.AreUsableAsync(action.Row, labels, ct)) return (null, false);
        return (await _executor.ExecuteAsync(action, labels, ct, closeIssues, uploadStaged,
            observedTarget: fresh.ObservedTarget, committedIssue: committedIssue, firstRunFolder: firstRunFolder,
            attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection), false);
    }

    private bool OutsideBatchScope(SyncAction action, KeepBothLabels? labels) =>
        _context.BatchScope is { } scope && LockKeys.ForLiveAction(action, _context.Pair, labels)
            .Any(item => (item.Side == Side.Local
                ? PathNames.RelativeUnderLocalRoot(_context.Pair.LocalRoot, item.NativePath)
                : PathNames.RelativeUnderDropboxRoot(_context.Pair.DropboxRoot, item.NativePath)) is not { } relative ||
                !PathNames.IsSelfOrUnder(relative, scope));

    // A private method, not a local function: a local would capture action and ct in a heap closure on every call.
    private async ValueTask<(SyncOutcome? Outcome, bool Reobserve)> RefusedAsync(SyncAction action, CancellationToken ct,
        Action<Issue>? committedIssue, Row? firstRunFolder, Guid? attemptId, ItemRetryCandidate? expectedAttempt,
        WorkSelection? selection) =>
        (await _executor.FailAsync(action.Row, action, new ChangedUnderneathException(Row.Path(action.Row)),
            ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection), false);

    private async ValueTask<SyncOutcome?> TryRecordAsync(SyncAction action, CancellationToken ct,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null)
    {
        var keys = LockKeys.For(action, _context.Pair, null);
        await using var hold = await PathLocks.Shared.EnterAsync(keys, ct);
        return (await ExecuteLockedAsync(action, null, keys, keys, ct, null, null, attemptId: attemptId, expectedAttempt: expectedAttempt)).Outcome;
    }

    internal async ValueTask<SyncAction> PrepareAsync(Row row, CancellationToken ct, DirtyProof? evidence = null)
    {
        ct.ThrowIfCancellationRequested();
        row = Rows.RefreshDeletionEligibility(row);
        if (Decide.WithoutContent(row) is { } settled) return AdmitInBatch(settled);
        if (row.Synced is null && row.Local is { } local && row.Dropbox is null)
            row = row with { Context = row.Context with { Destination = _parents.Destination(Side.Dropbox, local.Path) } };
        else if (row.Synced is null && row.Dropbox is { } remote && row.Local is null)
            row = row with { Context = row.Context with { Destination = _parents.Destination(Side.Local, remote.Path) } };
        if (evidence is null && LocalEvidence.AppliesTo(row)) evidence = ReadDirty(row);
        if (evidence?.Dirty == true) row = row with { LocalHash = null };
        var action = AdmitInBatch(Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(), evidence?.Dirty == true));
        if (action.Kind == ActionKind.NeedDeletionEvidence)
        {
            row = row with { Deletion = await _context.ReadDeletionEvidenceAsync(action.Target!, row.IsFolder(), ct) };
            if (row.Deletion is DeletionEvidence.LiveAtTarget live)
                row = IssuePolicy.ReobserveFirstRun(row, row with { Dropbox = live.Location.Entry, Deletion = null });
            else if (row.Deletion is DeletionEvidence.LiveElsewhere)
                throw new ChangedUnderneathException(action.Target!);
            action = Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(), evidence?.Dirty == true);
        }
        // A held row settles without a hash: Decide already refused it, and no answer can release it.
        if (action.Kind == ActionKind.NeedLocalHash || evidence?.Dirty == true && action.None != NoneReason.Held)
        {
            evidence ??= ReadDirty(row);
            var hashed = await _context.Evidence.HashAsync(_context.Local, row.Local!, row.Synced,
                (evidence.Dirty, evidence.Boundary, evidence.Version), ct);
            row = row with { LocalHash = hashed.Hash };
            _proofs.Add(row, new DirtyProof(hashed.Proof.Dirty, hashed.Proof.Boundary, hashed.Proof.Version));
            action = Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(), evidence?.Dirty == true);
        }
        return AdmitInBatch(action);
    }

    internal SyncAction AdmitInBatch(SyncAction action) => _context.BatchScope is null ? action :
        Decide.AdmitInBatch(action, action.Row.Synced is null && BoundById(action.Row),
            _context.BatchUnlisted, _context.BatchSettlesFirstRun);

    private bool BoundById(Row row) =>
        row.Local is { } local && _context.Store.SyncedById(_context.Pair.PairId, Side.Local, local.Id) is not null ||
        row.Dropbox is { } remote && _context.Store.SyncedById(_context.Pair.PairId, Side.Dropbox, remote.Id) is not null ||
        row.Extra.Any(entry => _context.Store.SyncedById(_context.Pair.PairId, Side.Dropbox, entry.Id) is not null);

    private async ValueTask<(bool Fresh, (Side Side, string Path, Entry? Entry)? ObservedTarget)> IsFreshAsync(
        Row row, SyncAction action, CancellationToken ct)
    {
        if (row.Context.DeletionEligible && !Rows.RefreshDeletionEligibility(row).Context.DeletionEligible)
            return (false, null);
        var paths = Row.Paths(row, action.Target);
        var storeOnly = action.Kind == ActionKind.Record;
        var local = storeOnly ? row.Local : await _context.Local.ReadOneAsync(paths.Local, ct);
        var dropbox = storeOnly ? row.Dropbox : await _context.Dropbox.ReadOneAsync(paths.Dropbox, ct);
        var localBinding = _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, PathNames.KeyFor(Side.Local, paths.Local));
        var dropboxBinding = _context.Store.SyncedByKey(_context.Pair.PairId, Side.Dropbox, PathNames.KeyFor(Side.Dropbox, paths.Dropbox));
        // A binding of this new row's identity committed since preparation is a move-in: reobserve, the seam leaves it.
        if (_context.BatchScope is not null && row.Synced is null && BoundById(row)) return (false, null);
        if (_proofs.TryGetValue(row, out var proof) && !LocalEvidence.SameFile(
            (proof.Dirty, proof.Boundary, proof.Version), _context.Evidence.Read(row.Local!, row.Synced))) return (false, null);
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
    private DirtyProof? ReadDirtyIfApplicable(Row row) =>
        LocalEvidence.AppliesTo(row) ? ReadDirty(row) : null;

    /// <summary>The one stored dirty answer for this row, or null when no saved same-kind content answer applies.
    /// The owner is the store's LocalEvidence; callers never supply a literal.</summary>
    internal bool? LocalContentDirty(Row row) => ReadDirtyIfApplicable(row)?.Dirty;

    private DirtyProof ReadDirty(Row row)
    {
        var evidence = _context.Evidence.Read(row.Local ?? throw new ArgumentException(nameof(row)), row.Synced);
        return new DirtyProof(evidence.Dirty, evidence.Boundary, evidence.Version);
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
