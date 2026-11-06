using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: ResultResolveDone/Failed and the drive-choice decision sentence; exit 0 when Done, else
/// TransientUnavailable. A failed resolution's reason is the kind of issue its outcome raised.</summary>
public sealed record ResolveResult(long Issue, ResolutionChoice Choice, bool Done, SyncOutcome Outcome) : IEngineCliData
{
    /// <summary>The resolved, failed or drive-choice-recorded sentence, naming the choice or the raised kind.</summary>
    public string RenderHuman()
    {
        var id = (TextArg.Id, Issue.ToString(CultureInfo.InvariantCulture));
        if (!Done)
            return Text.Get(TextKey.ResultResolveFailed, id,
                (TextArg.Reason, Outcome.Raised?.ToString() ?? Outcome.Kind.ToString()));
        return Choice is ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally
            ? Text.Get(TextKey.ResultDriveChoiceRecorded, id, (TextArg.Choice, Choice.ToString()))
            : Text.Get(TextKey.ResultResolveDone, id, (TextArg.Choice, Choice.ToString()));
    }

    public int ExitCode => (int)(Done ? SyncCliExitCode.Success : SyncCliExitCode.TransientUnavailable);
}
