namespace Massdex.SyncShark.Engine.Edge;

/// <summary>HostServer operations for the edge module.</summary>
public sealed class HostServer
{
    /// <summary>Claims the pipe before opening a store; a second starter attaches rather than taking ownership. Keep the claim until shutdown drains.</summary>
    public Task<bool> ClaimAsync(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Serves the already-claimed pipe while the finite startup recovery pass and other pairs proceed.</summary>
    public Task ServeAsync(Run.Host host, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Verifies the platform peer on the established pipe before exchanging a frame.</summary>
    public static bool Trusted(System.IO.Pipes.PipeStream pipe) => throw new NotImplementedException();

}
