namespace Massdex.SyncShark.Engine.Edge;

/// <summary>HostClient operations for the edge module.</summary>
public sealed class HostClient
{
    /// <summary>Send operation on the supplied facts.</summary>
    public Task<int> SendAsync(string[] args, TextWriter output, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Send the command; host stop can wait for the authenticated host process to exit.</summary>
    public Task<(int? ExitCode, bool ReceivedExit)> SendAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct, bool waitForHostExit = false) => throw new NotImplementedException();
    /// <summary>Start the host and await readiness.</summary>
    public static Task<int> StartAsync(CancellationToken ct) => throw new NotImplementedException();
}
