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
        return _admission.WithAsync(pairId, async () =>
        {
            ct.ThrowIfCancellationRequested();
            lock (_stopGate) if (_reconciling.ContainsKey(pairId)) return false;
            await _endpoints.PrepareAsync(pair, ct).ConfigureAwait(false);
            var decision = SampleReconciliation(pair, sql);
            return decision is not null && RequestReconciliation(pairId, decision, progress, ct);
        }, ct);
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
            if (_store.Pair(pairId) is { } pair && ReconciliationRetriesSleeping(pair, decision))
            {
                RunTrace.Emit("reconciliation monitor waiting for existing scan retry due time");
                return false;
            }
            RunTrace.Emit(MessageCatalog.Default.Template("monitor-scope-decision") + decision.Trigger);
            // ReconcileScopeAsync registers both scope and StopAsync work before its first yield.
            _ = ObserveMonitoredReconciliationAsync(pairId, _admission.WithoutBorrowedAdmissionAsync(
                () => ReconcileScopeAsync(pairId, decision, progress, ct)));
        }
        return true;
    }

    internal bool ReconciliationRetriesSleeping(PairDef pair, StateMonitorDecision decision)
    {
        if (decision.Trigger == ReconciliationTrigger.PlanningBacklog) return false;
        var scope = decision.UnknownScope ?? ReconciliationScope.Root;
        var retries = ReconciliationSides(pair, decision.Trigger)
            .Select(side => side == Side.A ? pair.A : pair.B)
            .Where(root => decision.RootId is null || root.RootId == decision.RootId)
            .SelectMany(root => ScanRetriesForRoot(pair, root))
            .Where(retry => scope.Contains(retry.Path) || scope.ReadScopes.Any(path => PairPath.IsSelfOrUnder(path, retry.Path))).ToArray();
        return retries.Length != 0 && retries.All(retry => retry.RetryAfter > _time.GetUtcNow());
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
