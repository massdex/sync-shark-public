namespace Massdex.SyncShark.V2;

/// <summary>Receipt-driven settlement: no upload API exists here, so a conversion retry cannot upload twice.</summary>
internal sealed class FilesOnDemandSettlement(SqliteStateStore store,
    Func<StorageSettlement, CancellationToken, Task<StorageObserved>> convert,
    Action<StorageSettlement, Exception> reportFailure)
{
    // One bounded page per scheduling turn; the caller persists the returned cursor or restarts from zero.
    internal async Task<long> RunPageAsync(long after, CancellationToken ct)
    {
        var page = store.StorageSettlementPage(after, 128);
        foreach (var job in page)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var observed = await convert(job, ct).ConfigureAwait(false);
                if (!observed.InSync || observed.Availability != nameof(SyncCloudFileAvailability.OnlineOnly))
                    throw new IOException("Conversion was not confirmed online-only and in-sync. Keep the local file; Retry conversion or Ignore.");
                store.CompleteStorageSettlement(job, observed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { reportFailure(job, error); }
        }
        return page.Count == 0 ? 0 : page[^1].JobId;
    }
}
