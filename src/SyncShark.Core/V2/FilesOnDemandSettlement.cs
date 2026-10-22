using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

/// <summary>Receipt-driven settlement; retries never repeat uploads.</summary>
internal sealed class FilesOnDemandSettlement(SqliteStateStore store,
    Func<StorageSettlement, CancellationToken, Task<StorageObserved>> convert,
    Action<StorageSettlement, Exception> reportFailure,
    Func<StorageSettlement, bool>? eligible = null,
    Action<StorageSettlement>? completed = null)
{
    private sealed class ItemGate { internal readonly SemaphoreSlim Semaphore = new(1, 1); internal int Users; }
    private static readonly ConditionalWeakTable<SqliteStateStore, Dictionary<(string, string), ItemGate>> Gates = new();

    // Shared by background settlement, pointer refresh and explicit Retry, but never hydration.
    // Entries exist only for active/waiting work, not for every indexed item.
    internal static async Task SerializeAsync(SqliteStateStore store, StorageSettlement job,
        Func<Task> work, CancellationToken ct)
    {
        await SerializeAsync(store, job.RootId, job.ObjectId, work, ct).ConfigureAwait(false);
    }

    internal static async Task SerializeAsync(SqliteStateStore store, string rootId, string objectId,
        Func<Task> work, CancellationToken ct)
    {
        var gates = Gates.GetOrCreateValue(store);
        var key = (rootId, objectId);
        ItemGate gate;
        lock (gates)
        {
            if (!gates.TryGetValue(key, out gate!)) gates.Add(key, gate = new());
            gate.Users++;
        }
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            await work().ConfigureAwait(false);
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            lock (gates)
                if (--gate.Users == 0) { gates.Remove(key); gate.Semaphore.Dispose(); }
        }
    }

    internal Task RunItemAsync(StorageSettlement job, CancellationToken ct) => SerializeAsync(store, job, async () =>
    {
        ct.ThrowIfCancellationRequested();
        if (!store.IsCurrentStorageSettlement(job)) return;
        try
        {
            if (eligible is not null && !eligible(job)) return;
            var observed = await convert(job, ct).ConfigureAwait(false);
            if (!observed.InSync || observed.Availability != nameof(SyncCloudFileAvailability.OnlineOnly))
                throw new IOException("Conversion was not confirmed online-only and in-sync. Keep the local file; Retry conversion or Ignore.");
            if (store.CompleteStorageSettlement(job, observed)) completed?.Invoke(job);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { reportFailure(job, error); }
    }, ct);

    internal async Task<long> RunPageAsync(long after, CancellationToken ct,
        IReadOnlyCollection<string>? activeRoots = null)
    {
        var page = store.StorageSettlementPage(after, 128, activeRoots);
        await Parallel.ForEachAsync(page,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (job, token) => await RunItemAsync(job, token).ConfigureAwait(false)).ConfigureAwait(false);
        return page.Count == 0 ? 0 : page[^1].JobId;
    }
}
