using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: ResultRepair*; exit 0 if Completed, else TransientUnavailable.</summary>
public sealed record RepairResult(RepairSummary Summary) : IEngineCliData
{
    /// <summary>The repair header, effect counts, incompleteness and log lines.</summary>
    public string RenderHuman() => string.Join(Environment.NewLine, Lines());

    private IEnumerable<string> Lines()
    {
        yield return Text.Get(TextKey.ResultRepairHeader,
            (TextArg.Started, Summary.Started.ToString(CultureInfo.InvariantCulture)),
            (TextArg.Finished, Summary.Finished.ToString(CultureInfo.InvariantCulture)));
        foreach (var effect in Summary.Effects)
            yield return Text.Get(TextKey.ResultRepairCount, (TextArg.Effect, effect.Key),
                (TextArg.Count, effect.Value.ToString(CultureInfo.InvariantCulture)));
        if (!Summary.Completed)
            yield return Text.Get(TextKey.ResultRepairIncomplete, (TextArg.Reason, Summary.IncompleteReason ?? string.Empty));
        yield return Text.Get(TextKey.ResultRepairLog, (TextArg.LogPath, Summary.LogPath));
        if (Summary.LogFailed) yield return Text.Get(TextKey.ResultRepairLogFailed);
    }

    public int ExitCode => (int)(Summary.Completed ? SyncCliExitCode.Success : SyncCliExitCode.TransientUnavailable);
}
