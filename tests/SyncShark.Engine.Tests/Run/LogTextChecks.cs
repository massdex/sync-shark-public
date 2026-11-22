using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Contract checks for LogText: every fact is projected, control characters stay inside one line, empty
/// facts are omitted, every event kind renders, and the kind is the first column so a reader can select per-file
/// lines (A49).</summary>
public static class LogTextChecks
{
    public static Task RunAsync()
    {
        ProjectsEveryFact();
        EscapesControlCharactersSoOneEventStaysOneLine();
        OmitsEmptyFacts();
        EveryKindProjects();
        EffectIsSelectableAsTheFirstColumn();
        return Task.CompletedTask;
    }

    private static void ProjectsEveryFact()
    {
        var line = LogText.Format(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", "copied",
            "Dropbox") { Count = 2, Total = 5 });
        var fields = line.Split(RunLog.FieldSeparator);
        Check.Require(fields.Length == 7, "kind, what, path, detail, outcome, count and total are all present");
        Check.Require(fields[0] == "Effect" && fields[1] == "Uploaded" && fields[2] == "alpha/report.txt" &&
            fields[3] == "copied" && fields[4] == "Dropbox" && fields[5] == "2" && fields[6] == "5",
            "the projection keeps each fact in its column");
    }

    private static void EscapesControlCharactersSoOneEventStaysOneLine()
    {
        var line = LogText.Format(new RunEvent(RunEventKind.Issue, "Failure", "a\nb\tc\rd\u0001e", "reason", "cause"));
        Check.Require(!line.Contains('\n') && !line.Contains('\r'),
            "an embedded newline cannot split one event into two stored lines");
        var fields = line.Split(RunLog.FieldSeparator);
        Check.Require(fields.Length == 5, "an embedded tab cannot add a column");
        Check.Require(fields[2] == "a\\nb\\tc\\rd\\u0001e", "each control character has its reversible escape");
    }

    private static void OmitsEmptyFacts()
    {
        var fields = LogText.Format(new RunEvent(RunEventKind.Wait, string.Empty, string.Empty, "reason", string.Empty))
            .Split(RunLog.FieldSeparator);
        Check.Require(fields.Length == 2 && fields[0] == "Wait" && fields[1] == "reason",
            "empty facts leave no stray columns");
    }

    private static void EveryKindProjects()
    {
        foreach (var kind in Enum.GetValues<RunEventKind>())
        {
            var line = LogText.Format(new RunEvent(kind, "what", string.Empty, string.Empty, string.Empty));
            Check.Require(line.StartsWith(kind.ToString(), StringComparison.Ordinal),
                $"the {kind} line names its kind first");
        }
    }

    private static void EffectIsSelectableAsTheFirstColumn()
    {
        var perFile = LogText.Format(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", string.Empty, "Dropbox"));
        var routine = LogText.Format(new RunEvent(RunEventKind.Stage, "Reading both sides", string.Empty, string.Empty, string.Empty));
        Check.Require(perFile.Split(RunLog.FieldSeparator)[0] == nameof(RunEventKind.Effect),
            "a per-file line is selectable by its first column");
        Check.Require(routine.Split(RunLog.FieldSeparator)[0] != nameof(RunEventKind.Effect),
            "a routine line is not mistaken for a per-file line");
    }
}
