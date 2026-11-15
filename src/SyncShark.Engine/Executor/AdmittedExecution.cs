namespace Massdex.SyncShark.Engine.Executor;

/// <summary>The shared execution-admission owner (LIVE 3): it acquires one permit per distinct account controller in
/// the existing canonical order, then the union of the hierarchical path-lock keys of every endpoint's complete
/// footprint, and returns the ephemeral lease for exactly one admitted callback. It is the only admission path;
/// root checks, selection, coverage and per-item freshness keep their own owners.</summary>
internal static class AdmittedExecution
{
    // One admission acquisition: permits and the footprint lock follow one canonical order and one release-on-failure catch.
    /// <summary>Acquires the canonical permits and the union footprint for the exact requests, releasing every
    /// acquired resource when a later acquisition or cancellation fails; the returned lease is disposed once.</summary>
    internal static async Task<IAdmittedExecution> AcquireAsync(
        IReadOnlyList<ExecutionRequest> requests, Func<bool> acceptGrant, CancellationToken ct,
        Action? revalidate = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(acceptGrant);
        var controllers = new SortedDictionary<string, (IAdmission Controller, bool Priority)>(StringComparer.Ordinal);
        var identities = new Dictionary<IAdmission, string>(ReferenceEqualityComparer.Instance);
        var footprint = new List<PathLockScope>();
        foreach (var request in requests)
        {
            ArgumentException.ThrowIfNullOrEmpty(request.AccountOrderKey);
            if (identities.TryGetValue(request.Admission, out var key) && key != request.AccountOrderKey)
                throw new ArgumentException(null, nameof(requests));
            identities[request.Admission] = request.AccountOrderKey;
            if (controllers.TryGetValue(request.AccountOrderKey, out var prior))
            {
                if (!ReferenceEquals(prior.Controller, request.Admission))
                    throw new ArgumentException(null, nameof(requests));
                controllers[request.AccountOrderKey] = (prior.Controller, prior.Priority || request.Priority);
            }
            else controllers.Add(request.AccountOrderKey, (request.Admission, request.Priority));
            footprint.AddRange(request.Footprint);
        }
        var permits = new List<IAsyncDisposable>(controllers.Count);
        IAsyncDisposable? locks = null;
        try
        {
            foreach (var (controller, priority) in controllers.Values)
            {
                permits.Add(await controller.AdmitAsync(priority, ct).ConfigureAwait(false));
                RequireGrant();
            }
            locks = await PathLocks.Shared.EnterAsync(footprint, ct).ConfigureAwait(false);
            RequireGrant();
            return new Lease(permits, locks, requests, revalidate);
        }
        catch
        {
            try
            {
                if (locks is not null) await locks.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                for (var i = permits.Count - 1; i >= 0; i--)
                    await permits[i].DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }

        void RequireGrant()
        {
            if (acceptGrant()) return;
            ct.ThrowIfCancellationRequested();
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        }
    }

    /// <summary>The ephemeral lease for one admitted callback: it validates the admitted pair, generation and lock
    /// keys, never widens the footprint, and is disposed exactly once.</summary>
    private sealed class Lease(
        IReadOnlyList<IAsyncDisposable> permits, IAsyncDisposable locks, IReadOnlyList<ExecutionRequest> requests,
        Action? revalidate)
        : IAdmittedExecution
    {
        private int disposed;
        private readonly object releaseGate = new();
        private Task? admissionRelease;

        /// <summary>Verifies the pair, its generation and every required lock key against the admitted footprint.</summary>
        public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys) =>
            RequireScopes(pairId, generation, requiredLockKeys.Select(key => new PathLockScope(key, false)).ToArray());

        public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var admitted = false;
            foreach (var request in requests)
            {
                if (!string.Equals(request.PairId, pairId, StringComparison.Ordinal)) continue;
                if (request.PairGeneration != generation)
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
                admitted = true;
                foreach (var scope in scopes)
                    if (!request.Footprint.Any(held => PathLocks.Covers(held, scope)))
                        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
            }
            if (!admitted) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        }

        /// <summary>Re-runs this lease's own admission validation; only a live selection is asked to.</summary>
        public void Revalidate() => revalidate?.Invoke();

        public ValueTask ReleaseAdmissionAsync()
        {
            lock (releaseGate) return new ValueTask(admissionRelease ??= ReleasePermitsAsync());
        }

        private async Task ReleasePermitsAsync()
        {
            List<Exception>? failures = null;
            for (var i = permits.Count - 1; i >= 0; i--)
            {
                try { await permits[i].DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null) throw new AggregateException(failures);
        }

        /// <summary>Releases the footprint and any remaining account permits once.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 0 + 1) != 0) return;
            try { await locks.DisposeAsync().ConfigureAwait(false); }
            finally { await ReleaseAdmissionAsync().ConfigureAwait(false); }
        }
    }
}
