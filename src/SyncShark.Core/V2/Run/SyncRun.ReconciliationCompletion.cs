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
                active.InitialGapItems[root.RootId] = ((SqliteStateStore)_store).GapItemCount(root.RootId, epoch.Epoch);
                await InvalidateReconciliationAsync(pair, root, side, epoch, throttle, ct);
            }
            await SelectAsync(pair, side, active.Decision, active.Session, throttle, ct,
                basis => active.ListingBases[root.RootId] = basis,
                () => active.CompletedSelectionRoots.Add(root.RootId));
        }
    }

    private bool RootScanRetryVetoesCompletion(PairDef pair)
    {
        var root = new[] { pair.A, pair.B }.FirstOrDefault(candidate =>
            ScanRetriesForRoot(pair, candidate).Any(retry => retry.Kind == "scan" && retry.Path.Length == 0));
        if (root is null) return false;
        RunTrace.Emit(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("reconciliation-root-retry"), root.RootId == pair.A.RootId ? Side.A : Side.B));
        return true;
    }

    private void CompleteReconciliationGap(PairDef pair, RootDef root, string epoch, ActiveReconciliation active, CancellationToken ct)
    {
        if (RootScanRetryVetoesCompletion(pair)) return;
        var sql = (SqliteStateStore)_store;
        var notices = _store.OpenNotices(pair.PairId);
        var folders = ScanRetriesForRoot(pair, root).Where(retry => retry.Kind == "scan" && retry.Path.Length > 0 &&
            notices.Any(notice => notice.ReasonKey == "sync-retry" && PairPath.Same(notice.Path, retry.Path)))
            .Select(retry => retry.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var transferred = sql.TransferGapItemsToRetries(root.RootId, epoch, folders, ct);
        var remaining = sql.GapItemCount(root.RootId, epoch);
        var acknowledged = Math.Max(default(long), active.InitialGapItems.GetValueOrDefault(root.RootId) - transferred - remaining);
        var updated = sql.CompleteGapInventory(root.RootId, epoch);
        RunTrace.Emit(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("gap-completion-result"), acknowledged, transferred, updated, remaining));
    }

    private Task<RunReport> CompleteReconciliationAsync(PairDef pair, ActiveReconciliation active,
        RunReport report, CancellationToken ct) => _admission.WithAsync(pair.PairId, () =>
    {
        ct.ThrowIfCancellationRequested();
        var sql = (SqliteStateStore)_store;
        var completion = new CompletionSteps(this, pair.PairId, ct);
        var scope = active.Decision.UnknownScope ?? ReconciliationScope.Root;
        var unreadablePaths = ReconciliationSides(pair, active.Decision.Trigger).Select(side => side == Side.A ? pair.A : pair.B)
            .Where(root => active.Decision.RootId is null || active.Decision.RootId == root.RootId)
            .SelectMany(root => ScanRetriesForRoot(pair, root)).Where(retry => retry.Kind == "scan")
            .Select(retry => retry.Path).Where(path => scope.Contains(path) || scope.ReadScopes.Any(child => PairPath.IsSelfOrUnder(child, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var unreadable = unreadablePaths.Length;
        // Readable remainder completes; unreadable subtrees retain their scan retries and planning exclusion.
        if ((active.Decision.UnknownScope ?? ReconciliationScope.Root).ReadScopes.Contains(string.Empty))
        {
            foreach (var (rootId, epoch) in active.GapEpochs)
                if (active.CompletedSelectionRoots.Contains(rootId))
                    completion.Step(() => CompleteReconciliationGap(pair, rootId == pair.A.RootId ? pair.A : pair.B, epoch, active, ct));
            if (active.Decision.Trigger == ReconciliationTrigger.DropboxReset)
                foreach (var (rootId, basis) in active.ListingBases.Where(entry => active.CompletedSelectionRoots.Contains(entry.Key)))
                    completion.Step(() =>
                    {
                        _store.CompleteGeneration(_store.BeginGeneration(rootId, "delta"), basis, true);
                        lock (_stopGate) _pendingResets.Remove((pair.PairId, rootId));
                    });
        }
        if (unreadable > 0) RunTrace.Emit($"reconciliation completion checked; scan retries retain unreadable={unreadable}");
        report = report with { Deferred = Math.Max(report.Deferred, unreadable) };
        completion.Step(_store.Prune);
        completion.Step(() => sql.ClearRemovalObservation(active.Session + ":work"));
        return Task.FromResult(completion.Finish(report));
    }, ct);
}
