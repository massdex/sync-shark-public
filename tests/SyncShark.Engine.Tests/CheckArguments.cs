namespace Massdex.SyncShark.Engine.Tests;

/// <summary>The test runner's command line.</summary>
public static class CheckArguments
{
    public const string Flag = "--engine-checks";
    public const string LiteralSitesFlag = "--literal-sites";
    /// <summary>Test-only: runs an engine CLI command through the same SyncCli call the app makes (live test pair).</summary>
    public const string CliFlag = "--cli";
    /// <summary>Test-only: runs one named measurement that no full CI run includes (its own dispatch workflow does).</summary>
    public const string MeasurementFlag = "--measurement";
    public const string ItemWaitContention = "item-wait-contention";
    public const string ListSeparator = " ";
    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageExit = 2;
}
