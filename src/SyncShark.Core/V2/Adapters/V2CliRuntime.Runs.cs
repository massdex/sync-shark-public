namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    /// <inheritdoc />
    public Task<SyncCliRunListData> LoadRunsAsync(int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runs = _store.ReadRunHistory(null, limit);
        return Task.FromResult(new SyncCliRunListData(runs.Count, runs.Select(run =>
            new SyncCliRunHistoryData(run.RunId, run.Kind, run.StartedUtc, run.CompletedUtc, run.Status)).ToArray()));
    }
}
