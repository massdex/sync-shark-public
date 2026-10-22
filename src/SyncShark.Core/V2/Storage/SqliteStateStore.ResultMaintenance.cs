namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal Action? ResultMaintenanceForTest { get; set; }
    // A committed result remains committed even if subsequent optional housekeeping fails.
    // Fatal authority information is consumed by run completion, after results are counted.
    private Exception? _resultMaintenanceHalt;
    internal Exception? TakeResultMaintenanceHalt() => Interlocked.Exchange(ref _resultMaintenanceHalt, null);

    internal void RetryResultMaintenance() => InStore(() => TryResultMaintenance());

    private void TryResultMaintenance(string? pairId = null)
    {
        try
        {
            ResultMaintenanceForTest?.Invoke();
            CleanupStorageItems();
            // The work is bounded to one cleanup page; keep the notice while pages remain.
            if (!_storageEnabled || StoreSchema.Scalar(_connection, null, "SELECT 1 FROM storage_cleanup LIMIT 1;") is null)
            {
                var pairs = pairId is null ? Pairs().Select(pair => pair.PairId) : [pairId];
                foreach (var id in pairs)
                {
                    var noticeId = NoticeTable.Id(new Notice(id, 0, NoticeKind.Info, "", "", "store-maintenance"));
                    StoreSchema.Execute(_connection, null,
                        "UPDATE notice SET state='closed',choice='resolved' WHERE notice_id=$id AND state='open';", ("$id", noticeId));
                }
            }
        }
        catch (Exception error)
        {
            RunLog.Trace("Committed results retained; storage maintenance pending: " + error.ToString());
            if (error is StateDamagedException or EndpointAccessException ||
                error is Microsoft.Data.Sqlite.SqliteException sql && StoreSchema.IsDamaged(sql))
                Interlocked.CompareExchange(ref _resultMaintenanceHalt, error is Microsoft.Data.Sqlite.SqliteException
                    ? new StateDamagedException(error.Message, error) : error, null);
            if (pairId is null) return; // The original durable maintenance notice remains open.
            try
            {
                AddNotices([new Notice(pairId, 0, NoticeKind.Info, "",
                    "File results were saved, but storage maintenance is pending: " + error.Message +
                    ". Retry runs the remaining maintenance; the next sync retries automatically. Ignore dismisses this notice without undoing file results.",
                    "store-maintenance")]);
            }
            catch (Exception noticeError) { RunLog.Trace("Maintenance notice could not be saved: " + noticeError.ToString()); }
        }
    }
}
