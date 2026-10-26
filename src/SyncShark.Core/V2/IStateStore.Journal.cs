namespace Massdex.SyncShark.V2;

public partial interface IStateStore
{
    /// <summary>A bounded journal lookup slice, including misses. A short prefix yields store admission.
    /// The caller must request the remaining IDs again; results are not absence/deletion authority.</summary>
    IReadOnlyDictionary<string, (ItemState? Item, bool MultipleNames)> JournalObjects(
        string rootId, IReadOnlyList<string> objectIds, CancellationToken ct) =>
        objectIds.ToDictionary(id => id, id => (ByObject(rootId, id), HasMultipleNames(rootId, id)), StringComparer.Ordinal);
}
