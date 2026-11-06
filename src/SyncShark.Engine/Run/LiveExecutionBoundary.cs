namespace Massdex.SyncShark.Engine.Run;

/// <summary>Forwards captured live source evidence without recapturing or granting admission itself.</summary>
internal sealed class LiveExecutionBoundary : ILiveExecutionBoundary
{
    private readonly Func<string, PairSession> _session;
    internal LiveExecutionBoundary(Func<string, PairSession> session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));
    public bool TryHoldGeneration(string pairId)
    {
        var session = _session(pairId);
        return session.Host.TryHoldGeneration(session.Registration);
    }

    public Task<T> ExecuteAsync<T>(IReadOnlyList<LiveExecutionRequest> requests,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        PairSafety.ExecuteLiveAsync(Resolve(requests), execute, ct);
    public async Task<LiveCoverage> CaptureCoverageAsync(WorkSelection selection,
        IReadOnlyList<LiveEffectScope> footprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(footprint);
        var session = _session(selection.PairId);
        selection.RequireFor(session.Pair.PairId, session.ExpectedGeneration);
        var local = await session.Host.CoverLocalAsync(selection.PairId, selection.PairGeneration, ct).ConfigureAwait(false);
        var dropbox = await session.Host.CatchUpDropboxAsync(session.Registration,
            selection.PairId, selection.PairGeneration, ct).ConfigureAwait(false);
        session.Host.RequireGeneration(session.Registration, selection.PairId, selection.PairGeneration);
        return session.Store.CaptureLiveCoverage(selection, local, dropbox);
    }

    private LiveRootRequest[] Resolve(IReadOnlyList<LiveExecutionRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var result = new LiveRootRequest[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var session = _session(request.Selection.PairId);
            request.Selection.RequireFor(session.Pair.PairId, session.ExpectedGeneration);
            result[index] = new LiveRootRequest(session, request.Selection, request.Coverage, request.Footprint);
        }
        return result;
    }
}
