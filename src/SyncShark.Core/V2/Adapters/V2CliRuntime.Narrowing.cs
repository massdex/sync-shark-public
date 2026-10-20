namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    private IReadOnlyList<string> SaveNarrowerPair(PairDef previous, PairDef updated)
    {
        // Retrying an optimistic conflict merges only the requested additions into the latest pair;
        // it never overwrites an unrelated settings change made while this operation was waiting.
        var exclusions = updated.Excluded.Where(path => !previous.Excluded.Contains(path,
            StringComparer.OrdinalIgnoreCase)).ToArray();
        var ignores = updated.IgnoreRules.Except(previous.IgnoreRules, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var prepared = FirstSyncChoicesState.Load(_productRoot, previous.PairId, _store);
            if (prepared?.Database is { } database)
            {
                try
                {
                    var oldScope = _run.CompatibleInventoryScope(previous, prepared.InventoryScope);
                    var newScope = _run.InventoryScope(updated);
                    // Other pairs' roots participate in coverage. Never turn their concurrent scope
                    // change into a valid cursor rebind merely because this pair itself is unchanged.
                    if (_run.CompatibleInventoryScope(previous, prepared.InventoryScope) != oldScope)
                        throw new PreparePublicationException("Inventory coverage changed while narrowing.");
                    database.Narrow(previous, updated, oldScope, newScope);
                    return attempt == 0 ? [] : ["The pair changed while narrowing; the requested additions were merged into its latest review."];
                }
                catch (PreparePublicationException)
                {
                    previous = RequirePair(previous.PairId);
                    updated = previous with
                    {
                        Excluded = previous.Excluded.Concat(exclusions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        IgnoreRules = previous.IgnoreRules.Concat(ignores).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    };
                    continue;
                }
            }
            // Custom non-SQLite stores have no transactional row filtering capability. Accept the
            // setting, invalidate authorization and make the fallback visible in the result too.
            FirstSyncChoicesState.Clear(_productRoot, previous.PairId, _store);
            _store.SavePair(updated);
            return prepared is null ? [] : ["The exclusions were saved. This store cannot narrow a saved review; prepare again before applying."];
        }
        throw new SyncCliReviewException("The pair changed again while narrowing. No stale setting was saved. " +
            "Retry the same exclusion or ignore command against the latest pair; the request can then be applied safely.");
    }
}
