namespace Massdex.SyncShark.Engine.Run;

/// <summary>Status operations for the run module.</summary>
public sealed class Status
{
    /// <summary>Read the pair’s stored last-Repair summary, including after restart.</summary>
    public Task<string> ReadAsync(IStore store, string? pairId, CancellationToken ct) => throw new NotImplementedException();
}
