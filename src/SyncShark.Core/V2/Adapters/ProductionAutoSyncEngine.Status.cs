namespace Massdex.SyncShark.V2;

public sealed partial class ProductionAutoSyncEngine
{
    private StatusCache _statusCache = StatusCache.Empty;

    /// <summary>One pair's store-sourced status fields, cached while the gate is unavailable.</summary>
    internal sealed record PairStatus(string PairId, string? Name, bool Enabled, RunSummary? LastResult,
        long? UrgentPending, int? PlanningBacklog, bool StoreKnown)
    {
        internal static PairStatus FromStore(PairDef pair, RunSummary? last, long urgent, int backlog) =>
            new(pair.PairId, pair.Name, pair.Enabled, last, urgent, backlog, StoreKnown: true);

        /// <summary>A pair the runtime already knows from memory, whose store fields are not available.</summary>
        internal static PairStatus FromMemory(string pairId) =>
            new(pairId, null, false, null, null, null, StoreKnown: false);
    }

    /// <summary>The last successful store projection, so status can answer while another owner holds the gate.</summary>
    internal sealed record StatusCache(IReadOnlyList<PairStatus> Pairs, DateTimeOffset AsOf, int? OpenConflicts = null)
    {
        internal static StatusCache Empty { get; } = new([], DateTimeOffset.MinValue);

        /// <summary>The pair identities already read at startup; their retained fields are not read yet.</summary>
        internal static StatusCache Seeded(IReadOnlyList<PairDef> pairs, DateTimeOffset asOf) => new(
            pairs.Select(pair => new PairStatus(pair.PairId, pair.Name, pair.Enabled, null, null, null,
                StoreKnown: false)).ToArray(), asOf);
    }

    /// <summary>
    /// Composes status from memory first (states, live progress, automation and the cached pair list), then one bounded
    /// store read for the retained fields. While another owner holds the store gate, or the read runs past its
    /// deadline, the cached rows answer with <c>storeStale</c>, so a status read never queues behind a run.
    /// </summary>
    internal static (object Status, StatusCache Cache) ReadStatus(IReadOnlyList<PairAutoState> states,
        IReadOnlyDictionary<string, LiveProgress> progress, bool automationEnabled, int intervalMinutes,
        IStateStore store, V2Settings settings, Func<string, IntakeRetirement?> intakeRetired, TimeProvider time,
        StatusCache cached)
    {
        var byPair = states.ToDictionary(state => state.PairId, StringComparer.Ordinal);
        var (pairs, cache, stale) = ReadStoreStatus(store, settings, time, cached);
        // A pair the runtime is already driving stays visible even when the projection that would name it is stale.
        if (stale) pairs = KeepMemoryPairs(pairs, states, progress);
        var status = new
        {
            running = true, automationEnabled, intervalMinutes, storeStale = stale, storeAsOf = cache.AsOf,
            openConflicts = cache.OpenConflicts,
            pairs = pairs.Select(pair => Pair(pair, byPair.GetValueOrDefault(pair.PairId),
                progress.GetValueOrDefault(pair.PairId), intakeRetired(pair.PairId), stale)).ToArray()
        };
        return (status, cache);
    }

    private static (IReadOnlyList<PairStatus> Pairs, StatusCache Cache, bool Stale) ReadStoreStatus(
        IStateStore store, V2Settings settings, TimeProvider time, StatusCache cached)
    {
        using var deadline = new CancellationTokenSource(settings.StatusStoreTimeout);
        try
        {
            StatusCache snapshot;
            using (StoreAdmission.WithCancellation(deadline.Token))
            {
                // SQLite reads abort a running statement at the deadline through the storage owner.
                snapshot = store is SqliteStateStore sql
                    ? sql.BoundedRead(deadline.Token, token => Project(sql, token, time))
                    : Project(store, deadline.Token, time);
            }
            // A projection the deadline already overtook is never published as fresh cache.
            deadline.Token.ThrowIfCancellationRequested();
            return (snapshot.Pairs, snapshot, false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return (cached.Pairs, cached, true);
        }
    }

    // One owner for the retained projection; the token is checked between its bounded store calls.
    private static StatusCache Project(IStateStore store, CancellationToken token, TimeProvider time)
    {
        var pairs = store.Pairs();
        token.ThrowIfCancellationRequested();
        var rows = new List<PairStatus>(pairs.Count);
        foreach (var pair in pairs)
        {
            token.ThrowIfCancellationRequested();
            var last = store.ReadRunHistory(pair.PairId, 1).FirstOrDefault();
            token.ThrowIfCancellationRequested();
            var urgent = store is SqliteStateStore sql ? sql.UrgentPending(pair.PairId) : 0;
            token.ThrowIfCancellationRequested();
            var backlog = store is SqliteStateStore planning ? planning.CountDeferredPlanning(pair.PairId) : 0;
            token.ThrowIfCancellationRequested();
            rows.Add(PairStatus.FromStore(pair, last, urgent, backlog));
        }
        var conflicts = store is SqliteStateStore notices ? notices.OpenConflictCount() : 0;
        token.ThrowIfCancellationRequested();
        return new(rows, time.GetUtcNow(), conflicts);
    }

    // Pairs the runtime already drives in memory are never dropped by a cache that predates them.
    private static IReadOnlyList<PairStatus> KeepMemoryPairs(IReadOnlyList<PairStatus> cached,
        IReadOnlyList<PairAutoState> states, IReadOnlyDictionary<string, LiveProgress> progress)
    {
        var known = cached.Select(pair => pair.PairId).ToHashSet(StringComparer.Ordinal);
        return cached.Concat(states.Select(state => state.PairId).Concat(progress.Keys)
            .Where(pairId => !known.Contains(pairId)).Distinct(StringComparer.Ordinal)
            .Select(PairStatus.FromMemory)).ToArray();
    }

    private static object Pair(PairStatus pair, PairAutoState? state, LiveProgress? progress,
        IntakeRetirement? intake, bool storeStale) => new
    {
        pair.PairId, pair.Name, pair.Enabled,
        // The cached row can be old, and a memory-only pair has no retained fields at all.
        storeStale = storeStale || !pair.StoreKnown,
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
