namespace Massdex.SyncShark.V2;

public partial interface IStateStore
{
    /// <summary>Merges background scan debt without overwriting concurrent reviewed retry authority.</summary>
    void UpdateSyncRetry(string pairId, string path, Func<SyncRetry?, SyncRetry?> update);

    /// <summary>
    /// The earliest persisted retry deadline that still schedules work, or null when none remains. Parked
    /// user decisions at MaxValue are not scheduling debt. Persistent stores seek the deadline index; the
    /// default keeps other adapters correct from the existing stream.
    /// </summary>
    DateTimeOffset? EarliestSyncRetryAfter(string pairId) => ReadSyncRetries(pairId)
        .Where(retry => retry.RetryAfter < DateTimeOffset.MaxValue)
        .Select(retry => (DateTimeOffset?)retry.RetryAfter)
        .Min();
}
