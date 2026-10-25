namespace Massdex.SyncShark.V2;

// Coalesce only workers already admitted to execution. Never cache absence across actions,
// phases or runs: every request belongs to this guard invocation immediately before its intent.
internal sealed class AdmissionAbsenceBatch
{
    private readonly Func<Exception, bool> _isStateDamage;

    internal AdmissionAbsenceBatch(Func<Exception, bool> isStateDamage)
    {
        ArgumentNullException.ThrowIfNull(isStateDamage);
        _isStateDamage = isStateDamage;
    }

    private readonly object _gate = new();
    private readonly Dictionary<ISourceAbsenceProbe, List<Request>> _pending = new();
    private sealed record Request(ItemState Historical, string Path, TaskCompletionSource<AbsenceObservation> Result);

    internal Task<AbsenceObservation> ProbeAsync(ISourceAbsenceProbe probe, ItemState historical, string path, CancellationToken ct)
    {
        var request = new Request(historical, path, new(TaskCreationOptions.RunContinuationsAsynchronously));
        List<Request>? ready = null;
        lock (_gate)
        {
            if (!_pending.TryGetValue(probe, out var page))
            {
                page = [];
                _pending.Add(probe, page);
                _ = FlushAfterDelayAsync(probe, page, ct);
            }
            page.Add(request);
            if (page.Count == 128)
            {
                _pending.Remove(probe);
                ready = page;
            }
        }
        if (ready is not null) _ = FlushAsync(probe, ready, ct, _isStateDamage);
        return request.Result.Task;
    }

    private async Task FlushAfterDelayAsync(ISourceAbsenceProbe probe, List<Request> page, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_pending.TryGetValue(probe, out var current) || !ReferenceEquals(page, current)) return;
            _pending.Remove(probe);
        }
        await FlushAsync(probe, page, ct, _isStateDamage).ConfigureAwait(false);
    }

    private static async Task FlushAsync(ISourceAbsenceProbe probe, List<Request> page, CancellationToken ct,
        Func<Exception, bool> isStateDamage)
    {
        ArgumentNullException.ThrowIfNull(isStateDamage);
        try
        {
            var results = await SourceAbsenceBatch.ObserveAsync(probe, page.Select(x => (x.Historical, x.Path)).ToArray(), ct, isStateDamage)
                .ConfigureAwait(false);
            // Malformed evidence is inconclusive for the entire page, never inferred absence.
            // Keep a reason so the blocked item's notice can say why it could not be verified.
            for (var i = 0; i < page.Count; i++)
                page[i].Result.TrySetResult(results.Count == page.Count
                    ? results[i]
                    : new(null, "the provider returned an incomplete absence result"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var request in page) request.Result.TrySetCanceled(ct);
        }
        catch (Exception error)
        {
            foreach (var request in page) request.Result.TrySetException(error);
        }
    }
}
