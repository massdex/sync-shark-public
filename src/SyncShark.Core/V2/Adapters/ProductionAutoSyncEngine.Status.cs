namespace Massdex.SyncShark.V2;

public sealed partial class ProductionAutoSyncEngine
{
    private StatusCache _statusCache = StatusCache.Empty;

    /// <summary>One pair's store-sourced status fields, cached while the gate is unavailable.</summary>
    internal sealed record PairStatus(string PairId, string Name, bool Enabled, RunSummary? LastResult,
        long UrgentPending, int PlanningBacklog);

    /// <summary>The last successful store read, so status can answer while another owner holds the gate.</summary>
    internal sealed record StatusCache(IReadOnlyList<PairStatus> Pairs, DateTimeOffset AsOf)
    {
        internal static StatusCache Empty { get; } = new([], DateTimeOffset.MinValue);
    }

    /// <summary>
    /// Composes status from memory first (states, live progress, automation and the cached pair list), then one bounded
    /// store read for the retained fields. While another owner holds the store gate the cached rows answer with
    /// <c>storeStale</c>, so a status read never queues behind a run.
    /// </summary>
    internal static (object Status, StatusCache Cache) ReadStatus(IReadOnlyList<PairAutoState> states,
        IReadOnlyDictionary<string, LiveProgress> progress, bool automationEnabled, int intervalMinutes,
        IStateStore store, V2Settings settings, Func<string, IntakeRetirement?> intakeRetired, TimeProvider time,
        StatusCache cached)
    {
        var byPair = states.ToDictionary(state => state.PairId, StringComparer.Ordinal);
        var (pairs, cache, stale) = ReadStoreStatus(store, settings, time, cached);
        var status = new
        {
            running = true, automationEnabled, intervalMinutes, storeStale = stale, storeAsOf = cache.AsOf,
            pairs = pairs.Select(pair => Pair(pair, byPair.GetValueOrDefault(pair.PairId),
                progress.GetValueOrDefault(pair.PairId), intakeRetired(pair.PairId))).ToArray()
        };
        return (status, cache);
    }

    private static (IReadOnlyList<PairStatus> Pairs, StatusCache Cache, bool Stale) ReadStoreStatus(
        IStateStore store, V2Settings settings, TimeProvider time, StatusCache cached)
    {
        using var deadline = new CancellationTokenSource(settings.StatusStoreTimeout);
        try
        {
            IReadOnlyList<PairStatus> pairs;
            using (StoreAdmission.WithCancellation(deadline.Token))
                pairs = store.Pairs().Select(pair => new PairStatus(pair.PairId, pair.Name, pair.Enabled,
                    store.ReadRunHistory(pair.PairId, 1).FirstOrDefault(),
                    store is SqliteStateStore sql ? sql.UrgentPending(pair.PairId) : 0,
                    store is SqliteStateStore planning ? planning.CountDeferredPlanning(pair.PairId) : 0)).ToArray();
            return (pairs, new StatusCache(pairs, time.GetUtcNow()), false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return (cached.Pairs, cached, true);
        }
    }

    private static object Pair(PairStatus pair, PairAutoState? state, LiveProgress? progress,
        IntakeRetirement? intake) => new
    {
        pair.PairId, pair.Name, pair.Enabled,
        state = state?.Status.ToString() ?? "starting",
        lastResult = pair.LastResult,
        lastChecked = state?.LastReport?.CompletedUtc,
        urgentPending = pair.UrgentPending,
        planningBacklog = pair.PlanningBacklog,
        retryDeadline = state?.RetryAt,
        nextRun = state?.NextRunUtc,
        reason = state?.Reason,
        progress = progress is null ? null : new
        {
            kind = progress.Kind, stage = progress.Phase, done = progress.Done, total = progress.Total,
            item = progress.Path, eta = progress.Eta?.TotalSeconds, updatedAt = progress.UpdatedUtc
        },
        intakeRetired = new
        {
            lastBatch = (intake?.LastBatch).GetValueOrDefault(),
            total = (intake?.Total).GetValueOrDefault()
        }
    };
}
