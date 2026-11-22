namespace Massdex.SyncShark.Engine.Run;

/// <summary>Host operations for the run module.</summary>
public sealed class Host
{
    /// <summary>Start operation on the supplied facts.</summary>
    public Task StartAsync(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Stop operation on the supplied facts.</summary>
    public Task StopAsync(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Pause operation on the supplied facts.</summary>
    public Task PauseAsync(string? pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Resume operation on the supplied facts.</summary>
    public Task ResumeAsync(string? pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Whether this pair may start work under the global and per-pair pause settings.</summary>
    public bool IsPaused(string pairId) => throw new NotImplementedException();

    /// <summary>The composition root supplies the store and resolves the two adapters on each configured pair.</summary>
    public Host(IStore store, Func<PairConfig,(ISide Local,ISide Dropbox)> sides, EngineSettings settings, ILogFiles logs, TimeProvider clock) => throw new NotImplementedException();

}
