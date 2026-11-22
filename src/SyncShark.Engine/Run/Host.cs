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
}
