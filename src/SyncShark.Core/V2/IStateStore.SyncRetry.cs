namespace Massdex.SyncShark.V2;

public partial interface IStateStore
{
    /// <summary>Merges background scan debt without overwriting concurrent reviewed retry authority.</summary>
    void UpdateSyncRetry(string pairId, string path, Func<SyncRetry?, SyncRetry?> update);
}
