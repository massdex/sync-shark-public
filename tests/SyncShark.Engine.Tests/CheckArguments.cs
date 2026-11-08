namespace Massdex.SyncShark.Engine.Tests;

/// <summary>The test runner's command line.</summary>
public static class CheckArguments
{
    public const string Flag = "--engine-checks";
    /// <summary>Test-only: runs an engine CLI command through the same SyncCli call the app makes (live test pair).</summary>
    public const string CliFlag = "--cli";
    public const string ListSeparator = " ";
    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageExit = 2;
}
