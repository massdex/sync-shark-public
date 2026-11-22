using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Contract checks for LogText: one readable line per event, every present fact kept, empty facts omitted,
/// control characters escaped so one event stays one line, a Windows backslash left readable, and a typed outcome
/// carried through (M22). The stored canonical TSV and its timestamp are checked in RunLogChecks.</summary>
public static class LogTextChecks
{
    public static Task RunAsync()
    {
        ProjectsPresentFacts();
        OmitsEmptyFacts();
        EscapesControlCharacters();
        LeavesBackslashReadable();
        CarriesTypedOutcome();
        EveryKindRenders();
        return Task.CompletedTask;
    }

    private static void ProjectsPresentFacts()
    {
        var line = LogText.Format(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", "copied", "Dropbox"));
        Check.Require(line.StartsWith("Effect ", StringComparison.Ordinal), "the line names its kind first");
        Check.Require(line.Contains("Uploaded", StringComparison.Ordinal) &&
            line.Contains("alpha/report.txt", StringComparison.Ordinal) &&
            line.Contains("copied", StringComparison.Ordinal) &&
            line.Contains("Dropbox", StringComparison.Ordinal), "every present fact is projected");
    }

    private static void OmitsEmptyFacts()
    {
        var line = LogText.Format(new RunEvent(RunEventKind.Wait, string.Empty, string.Empty, "reason", string.Empty));
        Check.Require(line == "Wait reason", "empty facts leave no stray text");
    }

    private static void EscapesControlCharacters()
    {
        var line = LogText.Format(new RunEvent(RunEventKind.Issue, "Failure", "a\nb", "c\rd", "cause"));
        Check.Require(!line.Contains('\n') && !line.Contains('\r'),
            "an embedded newline cannot split one projection into two lines");
        Check.Require(line.Contains("a\\nb", StringComparison.Ordinal) &&
            line.Contains("c\\rd", StringComparison.Ordinal), "each control character has its display escape");
    }

    private static void LeavesBackslashReadable()
    {
        var line = LogText.Format(new RunEvent(RunEventKind.Effect, "Uploaded", "a\\b", string.Empty, string.Empty));
        Check.Require(line.Contains("a\\b", StringComparison.Ordinal),
            "a Windows path's own backslash stays readable in the display projection");
    }

    private static void CarriesTypedOutcome()
    {
        var line = LogText.Format(new RunEvent(RunEventKind.Issue, "Failure", "alpha/report.txt", "name rejected",
            "FailDropboxNameRejected"));
        Check.Require(line.Contains("FailDropboxNameRejected", StringComparison.Ordinal),
            "a representative typed outcome is projected");
    }

    private static void EveryKindRenders()
    {
        foreach (var kind in Enum.GetValues<RunEventKind>())
        {
            var line = LogText.Format(new RunEvent(kind, "what", string.Empty, string.Empty, string.Empty));
            Check.Require(line.StartsWith(kind.ToString(), StringComparison.Ordinal), $"the {kind} line names its kind");
        }
    }
}
