namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Commands operations for the edge module.</summary>
public sealed class Commands
{
    /// <summary>Translate the registered command to its Host operation; the edge neither opens a store nor decides synchronization.</summary>
    public Task<int> ExecuteAsync(string[] args, Run.Host host, TextWriter output, CancellationToken ct) => throw new NotImplementedException();
}
