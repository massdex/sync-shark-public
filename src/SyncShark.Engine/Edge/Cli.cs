namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Cli operations for the edge module.</summary>
public static class Cli
{
    /// <summary>Use ResourceNames.LogDirectory for detached log reads. Confirmed host absence renders HostNotRunning for a non-log read; never start the host on a read.</summary>
    public static Task<int> RunAsync(string[] args, ILogFiles files, TextWriter output, TextWriter error, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Help operation on the supplied facts.</summary>
    public static IReadOnlyList<string> Help() => throw new NotImplementedException();
}
