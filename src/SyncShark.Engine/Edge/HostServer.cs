namespace Massdex.SyncShark.Engine.Edge;

/// <summary>HostServer operations for the edge module.</summary>
public sealed class HostServer
{
    /// <summary>Serve operation on the supplied facts.</summary>
    public Task ServeAsync(Run.Host host, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Verifies the platform peer on the established pipe before exchanging a frame.</summary>
    public static bool Trusted(System.IO.Pipes.PipeStream pipe) => throw new NotImplementedException();

}
