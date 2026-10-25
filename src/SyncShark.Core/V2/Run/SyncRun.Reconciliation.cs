namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private readonly Dictionary<string, ActiveReconciliation> _reconciling = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _reconciliationRunId = new();

    internal Task<RunReport> ReconcileScopeAsync(string pairId, StateMonitorDecision decision,
        IProgress<RunProgress> progress, CancellationToken ct)
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
            _reconciliationRunId.Value = id;
            try
            {
                return await RunExclusiveAsync(progress, (sink, token) => ReconcileScopeBodyAsync(pairId, active, sink, token),
                    ct, historyPairId: pairId, admissionPairId: pairId, admitBody: false);
            }
            finally
            {
                lock (_stopGate) _reconciling.Remove(pairId);
                _reconciliationRunId.Value = null;
                try { if (_store is SqliteStateStore sql) sql.ClearRemovalObservation(active.Session + ":work"); }
                catch (Exception error) { RunOutput.Observe(error); }
            }
        }
    }

    private async Task<RunReport> ReconcileScopeBodyAsync(string pairId, ActiveReconciliation active,
        IProgress<RunProgress> progress, CancellationToken ct)
    {
        if (!_store.FirstSyncDone(pairId)) throw new SyncRunRefusedException("Review and apply the first sync first.");
        var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("No pair " + pairId);
        var sql = _store as SqliteStateStore ?? throw new InvalidOperationException(MessageCatalog.Default.Template("reconciliation-staging-required"));
        await _endpoints.PrepareAsync(pair, ct);
        var throttle = new ProgressThrottle(progress, _time, _settings.ProgressInterval);
        foreach (var side in ReconciliationSides(pair, active.Decision.Trigger))
            await SelectAsync(pair, side, active.Decision, active.Session, throttle, ct);
        lock (_stopGate) active.Selecting = false;
        var total = new RunReport(pairId, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, []);
        while (NextReconciliationBatch(sql, active.Session) is { } batch)
        {
            ct.ThrowIfCancellationRequested();
            var report = await _admission.WithAsync(pairId,
                () => ReconcileBatchAsync(pair, batch, progress, ct), ct);
            total = AddReconciliationReport(total, report);
            lock (_stopGate) active.LiveProgress = new(RunPhases.Reconcile, null, total.Done, 0);
            if (report.HaltedBy is not null || report.Cancelled || report.Paused || report.CompletionFailed) break;
        }
        throttle.Flush();
        return total;
    }

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
        if (report.HaltedBy is not null || report.Cancelled || report.Paused || report.CompletionFailed) return report;
        FinishReconciliationDisposition(pair, batch);
        report = report with { Deferred = Math.Max(report.Deferred,
            batch.Dispositions.Values.Count(value => value.StartsWith("deferred:", StringComparison.Ordinal) ||
                value.StartsWith("notice:", StringComparison.Ordinal))) };
        var sql = (SqliteStateStore)_store;
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
