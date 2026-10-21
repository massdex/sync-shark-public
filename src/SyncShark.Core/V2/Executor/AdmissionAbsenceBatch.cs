namespace Massdex.SyncShark.V2;

// Coalesce only workers already admitted to execution. Never cache absence across actions,
// phases or runs: every request belongs to this guard invocation immediately before its intent.
internal sealed class AdmissionAbsenceBatch
{
    private readonly object _gate = new();
    private readonly Dictionary<ISourceAbsenceProbe, List<Request>> _pending = new();
    private sealed record Request(ItemState Historical, string Path, TaskCompletionSource<bool?> Result);

    internal Task<bool?> ProbeAsync(ISourceAbsenceProbe probe, ItemState historical, string path, CancellationToken ct)
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
        if (ready is not null) _ = FlushAsync(probe, ready, ct);
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
        await FlushAsync(probe, page, ct).ConfigureAwait(false);
    }

    private static async Task FlushAsync(ISourceAbsenceProbe probe, List<Request> page, CancellationToken ct)
    {
        try
        {
            var results = await probe.IsAbsentBatchAsync(page.Select(x => (x.Historical, x.Path)).ToArray(), ct)
                .ConfigureAwait(false);
            // Malformed evidence is inconclusive for the entire page, never inferred absence.
            for (var i = 0; i < page.Count; i++)
                page[i].Result.TrySetResult(results.Count == page.Count ? results[i] : null);
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
