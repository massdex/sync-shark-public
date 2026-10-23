namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    private IReadOnlyList<string> SaveNarrowerPair(PairDef previous, PairDef updated) =>
        SaveNarrowerPair(_store, _run, _productRoot, previous, updated);

    internal static IReadOnlyList<string> SaveNarrowerPair(IStateStore store, SyncRun run, string productRoot,
        PairDef previous, PairDef updated)
    {
        // Retrying an optimistic conflict merges only requested additions into the latest pair.
        var exclusions = updated.Excluded.Where(path => !previous.Excluded.Contains(path,
            StringComparer.OrdinalIgnoreCase)).ToArray();
        var ignores = updated.IgnoreRules.Except(previous.IgnoreRules, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var prepared = FirstSyncChoicesState.Load(productRoot, previous.PairId, store);
            if (prepared?.Database is { } database)
            {
                try
                {
                    var oldScope = run.CompatibleInventoryScope(previous, prepared.InventoryScope);
                    var newScope = run.InventoryScope(updated);
                    if (run.CompatibleInventoryScope(previous, prepared.InventoryScope) != oldScope)
                        throw new PreparePublicationException("Inventory coverage changed while narrowing.");
                    database.Narrow(previous, updated, oldScope, newScope);
                    return attempt == 0 ? [] : ["The pair changed while narrowing; the requested additions were merged into its latest review."];
                }
                catch (PreparePublicationException)
                {
                    previous = store.Pair(previous.PairId) ?? throw new SyncRunRefusedException("The pair is no longer available.");
                    updated = previous with
                    {
                        Excluded = previous.Excluded.Concat(exclusions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        IgnoreRules = previous.IgnoreRules.Concat(ignores).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    };
                    continue;
                }
            }
            // Custom stores cannot narrow transactionally; invalidate their saved authorization.
            FirstSyncChoicesState.Clear(productRoot, previous.PairId, store);
            store.SavePair(updated);
            return prepared is null ? [] : ["The exclusions were saved. This store cannot narrow a saved review; prepare again before applying."];
        }
        throw new SyncCliReviewException("The pair changed again while narrowing. No stale setting was saved. " +
            "Retry the same exclusion or ignore command against the latest pair; the request can then be applied safely.");
    }
}
