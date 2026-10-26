using System.Collections.Concurrent;
using System.Globalization;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private const string MissingPairPrefix = "No pair ";
    private const string FirstSyncRequired = "Review and apply the first sync first.";
    private const string ReconcileLogCommand = "reconcile";
    private readonly Dictionary<string, ActiveReconciliation> _reconciling = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IntakeRetirement> _intakeRetired = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _reconciliationRunId = new();

    /// <summary>The last batch and running total of deferred planning rows this pair's reconciliation retired.</summary>
    internal IntakeRetirement? IntakeRetired(string pairId) =>
        _intakeRetired.TryGetValue(pairId, out var intake) ? intake : null;

    /// <summary>A removed pair leaves no reachable retirement total; drop it with the pair's other in-memory state.</summary>
    internal void ForgetIntakeRetirement(string pairId) => _intakeRetired.TryRemove(pairId, out _);

    internal Task<RunReport> ReconcileScopeAsync(string pairId, StateMonitorDecision decision,
        IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var id = "reconcile-" + Guid.NewGuid().ToString("N");
        var active = new ActiveReconciliation(decision, "rc-" + id);
        lock (_stopGate)
        {
            if (_reconciling.ContainsKey(pairId)) throw new SyncRunRefusedException(MessageCatalog.Default.Template("reconciliation-active"));
            _reconciling.Add(pairId, active);
        }
        return RunScope();

        async Task<RunReport> RunScope()
        {
            var previousTrace = RunTrace.Slot.Value;
            var previousRunId = _reconciliationRunId.Value;
            _reconciliationRunId.Value = id;
            using var log = _beginRunLog?.Invoke(pairId, ReconcileLogCommand);
            try
            {
                LogScope(decision);
                var run = RunExclusiveAsync(progress, (sink, token) => ReconcileScopeBodyAsync(pairId, active, sink, token),
                    ct, historyPairId: pairId, admissionPairId: pairId, admitBody: false);
                // The run captured this scope's context at its first yield; restoring the monitor caller's
                // ambient trace and run id keeps the ordinary run that follows on its own log and history entry.
                RunTrace.Slot.Value = previousTrace;
                _reconciliationRunId.Value = previousRunId;
                return await run;
            }
            finally
            {
                _reconciliationRunId.Value = previousRunId;
                RunTrace.Slot.Value = previousTrace;
                FinishReconciliationScope(pairId, active);
            }
        }
    }

    private static void LogScope(StateMonitorDecision decision) => RunTrace.Emit(string.Format(
        CultureInfo.InvariantCulture, MessageCatalog.Default.Template("reconciliation-scope-trace"), decision.Trigger,
        ScopeText(decision)));

    // The root scope is the empty subtree; no decided scope is the same root.
    private static string ScopeText(StateMonitorDecision decision)
    {
        var scopes = decision.UnknownScope?.ReadScopes.ToArray() ?? Array.Empty<string>();
        return scopes.Length == 0 || scopes.Contains(string.Empty) ? "root" : string.Join(",", scopes);
    }

    private static void ReportPhase(IProgress<RunProgress> progress, string phase)
    {
        progress.Report(new RunProgress(phase, null, 0, 0));
        TraceReconciliationPhase(phase);
    }

    private static void TraceReconciliationPhase(string phase) => RunTrace.Emit(string.Format(
        CultureInfo.InvariantCulture, MessageCatalog.Default.Template("reconciliation-phase-trace"), phase));

    private void FinishReconciliationScope(string pairId, ActiveReconciliation active)
    {
        try
        {
            if (_store is SqliteStateStore sql)
            {
                sql.ClearRemovalObservation(active.Session + ":work");
                sql.ClearRemovalObservation(active.Session + ":deferred");
            }
        }
        catch (Exception error) { RunOutput.Observe(error); }
        finally
        {
            lock (_stopGate)
            {
                _reconciling.Remove(pairId);
                active.Drained.TrySetResult();
            }
        }
    }

    private async Task<RunReport> ReconcileScopeBodyAsync(string pairId, ActiveReconciliation active,
        IProgress<RunProgress> progress, CancellationToken ct)
    {
        // Register scope and cancellable work synchronously, but never run selection on the monitor caller.
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (!_store.FirstSyncDone(pairId)) throw new SyncRunRefusedException(FirstSyncRequired);
        var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException(MissingPairPrefix + pairId);
        var sql = _store as SqliteStateStore ?? throw new InvalidOperationException(MessageCatalog.Default.Template("reconciliation-staging-required"));
        await _endpoints.PrepareAsync(pair, ct);
        var throttle = new ProgressThrottle(progress, _time, _settings.ProgressInterval);
        _intakeRetired[pairId] = new(0, 0);
        ReportPhase(progress, RunPhases.Selecting);
        if (!await SelectStableReconciliationAsync(pair, active, throttle, ct))
            return new RunReport(pairId, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, []) { Deferred = 1 };
        lock (_stopGate) active.Selecting = false;
        ReportPhase(progress, RunPhases.Repairing);
        var total = await RunReconciliationBatchesAsync(pair, active, sql, progress, ct);
        if (total.HaltedBy is null && !total.Cancelled && !total.Paused && !total.CompletionFailed)
            total = await CompleteReconciliationAsync(pair, active, total, ct);
        ReportIntakeRetired(pairId);
        throttle.Flush();
        return total;
    }

    private async Task<RunReport> RunReconciliationBatchesAsync(PairDef pair, ActiveReconciliation active,
        SqliteStateStore sql, IProgress<RunProgress> progress, CancellationToken ct)
    {
        var total = new RunReport(pair.PairId, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, []);
        long previousDeferred = long.MaxValue;
        while (true)
        {
            while (NextReconciliationBatch(sql, active.Session) is { } batch)
            {
                ct.ThrowIfCancellationRequested();
                var report = await RunAdmittedBatchAsync(pair, active, batch, progress, ct);
                total = AddReconciliationReport(total, report);
                LogBatch(batch, report);
                lock (_stopGate) active.LiveProgress = new(RunPhases.Reconcile, null, total.Done, 0);
                if (report.HaltedBy is not null || report.Cancelled || report.Paused || report.CompletionFailed) break;
            }
            if (total.HaltedBy is not null || total.Cancelled || total.Paused || total.CompletionFailed) break;
            var deferred = sql.DeferredReconciliationFolders(active.Session);
            RunTrace.Emit($"reconciliation deferred folders: remaining={deferred}");
            // Retry only after the main pass, and only while a pass shrinks the remaining work.
            // The same admission and fresh-read pipeline is used; saved actions are never replayed.
            if (deferred == 0 || deferred >= previousDeferred) break;
            previousDeferred = deferred;
            sql.RestageReconciliationFolders(active.Session);
        }
        return total;
    }

    private async Task<RunReport> RunAdmittedBatchAsync(PairDef pair, ActiveReconciliation active, ReconcileBatch batch,
        IProgress<RunProgress> progress, CancellationToken ct)
    {
        var queuedAt = _time.GetUtcNow();
        return await _admission.WithAsync(pair.PairId, async () =>
        {
            batch.AdmissionWait = _time.GetUtcNow() - queuedAt;
            using var decisionBudget = batch.MeasureDecisions(_settings.ReconciliationBatch.MaxBytes);
            try
            {
                return await ReconcileTracedBatchAsync((SqliteStateStore)_store, pair, active.Decision, batch, progress, ct);
            }
            finally
            {
                RunTrace.Emit($"reconciliation decision hash bytes: used={batch.DecisionBytes} budget={batch.DecisionByteLimit}");
                RunTrace.Emit($"reconciliation effect progress-stream bytes: read={batch.EffectReadBytes} written={batch.EffectWrittenBytes}; execution limits unchanged");
            }
        }, ct);
    }

    private static void LogBatch(ReconcileBatch batch, RunReport report)
    {
        long volume = 0;
        foreach (var item in batch.RowsA) volume += item.Size > 0 ? item.Size : 0;
        foreach (var item in batch.RowsB) volume += item.Size > 0 ? item.Size : 0;
        RunTrace.Emit(string.Format(CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("reconciliation-batch-trace"), batch.Paths.Count, volume,
            (long)batch.AdmissionWait.TotalMilliseconds, batch.IntakeRetired, report.Done, report.Uploaded,
            report.Downloaded, report.Deleted, report.Moved, report.KeptBoth, report.Held, report.Failed));
    }

    private void ReportIntakeRetired(string pairId)
    {
        if (_intakeRetired.TryGetValue(pairId, out var intake))
            RunTrace.Emit(string.Format(CultureInfo.InvariantCulture,
                MessageCatalog.Default.Template("reconciliation-intake-total"), intake.Total));
    }

    private IntakeRetirement AddIntakeRetired(string pairId, long retired) =>
        _intakeRetired.AddOrUpdate(pairId, _ => new IntakeRetirement(retired, retired),
            (_, previous) => new IntakeRetirement(retired, previous.Total + retired));

    private static IEnumerable<Side> ReconciliationSides(PairDef pair, ReconciliationTrigger trigger)
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            var root = side == Side.A ? pair.A : pair.B;
            if (trigger == ReconciliationTrigger.DropboxReset ? root.Provider == SyncProviders.Dropbox :
                trigger is ReconciliationTrigger.PlanningBacklog or ReconciliationTrigger.ScheduledConsistency ||
                root.Provider == SyncProviders.Local) yield return side;
        }
    }

    private ReconcileBatch? NextReconciliationBatch(SqliteStateStore sql, string session)
    {
        var bound = _settings.ReconciliationBatch;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var a = new List<ItemState>();
        var b = new List<ItemState>();
        long bytes = default;
        foreach (var side in new[] { Side.A, Side.B })
        {
            string key = "", path = "";
            while (sql.ReconcilePage(session + ":work", side, key, path, bound.MaxPaths) is { Count: > 0 } page)
            {
                foreach (var item in page)
                {
                    var size = Math.Max(default(long), item.Size);
                    if (paths.Count > 0 && (!paths.Contains(item.Path) && paths.Count >= bound.MaxPaths ||
                        size > bound.MaxBytes - bytes)) return Batch();
                    paths.Add(item.Path);
                    (side == Side.A ? a : b).Add(item);
                    bytes = size > long.MaxValue - bytes ? long.MaxValue : bytes + size;
                    if (bytes >= bound.MaxBytes) return Batch();
                }
                key = PathKey.Of(page[^1].Path); path = page[^1].Path;
            }
        }
        return paths.Count == 0 ? null : Batch();

        ReconcileBatch Batch() => new(session, paths.ToArray(),
            a.Select(item => item.ObjectId).ToHashSet(StringComparer.Ordinal),
            b.Select(item => item.ObjectId).ToHashSet(StringComparer.Ordinal),
            (side, path) => !paths.Contains(path) && sql.StagedAmong(session + ":work", side, [path]).Count > 0)
            { RowsA = a.ToArray(), RowsB = b.ToArray() };
    }

    private async Task<RunReport> ReconcileBatchAsync(PairDef pair, ReconcileBatch batch,
        IProgress<RunProgress> progress, CancellationToken ct)
    {
        var report = await IncrementalBodyAsync(pair.PairId, null, null, progress, ct, new RunOptions { Batch = batch });
        var intake = AddIntakeRetired(pair.PairId, batch.IntakeRetired);
        RunTrace.Emit(string.Format(CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("reconciliation-intake-trace"), batch.IntakeRetired, intake.Total));
        if (report.HaltedBy is not null || report.Cancelled || report.Paused || report.CompletionFailed) return report;
        FinishReconciliationDisposition(pair, batch);
        report = report with { Deferred = Math.Max(report.Deferred,
            batch.Dispositions.Values.Count(value => value.StartsWith("deferred:", StringComparison.Ordinal) ||
                value.StartsWith("notice:", StringComparison.Ordinal))) };
        var sql = (SqliteStateStore)_store;
        sql.StageReconcile(batch.Session + ":deferred", Side.A,
            batch.RowsA.Where(item => batch.DeferredFolders.Contains(item.Path)).ToArray());
        sql.StageReconcile(batch.Session + ":deferred", Side.B,
            batch.RowsB.Where(item => batch.DeferredFolders.Contains(item.Path)).ToArray());
        sql.RetireReconcile(batch.Session + ":work", Side.A, batch.RowsA);
        sql.RetireReconcile(batch.Session + ":work", Side.B, batch.RowsB);
        return report;
    }

    private RunReport AddReconciliationReport(RunReport total, RunReport batch) => batch with
    {
        Done = total.Done + batch.Done, Failed = total.Failed + batch.Failed,
        Held = total.Held + batch.Held, Uploaded = total.Uploaded + batch.Uploaded,
        Downloaded = total.Downloaded + batch.Downloaded, Deleted = total.Deleted + batch.Deleted,
        Moved = total.Moved + batch.Moved, KeptBoth = total.KeptBoth + batch.KeptBoth,
        Uncertain = total.Uncertain + batch.Uncertain, Skipped = total.Skipped + batch.Skipped,
        Failures = total.Failures.Concat(batch.Failures).Take(_settings.FailureSampleLimit).ToArray()
    };
}
