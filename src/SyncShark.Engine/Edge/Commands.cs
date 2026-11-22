namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Commands operations for the edge module.</summary>
public sealed class Commands
{
    /// <summary>Execute operation on the supplied facts.</summary>
    public Task<int> ExecuteAsync(string[] args, Run.Host host, TextWriter output, CancellationToken ct) => throw new NotImplementedException();
}
