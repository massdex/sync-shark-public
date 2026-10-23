namespace Massdex.SyncShark.V2;

// The `pairs rescan` verb (core v2 only): it forgets a pair's stored scan cursors, so the pair's next run
// re-reads those roots in full. Baselines, live rows and tombstones are left untouched, because a full scan
// reports no journal deletes and can therefore prove a deletion only from the baseline it already holds.
public sealed partial class V2CliRuntime
{
    // The --side vocabulary, owned here so the registry's usage text and this mapping name the same three words.
    internal const string RescanSideA = "a";
    internal const string RescanSideB = "b";
    internal const string RescanSideBoth = "both";

    /// <inheritdoc />
    public async Task<SyncCliRescanData> RescanPairAsync(string pairId, string? side,
        CancellationToken cancellationToken)
    {
        var pair = RequirePair(pairId);
        var roots = SideRoots(pair, side);
        // Preserve the immediate rescan response, scoped to this pair rather than unrelated runs.
        try
        {
            return await _run.WithPairAdmissionAsync(pairId, async () =>
            {
                // The reset goes through SyncRun's own gate, so a run started elsewhere (continuous sync) is never
                // reset out from under, and this rescan is never silently lost to that run's cursor write-back.
                var rootIds = roots.Select(root => root.RootId).ToArray();
                // Pending prepare checkpoints are owned by this adapter, not SyncRun's root store. Clear them
                // under the same pair lease as prepare/apply, so resume cannot bypass the requested full scan.
                // An unreadable prepare supplies no checkpoint and authorizes nothing, and a rescan is already the
                // full re-read that prepare would ask for, so discard it here instead of refusing an unrelated command.
                var warnings = new List<string>();
                string? unreadable = null;
                FirstSyncChoicesState.ClearPendingInventoryCursors(_productRoot, pairId, rootIds, _store,
                    reason => unreadable = reason);
                if (unreadable is not null)
                {
                    FirstSyncChoicesState.Clear(_productRoot, pairId, _store);
                    warnings.Add("Discarded unreadable first-sync prepare (" + unreadable +
                        "); the requested full scan proceeds.");
                }
                await _run.RequestRescanAsync(pairId, rootIds, cancellationToken);
                return new SyncCliRescanData(pair.PairId, pair.Name,
                    roots.Select(root => new SyncCliRescanRootData(SideName(root, pair), root.RootPath)).ToArray())
                {
                    Warnings = warnings
                };
            }, cancellationToken, Console.Error.WriteLine, immediate: true);
        }
        catch (SyncCliBusyException)
        {
            throw new SyncCliBusyException(
                "A sync is already running, so no cursor was reset. Stop it and run the command again.");
        }
    }

    // The three words the usage text offers, in one place: a and b name one side each, both (the default) names both.
    private static IReadOnlyList<RootDef> SideRoots(PairDef pair, string? side) => side switch
    {
        null or RescanSideBoth => [pair.A, pair.B],
        RescanSideA => [pair.A],
        RescanSideB => [pair.B],
        _ => throw new SyncCliUsageException("The option --side requires a, b or both.")
    };

    private static string SideName(RootDef root, PairDef pair) =>
        string.Equals(root.RootId, pair.A.RootId, StringComparison.Ordinal) ? RescanSideA : RescanSideB;
}
