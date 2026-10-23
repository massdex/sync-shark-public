using System.Runtime.ExceptionServices;

namespace Massdex.SyncShark;

// One bounded effect pump for first sync, ordinary sync and retained retries.
// It does not plan, acquire authority, retry effects or weaken executor fences.
internal static class SyncAuthorityOperationScheduler
{
    internal const int MaximumInFlight = 16;
    internal const int MaximumBenchmarkInFlight = 1024;
    private static readonly AsyncLocal<int?> BenchmarkInFlight = new();

    // Exercise the same production pump at higher limits without changing a
    // running application or introducing a user-facing, unmeasured default.
    // ExecutionContext propagation covers the CLI/host Task.Run path as well.
    internal static IDisposable OverrideMaximumInFlightForTest(int value)
    {
        if (value is < 1 or > MaximumBenchmarkInFlight)
            throw new ArgumentOutOfRangeException(nameof(value));
        var previous = BenchmarkInFlight.Value;
        BenchmarkInFlight.Value = value;
        return new BenchmarkScope(previous);
    }

    private sealed class BenchmarkScope(int? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            BenchmarkInFlight.Value = previous;
            _disposed = true;
        }
    }

    internal static async Task<bool> RunAsync<T, TResult>(IReadOnlyList<T> items,
        Func<T, SyncAuthorityOperationSnapshot> operation,
        Func<T, Task<TResult>> execute,
        Func<T, TResult, Task<bool>> accept,
        CancellationToken cancellationToken, int? maximumInFlight = null,
        SyncTransferSchedulingScope? adaptive = null, Func<TResult, bool>? successful = null,
        bool allowDirectoryCreates = false)
    {
        var limit = maximumInFlight ?? BenchmarkInFlight.Value ?? MaximumInFlight;
        var ceiling = BenchmarkInFlight.Value is null ? MaximumInFlight : MaximumBenchmarkInFlight;
        if (limit < 1 || limit > ceiling)
            throw new ArgumentOutOfRangeException(nameof(maximumInFlight));
        // Fixed test overrides exercise the same pump without the adaptive policy.
        if (maximumInFlight is not null || BenchmarkInFlight.Value is not null) adaptive = null;
        // Adaptive admission paces transfers; the worker count is still capped at the mode's file
        // ceiling. Hundreds of concurrent workers measured 2.7x the CPU per file of 16 and the same
        // throughput as 64: their DB writes and progress events contend rather than overlap.
        if (adaptive is not null) limit = adaptive.MaximumPending;
        // One immutable descriptor per item, derived on that item's first admission examination and
        // reused for every later capacity, barrier and active-window comparison of the same item.
        // Nothing is shared between pump invocations.
        var footprints = new SyncAuthorityOperationFootprint?[items.Count];
        var active = new List<(T Item, SyncAuthorityOperationFootprint Footprint, Task<TResult> Work,
            SyncTransferResourceController.Lease? Lease)>();
        var next = 0;
        var stopped = false;
        ExceptionDispatchInfo? failure = null;
        while (next < items.Count || active.Count != 0)
        {
            stopped |= cancellationToken.IsCancellationRequested;
            // Keep input order at barriers. In particular, never jump over a
            // directory effect to start a child against its old path authority.
            while (!stopped && next < items.Count && active.Count < limit)
            {
                try
                {
                    var item = items[next];
                    var footprint = footprints[next] ??=
                        SyncAuthorityOperationFootprint.Create(operation(item));
                    if (active.Any(entry => !SyncAuthorityOperationFootprint.Independent(footprint,
                            entry.Footprint, allowDirectoryCreates))) break;
                    var lease = adaptive?.TryAcquire(footprint.ExpectedBytes(), footprint.FileEligible);
                    if (adaptive is not null && lease is null) break;
                    next++;
                    // SQLite/native file APIs can finish their async methods
                    // synchronously. Bound the workers themselves, not merely the
                    // number of network awaits, so those calls cannot serialize the pump.
                    // The task stays active until it returns even if it frees its lease early.
                    active.Add((item, footprint, Task.Run(() =>
                    {
                        SyncTransferResourceController.Lease.Current.Value = lease;
                        return execute(item);
                    }, CancellationToken.None), lease));
                }
                catch (Exception error)
                {
                    // Malformed retained input can also fail admission itself.
                    // Preserve the same drain guarantee as an executor failure.
                    failure ??= ExceptionDispatchInfo.Capture(error);
                    stopped = true;
                }
            }
            if (active.Count == 0)
            {
                if (stopped || next >= items.Count) break;
                if (adaptive is null) break;
                await adaptive.ReportAsync().ConfigureAwait(false);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var completed = await Task.WhenAny(active.Select(entry => entry.Work)).ConfigureAwait(false);
            var index = active.FindIndex(entry => entry.Work == completed);
            var entry = active[index];
            active.RemoveAt(index);
            try
            {
                var result = await completed.ConfigureAwait(false);
                // Reduction is deliberately single-threaded: dictionaries,
                // reviewed-path checkpoints and planning invalidation stay ordered.
                if (!await accept(entry.Item, result).ConfigureAwait(false)) stopped = true;
                entry.Lease?.Complete(successful?.Invoke(result) == true);
            }
            catch (Exception error)
            {
                failure ??= ExceptionDispatchInfo.Capture(error);
                stopped = true;
            }
            finally { entry.Lease?.Dispose(); }
            if (adaptive is not null)
            {
                try { await adaptive.ReportAsync().ConfigureAwait(false); }
                catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); stopped = true; }
            }
            // On a stop/error, drain and observe every already-admitted effect.
            // Never return while one of this batch's workers can still mutate a file.
        }
        failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return !stopped;
    }

    /// <summary>
    /// The comparison every existing caller already uses: each snapshot is described exactly once and the
    /// two descriptors are then compared through the same eligibility, dependency, identity and path rules
    /// the pump uses. The descriptors are per call, so this stays a pure function of its two snapshots.
    /// </summary>
    internal static bool Independent(SyncAuthorityOperationSnapshot left,
        SyncAuthorityOperationSnapshot right, bool allowDirectoryCreates = false) =>
        SyncAuthorityOperationFootprint.Independent(SyncAuthorityOperationFootprint.Create(left),
            SyncAuthorityOperationFootprint.Create(right), allowDirectoryCreates);
}
