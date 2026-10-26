namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task SelectReconciliationAsync(PairDef pair, ActiveReconciliation active,
        ProgressThrottle throttle, CancellationToken ct)
    {
        foreach (var side in ReconciliationSides(pair, active.Decision.Trigger))
        {
            var root = side == Side.A ? pair.A : pair.B;
            if (active.Decision.RootId is { } rootId && root.RootId != rootId) continue;
            if ((active.Decision.Trigger is ReconciliationTrigger.GapSameJournal or
                ReconciliationTrigger.JournalReplaced or ReconciliationTrigger.JournalPositionInvalid) &&
                _store is ILocalGapStore gaps && gaps.GapEpoch(root.RootId) is { } epoch)
            {
                active.GapEpochs[root.RootId] = epoch.Epoch;
                await InvalidateReconciliationAsync(pair, root, side, epoch, ct);
            }
            await SelectAsync(pair, side, active.Decision, active.Session, throttle, ct,
                basis => active.ListingBases[root.RootId] = basis,
                () => active.CompletedSelectionRoots.Add(root.RootId));
        }
    }

    private Task<RunReport> CompleteReconciliationAsync(PairDef pair, ActiveReconciliation active,
        RunReport report, CancellationToken ct) => _admission.WithAsync(pair.PairId, () =>
    {
        ct.ThrowIfCancellationRequested();
        var sql = (SqliteStateStore)_store;
        var completion = new CompletionSteps(this, pair.PairId, ct);
        var unreadable = new[] { pair.A, pair.B }.SelectMany(root => ScanRetriesForRoot(pair, root))
            .Select(retry => retry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        // Readable remainder completes; unreadable subtrees retain their scan retries and planning exclusion.
        if ((active.Decision.UnknownScope ?? ReconciliationScope.Root).ReadScopes.Contains(string.Empty))
        {
            foreach (var (rootId, epoch) in active.GapEpochs)
                if (active.CompletedSelectionRoots.Contains(rootId))
                    completion.Step(() => sql.CompleteGapInventory(rootId, epoch));
            if (active.Decision.Trigger == ReconciliationTrigger.DropboxReset)
                foreach (var (rootId, basis) in active.ListingBases.Where(entry => active.CompletedSelectionRoots.Contains(entry.Key)))
                    completion.Step(() =>
                    {
                        _store.CompleteGeneration(_store.BeginGeneration(rootId, "delta"), basis, true);
                        lock (_stopGate) _pendingResets.Remove((pair.PairId, rootId));
                    });
        }
        if (unreadable > 0) RunTrace.Emit($"reconciliation readable remainder complete; scan retries retain unreadable={unreadable}");
        report = report with { Deferred = Math.Max(report.Deferred, unreadable) };
        completion.Step(_store.Prune);
        completion.Step(() => sql.ClearRemovalObservation(active.Session + ":work"));
        return Task.FromResult(completion.Finish(report));
    }, ct);
}
