namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // The invalid durable cursor re-derives these signals after restart; never replace it until repair completes.
    private readonly Dictionary<(string PairId, string RootId), StateMonitorDecision> _pendingResets = new();
    Task<bool> ISyncRun.MonitorAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct) =>
        MonitorAsync(pairId, progress, ct);

    internal Task<bool> MonitorAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_stopGate) if (_reconciling.ContainsKey(pairId)) return Task.FromResult(false);
        if (_store is not SqliteStateStore sql || !_store.FirstSyncDone(pairId) ||
            _store.Pair(pairId) is not { Enabled: true } pair) return Task.FromResult(false);
        var decision = SampleReconciliation(pair, sql);
        return Task.FromResult(decision is not null && RequestReconciliation(pairId, decision, progress, ct));
    }

    private bool RequestReconciliation(string pairId, StateMonitorDecision decision,
        IProgress<RunProgress>? progress, CancellationToken ct)
    {
        lock (_stopGate)
        {
            if (decision.Trigger == ReconciliationTrigger.DropboxReset && decision.RootId is { } rootId)
            {
                if (_pendingResets.TryGetValue((pairId, rootId), out var pending)) decision = pending;
                else _pendingResets[(pairId, rootId)] = decision;
            }
            if (_reconciling.ContainsKey(pairId)) return false;
            RunTrace.Emit(MessageCatalog.Default.Template("monitor-scope-decision") + decision.Trigger);
            // ReconcileScopeAsync registers both scope and StopAsync work before its first yield.
            _ = ObserveMonitoredReconciliationAsync(pairId, _admission.WithoutBorrowedAdmissionAsync(
                () => ReconcileScopeAsync(pairId, decision, progress, ct)));
        }
        return true;
    }

    private StateMonitorDecision? SampleReconciliation(PairDef pair, SqliteStateStore sql)
    {
        var gaps = (ILocalGapStore)sql;
        foreach (var root in new[] { pair.A, pair.B })
        {
            if (root.Provider != SyncProviders.Local) continue;
            // Never replace an unfinished boundary with the cursor already advanced for replay.
            if (sql.PendingGapInventory(root.RootId) && gaps.GapEpoch(root.RootId) is { } pending)
                return StateMonitor.FromGap(pending) with { RootId = root.RootId };
            if (RehearsalEndpoints.Unwrap(_endpoints.Inventory(root)) is not LocalInventory local ||
                local.JournalCheckpoint() is not { } checkpoint) continue;
            var saved = _store.Cursor(root.RootId);
            // A missing cursor is first inventory or an explicit rescan, not a lost journal interval.
            if (string.IsNullOrEmpty(saved)) continue;
            var decision = StateMonitor.Journal(checkpoint, saved);
            if (decision.UnknownScope is null) continue;
            NtfsUsnCheckpoint.TryParseCursor(saved, out _, out var boundary);
            gaps.BeginGap(root.RootId, decision.ReplayCursor ?? checkpoint.Cursor, boundary,
                decision.Trigger == ReconciliationTrigger.GapSameJournal, protect: false);
            return decision with { RootId = root.RootId };
        }
        lock (_stopGate)
            foreach (var root in new[] { pair.A, pair.B })
                if (_pendingResets.TryGetValue((pair.PairId, root.RootId), out var reset)) return reset;
        return sql.HasPlanningBacklog(pair.PairId) ? StateMonitor.PlanningBacklog() : null;
    }

    private async Task ObserveMonitoredReconciliationAsync(string pairId, Task<RunReport> run)
    {
        try { await run.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // Background runs use the same durable failure owner as explicit runs.
            try { RecordUnavailable(pairId, error); }
            catch (Exception reportingError) { RunOutput.Observe(reportingError); }
            RunOutput.Observe(error);
        }
    }
}
