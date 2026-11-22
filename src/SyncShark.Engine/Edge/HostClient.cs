namespace Massdex.SyncShark.Engine.Edge;

/// <summary>HostClient operations for the edge module.</summary>
public sealed class HostClient
{
    /// <summary>Send operation on the supplied facts.</summary>
    public Task<int> SendAsync(string[] args, TextWriter output, CancellationToken ct) => throw new NotImplementedException();
}
