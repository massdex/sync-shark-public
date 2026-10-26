namespace Massdex.SyncShark.V2;

internal interface IPrepareDatabase
{
    void UpdateSmall(Prepared small);
    void UpdateInventoryCursors(IReadOnlyDictionary<string, string> cursors, string scope);
    void ClearInventoryCursors(IEnumerable<string> roots, string? scope);
    void Narrow(PairDef previous, PairDef updated, string oldScope, string newScope,
        Func<Prepared, PrepareNarrowing> prepare);
    IReadOnlyDictionary<string, PrepareMatch> MatchLookup { get; }
    IReadOnlySet<string> ReviewedKeys { get; }
    bool ContainsHeld(HeldDeletion held);
    SyncCliCarriedIgnoredData? Budget(string key);
}
