namespace Massdex.SyncShark.V2;

internal interface IStoreRemovalObservation
{
    void StageRemovalItems(string session, Side side,
        IReadOnlyList<ItemState> rows, bool ordinalPaths = false);
    IEnumerable<ItemState> RemovalObservation(string session, Side side);
    void ClearRemovalObservation(string session, Exception? primaryError = null);
}
