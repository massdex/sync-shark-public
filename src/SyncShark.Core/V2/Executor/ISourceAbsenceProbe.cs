namespace Massdex.SyncShark.V2;

/// <summary>Fresh identity AND path absence evidence; null means absence could not be proved.</summary>
public interface ISourceAbsenceProbe
{
    /// <summary>True only when the historical identity (if supplied) and exact path are both absent.</summary>
    Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct);

    /// <summary>Bounded fresh evidence in request order; a null observation never authorizes deletion.</summary>
    async Task<IReadOnlyList<AbsenceObservation>> IsAbsentBatchAsync(
        IReadOnlyList<(ItemState Historical, string Path)> requests, CancellationToken ct)
    {
        var results = new AbsenceObservation[requests.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, requests.Count), new ParallelOptions
        { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (index, token) =>
        {
            try { results[index] = new(await IsSourceAbsentAsync(requests[index].Historical, requests[index].Path, token).ConfigureAwait(false), null); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item) { results[index] = new(null, AbsenceObservation.Cause(error)); }
        }).ConfigureAwait(false);
        return results;
    }

    /// <summary>Fresh metadata-only source observation for a planned replacement.</summary>
    Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct);

    /// <summary>True only when the same historical object still exists; null means unknown.</summary>
    Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct) => Task.FromResult<bool?>(null);
}
