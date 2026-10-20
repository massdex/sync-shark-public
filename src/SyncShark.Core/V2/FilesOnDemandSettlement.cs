namespace Massdex.SyncShark.V2;

/// <summary>Receipt-driven settlement: no upload API exists here, so a conversion retry cannot upload twice.</summary>
internal sealed class FilesOnDemandSettlement(SqliteStateStore store,
    Func<StorageSettlement, CancellationToken, Task<StorageObserved>> convert,
    Action<StorageSettlement, Exception> reportFailure,
    Func<StorageSettlement, bool>? eligible = null)
{
    // One bounded page per scheduling turn; the caller persists the returned cursor or restarts from zero.
    internal async Task<long> RunPageAsync(long after, CancellationToken ct)
    {
        var page = store.StorageSettlementPage(after, 128);
        // Different items proceed concurrently; multiple bindings of one item never race each other.
        await Parallel.ForEachAsync(page.GroupBy(job => (job.RootId, job.ObjectId)),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (jobs, token) =>
        {
            foreach (var job in jobs)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (eligible is not null && !eligible(job)) continue;
                    var observed = await convert(job, token).ConfigureAwait(false);
                    if (!observed.InSync || observed.Availability != nameof(SyncCloudFileAvailability.OnlineOnly))
                        throw new IOException("Conversion was not confirmed online-only and in-sync. Keep the local file; Retry conversion or Ignore.");
                    store.CompleteStorageSettlement(job, observed);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { reportFailure(job, error); }
            }
        }).ConfigureAwait(false);
        return page.Count == 0 ? 0 : page[^1].JobId;
    }
}
