using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>The counts of attempted and successfully resolved failures, without a single-issue identity.</summary>
public sealed record RetryAllResult(int Retried, int Succeeded) : IEngineCliData
{
    public string RenderHuman() => Text.Get(TextKey.ResultRetryAll,
        (TextArg.Count, Retried.ToString(CultureInfo.InvariantCulture)),
        (TextArg.Value, Succeeded.ToString(CultureInfo.InvariantCulture)));

    public int ExitCode => (int)(Retried == Succeeded ? SyncCliExitCode.Success : SyncCliExitCode.TransientUnavailable);
}
