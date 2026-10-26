namespace Massdex.SyncShark.V2;

/// <summary>Dispatches native batching or the bounded per-item fallback, never inferring absence.</summary>
internal static class SourceAbsenceBatch
{
    internal static async Task<IReadOnlyList<AbsenceObservation>> ObserveAsync(ISourceAbsenceProbe probe,
        IReadOnlyList<(ItemState Historical, string Path)> requests, CancellationToken ct,
        Func<Exception, bool> isStateDamage)
    {
        ArgumentNullException.ThrowIfNull(isStateDamage);
        var native = await probe.IsAbsentBatchAsync(requests, ct).ConfigureAwait(false);
        if (!ReferenceEquals(native, ISourceAbsenceProbe.NoNativeBatch)) return native;
        var results = new AbsenceObservation[requests.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, requests.Count), new ParallelOptions
        { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (index, token) =>
        {
            try { results[index] = new(await probe.IsSourceAbsentAsync(requests[index].Historical, requests[index].Path, token).ConfigureAwait(false), null); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, isStateDamage) == FailureRetryOwner.FailureScope.Item) { results[index] = new(null, AbsenceObservation.Cause(error)); }
        }).ConfigureAwait(false);
        return results;
    }
}
