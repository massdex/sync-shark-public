using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Contract checks for LogText: a readable per-event projection built from the text catalog, with present
/// facts shown, empty facts omitted, control characters escaped, a Windows backslash left readable, typed failures
/// and stage ends rendered as their readable labels, and every kind rendering (M22, product-0689).</summary>
public static class LogTextChecks
{
    public static Task RunAsync()
    {
        EveryKindRenders();
        ProjectPresentFacts();
        OmitEmptyFacts();
        EscapeControlCharacters();
        LeaveBackslashReadable();
        RenderTypedFailure();
        RenderTypedStageEnd();
        RenderEffectAsReadable();
        return Task.CompletedTask;
    }

    private static void EveryKindRenders()
    {
        foreach (var kind in Enum.GetValues<RunEventKind>())
        {
            var text = LogText.Format(new RunEvent(kind, "what", string.Empty, string.Empty, string.Empty));
            Check.Require(text.Length > 0, $"the {kind} projection is never empty");
        }
    }

    private static void ProjectPresentFacts()
    {
        var text = LogText.Format(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", "copied", "Dropbox"));
        Check.Require(text.Contains("alpha/report.txt", StringComparison.Ordinal), "the projected file is shown");
    }

    private static void OmitEmptyFacts()
    {
        var text = LogText.Format(new RunEvent(RunEventKind.Wait, string.Empty, string.Empty, "reason", string.Empty));
        Check.Require(text.Contains("reason", StringComparison.Ordinal), "a present fact is shown");
        Check.Require(!text.Contains("|", StringComparison.Ordinal), "empty facts leave no stray separators");
    }

    private static void EscapeControlCharacters()
    {
        var text = LogText.Format(new RunEvent(RunEventKind.Issue, "Failure", "a\nb", "c\rd", "cause"));
        Check.Require(text.Contains("a\\nb", StringComparison.Ordinal) &&
            text.Contains("c\\rd", StringComparison.Ordinal), "control characters in data are escaped in the readable view");
    }

    private static void LeaveBackslashReadable()
    {
        var text = LogText.Format(new RunEvent(RunEventKind.Effect, "Uploaded", "a\\b", string.Empty, string.Empty));
        Check.Require(text.Contains("a\\b", StringComparison.Ordinal),
            "a Windows path's own backslash stays readable in the readable view");
    }

    private static void RenderTypedFailure()
    {
        var text = LogText.Format(new RunEvent(RunEventKind.Failed, string.Empty, "alpha/report.txt", string.Empty,
            string.Empty) { Error = new IOException("boom") });
        Check.Require(text.Contains(Text.Get(TextKey.HumanError), StringComparison.Ordinal),
            "a typed failure renders the error label, not a raw kind word");
        Check.Require(text.Contains("boom", StringComparison.Ordinal) &&
            text.Contains("alpha/report.txt", StringComparison.Ordinal), "a typed failure renders its reason and file");
    }

    private static void RenderTypedStageEnd()
    {
        var text = LogText.Format(new RunEvent(RunEventKind.Stage, "Reading both sides", string.Empty, string.Empty,
            string.Empty) { Ended = true, Count = 42 });
        Check.Require(text.Contains("Reading both sides", StringComparison.Ordinal) &&
            text.Contains("Complete", StringComparison.Ordinal), "a typed stage end renders its name and completion");
    }

    private static void RenderEffectAsReadable()
    {
        var text = LogText.Format(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", string.Empty,
            "Dropbox") { Action = ActionKind.Upload });
        Check.Require(text.Contains(Text.Get(TextKey.HumanOkLabel), StringComparison.Ordinal),
            "an effect renders the ok label, not a raw kind word");
    }
}
