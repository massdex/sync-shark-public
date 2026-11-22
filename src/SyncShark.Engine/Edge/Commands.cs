namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Commands operations for the edge module.</summary>
public sealed class Commands
{
    /// <summary>Translate the registered command to its Host operation; render the returned recovery place in the exclusion preview without probing the drive.</summary>
    public Task<int> ExecuteAsync(string[] args, Run.Host host, TextWriter output, CancellationToken ct) => throw new NotImplementedException();
}
