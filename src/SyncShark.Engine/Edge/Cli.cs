namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Cli operations for the edge module.</summary>
public static class Cli
{
    /// <summary>Use ResourceNames.LogDirectory for detached log reads. Confirmed host absence renders HostNotRunning for a non-log read; never start the host on a read.</summary>
    public static Task<int> RunAsync(string[] args, ILogFiles files, TextWriter output, TextWriter error, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Help operation on the supplied facts.</summary>
    public static IReadOnlyList<string> Help() => throw new NotImplementedException();

    /// <summary>Parse for the selected Cli operation.</summary>
    public static CliInvocation? Parse(string[] args) => throw new NotImplementedException();
    /// <summary>RenderHostAbsent for the selected Cli operation.</summary>
    public static int RenderHostAbsent(TextWriter output) => throw new NotImplementedException();
    /// <summary>Exit code for definite host absence.</summary>
    public const int HostAbsentExitCode = 3;
    /// <summary>Exit code for invalid usage.</summary>
    public const int UsageExitCode = 2;
}

/// <summary>The parsed command and its options.</summary>
public sealed class CliInvocation
{
    /// <summary>Command selected by the parser.</summary>
    public string CommandKey => throw new NotImplementedException();
    /// <summary>Read a named option.</summary>
    public string? Value(string optionKey) => throw new NotImplementedException();
    /// <summary>Check whether a flag was supplied.</summary>
    public bool Flag(string optionKey) => throw new NotImplementedException();
}
