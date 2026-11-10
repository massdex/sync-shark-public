using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Prepares, observes and executes item decisions through the shared action executor.</summary>
internal sealed class ItemSync
{
    private readonly ExecutorContext _context;
    private readonly ActionExecutor _executor;
    internal FolderSync Folders { get; }
    internal ItemPreparation Preparation { get; }
    internal ItemExecution Execution { get; }
    internal ItemAdmission Admission { get; }
    internal ItemReplacement Replacement { get; }
    internal ItemDispatch Dispatch { get; }
    internal ItemRenames Renames { get; }
    internal ItemObservation Observation { get; }

    internal ItemSync(ExecutorContext context, Parents parents, ActionExecutor executor, KeepBothRunner keepBoth,
        FolderDeleteRunner folderDelete, SyncPorts ports, AncestorFence ancestors)
    {
        _context = context;
        _executor = executor;
        Observation = new ItemObservation(context, parents, executor, this);
        Preparation = new ItemPreparation(context, parents, keepBoth, this);
        Execution = new ItemExecution(context, parents, executor, this);
        Admission = new ItemAdmission(context, parents, executor, keepBoth, ancestors, this);
        Replacement = new ItemReplacement(context, parents, executor, ports.Live, this);
        Renames = new ItemRenames(context, parents, executor, ancestors, ports.Live, this);
        Dispatch = new ItemDispatch(context, executor, this);
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
        var action = await Preparation.PrepareAsync(row, ct, evidence);
        var owned = _context.Evidence.CaptureIfHashed(action.Row);
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

    // The pre-check and the captured attempt Prepare must read the same dirty answer; splitting would let them judge different evidence.
    internal async Task<SyncOutcome> RunRowAttemptAsync(Row row, CancellationToken ct, Action? releaseOrdinaryCapacity = null,
        Action<Row, SyncOutcome>? completed = null,
        PrefixSet? itemRetries = null, WorkSelection? selection = null, Guid? listedScanId = null)
    {
        // One dirty answer per row, shared by the route pre-check and Prepare; rows settled before content
        // (out of scope or skipped) read none, as Prepare never did.
        var settled = Decide.WithoutContent(row);
        var evidence = settled is null ? Preparation.ReadDirtyIfApplicable(row) : null;
        var initial = settled ?? Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(),
            _context.Evidence.ContentUnproven(row, evidence));
        // A row with an open in-use episode goes through the attempt path even when it looks in sync, so its
        // completion retires the episode instead of leaving the notice held.
        var inUse = itemRetries is not null && IssuePolicy.CoveredKey(row, itemRetries) is not null;
        if (!inUse && initial.Kind is ActionKind.None or ActionKind.DeferFolder && row.LocalHash is null)
        {
            if (initial.Kind == ActionKind.DeferFolder)
            {
                // A deferral is never executed from the listing: the admitted path re-observes it first.
                var deferred = await Preparation.PrepareAsync(row, ct);
                return await Dispatch.DispatchPreparedAsync(deferred, ct, releaseOrdinaryCapacity,
                    completed, selection: selection);
            }
            var prepared = await PrepareRowAsync(row, ct, completed, evidence: evidence, listedScanId: listedScanId);
            if (prepared.Completed is { } done) return done;
        }
        try
        {
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
                            return await Execution.RunAdmittedAsync(row, token, completed: completed,
                                attemptId: attempt, expectedAttempt: expected, reobserve: true);
                        }
                        return prepared.Completed ?? await Dispatch.DispatchPreparedAsync(prepared.Action, token,
                            releaseOrdinaryCapacity, completed, attempt, expected, selection);
                    }
                    catch (Exception error) when (ItemFailures.IsItemError(error))
                    {
                        var failed = await _executor.Failures.FailAsync(row, null, error, token, attemptId: attempt, expectedAttempt: expected);
                        Finish(initial, failed, completed);
                        return failed;
                    }
                }, ct);
        }
        catch (LiveAdmissionStaleException error) when (error.Reason == LiveAdmissionStaleReason.SelectionChanged)
        {
            // engine.md 369: a row whose item attempt is refused as a changed selection (its retry capture no longer
            // matches, another attempt holds an intersecting item, or its opportunity or retry claim was taken) raises
            // that item's Failure and lets its siblings finish. ExecuteAsync has already released any reservation it
            // made, so the Failure is raised with no attempt, and none is re-acquired.
            return await Execution.FailAndFinishAsync(row, initial, new ChangedUnderneathException(Row.Path(row), error), ct,
                null, completed, null, null, selection);
        }
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
        try { return await Observation.SyncFollowUpAsync(side, sidePath, ct, completed: completed); }
        finally { if (retryIssues is not null) _rows = previous; }
    }

    internal async Task<SyncOutcome> ExecuteSelectedAsync(IAdmittedExecution execution, 
        SyncAction action, KeepBothLabels? labels, CancellationToken ct)
    {
        var owned = _context.Evidence.CaptureIfHashed(action.Row);
        try
        {
            var keys = LockKeys.For(action, _context.Pair, labels);
            LockKeys.Require(execution, _context.Pair, _context.ExpectedGeneration, action, labels, keys);
            var result = await Admission.ExecuteLockedAsync(action, labels, keys, keys, ct, null, null, closeIssues: false);
            return result.Outcome ?? throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
        }
        finally { _context.Evidence.Release(action.Row, owned); }
    }

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

    private async ValueTask<SyncOutcome?> TryRecordAsync(SyncAction action, CancellationToken ct,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, Guid? listedScanId = null)
    {
        var keys = LockKeys.For(action, _context.Pair, null);
        await using var hold = await PathLocks.Shared.EnterAsync(keys, ct);
        return (await Admission.ExecuteLockedAsync(action, null, keys, keys, ct, null, null, attemptId: attemptId, expectedAttempt: expectedAttempt, listedScanId: listedScanId)).Outcome;
    }

    internal void Finish(SyncAction action, SyncOutcome outcome, Action<Row, SyncOutcome>? completed)
    {
        Rows.Observe(action, outcome);
        completed?.Invoke(action.Row, outcome);
    }

}
