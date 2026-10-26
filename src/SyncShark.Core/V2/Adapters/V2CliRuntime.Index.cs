namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    // The one outcome that means "nothing was started because the owner's pause state says so"; it is not a
    // run outcome, so it lives with the command rather than in RunOutcomes.
    private const string IndexHeld = "paused";

    /// <inheritdoc />
    public SyncCliIndexStatusData LoadIndexStatus()
    {
        // A store read only: the engine's status owner builds it from retained rows and probes no endpoint.
        var roots = (IndexComposition ?? throw new SyncCliUnsupportedException(
            "Indexing is available with core v2 only. Nothing was read.")).IndexStatus();
        return new SyncCliIndexStatusData(roots.Count, roots);
    }

    /// <inheritdoc />
    public async Task<SyncCliIndexRunData> RunIndexAsync(string rootId, Action<string> progress,
        CancellationToken ct)
    {
        var composition = IndexComposition ?? throw new SyncCliUnsupportedException(
            "One explicit indexing pass needs the core v2 engine, which this runtime does not provide. Nothing was indexed.");
        var pairs = _store.Pairs()
            .Where(pair => string.Equals(pair.A.RootId, rootId, StringComparison.Ordinal) ||
                           string.Equals(pair.B.RootId, rootId, StringComparison.Ordinal))
            .ToArray();
        // The engine's indexing owner accepts local roots only; name that refusal before any work begins.
        var root = pairs.SelectMany(pair => new[] { pair.A, pair.B })
            .FirstOrDefault(candidate => string.Equals(candidate.RootId, rootId, StringComparison.Ordinal) &&
                                         candidate.Provider == SyncProviders.Local)
            ?? throw new SyncCliTargetException("No local root has that exact identity.");
        // The same pause/disable authority an ordinary run obeys: a paused or disabled pair takes part in no
        // automatic or manual work. Held is informational and never resumes or enables anything.
        if (!pairs.Any(ActiveForIndexing))
        {
            progress($"Index root {rootId} paused: every pair using it is paused or disabled; resume or enable a pair first.");
            return Result(composition, rootId, IndexHeld);
        }
        // Deliberately not the runtime's pair run gate: this owner runs independently, so neither the index
        // pass nor a sync run ever waits for the other.
        progress($"Indexing local root {root.RootPath} ({rootId}) independently; no sync run waits for it.");
        var outcome = await composition.RunIndexAsync(rootId, ct).ConfigureAwait(false);
        progress($"Index root {rootId}: {outcome}; remaining verification debt " +
                 $"{LatestStatus(composition, rootId).Remaining}.");
        return Result(composition, rootId, outcome);
    }

    private bool ActiveForIndexing(PairDef pair) =>
        pair.Enabled && !_store.PairPaused(pair.PairId);

    private static SyncCliIndexRunData Result(EngineComposition composition, string rootId, string outcome)
    {
        var status = LatestStatus(composition, rootId);
        return new SyncCliIndexRunData(rootId, outcome, status.GapProtected, status.RehashPending, status.Progress)
        { Remaining = status.Remaining };
    }

    private static LocalIndexStatus LatestStatus(EngineComposition composition, string rootId) =>
        composition.IndexStatus().FirstOrDefault(row => string.Equals(row.RootId, rootId, StringComparison.Ordinal))
        ?? new LocalIndexStatus(rootId, false, string.Empty, 0);
}
