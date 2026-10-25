using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal sealed partial class FileStateTable
{
    private IEnumerable<SnapshotChanges.Change> RetainSnapshotObservations(SqliteTransaction transaction, long root,
        IEnumerable<SnapshotChanges.Change> changes)
    {
        var observations = new List<ItemState>(V2Settings.PlanningObservationBatchRows);
        foreach (var change in changes)
        {
            if (OnInventoryObserved is not null && change.Incoming is { } item)
            {
                observations.Add(item);
                if (observations.Count == V2Settings.PlanningObservationBatchRows)
                {
                    OnInventoryObserved(transaction, root, observations);
                    observations.Clear();
                }
            }
            yield return change;
        }
        if (observations.Count > 0) OnInventoryObserved?.Invoke(transaction, root, observations);
    }
}
