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
        var move = LogText.Format(new RunEvent(RunEventKind.Effect, "", "new.txt", "old.txt", nameof(RemovalOutcome.Published)) { Action = ActionKind.MoveLocal });
        Check.Require(move.Contains(Text.Get(TextKey.HumanCardFrom, (TextArg.Path, "old.txt"))) &&
            move.Contains(Text.Get(TextKey.HumanCardTo, (TextArg.Path, "new.txt"))), "a move shows its two endpoints");
        var stop = LogText.Format(new RunEvent(RunEventKind.Failed, "", "", "", "") { Error = new OperationCanceledException() });
        Check.Require(stop.Contains(Text.Get(TextKey.HumanAttentionStoppedResult)), "a cancellation reports a stop, not a failure");
        var notice = LogText.Format(new RunEvent(RunEventKind.Issue, "", "item", "", "") { Cause = TextKey.FailDropboxNameRejected });
        Check.Require(notice.Contains(Text.Get(TextKey.FailDropboxNameRejected, (TextArg.Path, "item"))), "a cause alone supplies a readable reason");
        var provider = LogText.Format(new RunEvent(RunEventKind.ProviderRequest, "", "", "arbitrary body", "done"));
        Check.Require(provider.Length == 0, "a completed provider diagnostic is not a user outcome");
        CheckRecordedFacts();
        OneMoveCard();
        AllocationChecks();
        return Task.CompletedTask;
    }

    private static void AllocationChecks()
    {
        var at = DateTimeOffset.UnixEpoch;
        var completed = new RunEvent(RunEventKind.ProviderRequest, "", "", "", "done");
        var incomplete = new RunEvent(RunEventKind.Effect, "", "old", "", nameof(RemovalOutcome.Published))
            { Action = ActionKind.MoveLocal, Side = Side.Local };
        // Warm the two paths before counting. Returning the shared empty string needs no display-clock string.
        long CountBytes(RunEvent item)
        {
            _ = LogText.Format(item, at);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var block = LogText.Format(item, at);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check.Require(block.Length == 0, "the suppressed record must stay invisible");
            return bytes;
        }
        var providerBytes = CountBytes(completed);
        var moveBytes = CountBytes(incomplete);
        Check.Require(providerBytes == 0 && moveBytes == 0,
            $"suppressed records must not allocate a display clock: provider bytes={providerBytes}; move bytes={moveBytes}");

        // An uncommon control must escape by appending the prefix and the formatted digits in place; the old
        // encoder built a concatenated prefix-plus-digits string first, one extra allocation per character. The
        // shared escape loop serves both the stored and readable encoders, so measuring one proves the other.
        const int controls = 4096;
        const int bytesPerEscapedControl = 90;
        var unusual = new string('\u001b', controls);
        _ = RunLog.EscapeDisplay(unusual);
        var beforeEscape = GC.GetAllocatedBytesForCurrentThread();
        var escapedLength = RunLog.EscapeDisplay(unusual).Length;
        var escapeBytes = GC.GetAllocatedBytesForCurrentThread() - beforeEscape;
        Check.Require(escapedLength == controls * 6 && escapeBytes < controls * bytesPerEscapedControl,
            $"a control run escapes in place without an intermediate string: length={escapedLength}; " +
            $"bytes={escapeBytes}; budget={controls * bytesPerEscapedControl}");
    }

    private static void OneMoveCard()
    {
        var at = DateTimeOffset.UnixEpoch;
        var move = new RunEvent(RunEventKind.Effect, nameof(ISide.MoveAsync), "new", "old", nameof(RemovalOutcome.Published)) { Side = Side.Local };
        foreach (var action in new[] { ActionKind.MoveLocal, ActionKind.MoveDropbox })
        {
            var binding = new RunEvent(RunEventKind.Effect, action.ToString(), "old", "", "Applied") { Action = action, Side = Side.Local };
            var card = LogText.Format(move, at).Split(Environment.NewLine);
            Check.Require(card.Contains(Text.Get(TextKey.HumanCardFrom, (TextArg.Path, "old"))) &&
                card.Contains(Text.Get(TextKey.HumanCardTo, (TextArg.Path, "new"))) &&
                card.Contains(Text.Get(TextKey.HumanCardSide, (TextArg.Side, Text.Get(TextKey.HumanSideLocal)))) &&
                LogText.Format(binding, at).Length == 0 &&
                LogText.Format(new RunEvent(RunEventKind.Effect, action.ToString(), "old", "", nameof(Side.Local)), at).Length == 0,
                "only the observed move contributes a card; the binding has no source or proven destination");
        }
    }

    private static void CheckRecordedFacts()
    {
        const string sentinel = "synthetic exception detail";
        var failed = LogText.Format(new RunEvent(RunEventKind.Failed, nameof(NoticeKind.Failure), "item", sentinel, nameof(NoticeKind.Failure)) { Cause = TextKey.ReadLocalFailed });
        bool safe = !failed.Contains(sentinel) && failed.Contains(Text.Get(TextKey.ReadLocalFailed, (TextArg.Path, "item"), (TextArg.Message, string.Empty)));
        bool neutral = true;
        foreach (var action in new[] { ActionKind.MoveLocal, ActionKind.KeepBoth })
            foreach (var outcome in new[] { nameof(RemovalOutcome.Pending), nameof(RemovalOutcome.Unknown), "Refused" })
            {
                var text = LogText.Format(new RunEvent(RunEventKind.Effect, action.ToString(), "target", "source", outcome));
                neutral &= text.Contains(outcome) && !text.Contains(Text.Get(TextKey.HumanOkLabel)) &&
                    !new[] { TextKey.HumanCardFrom, TextKey.HumanCardTo, TextKey.HumanCardOriginal, TextKey.HumanCardCopy }
                        .Any(key => text.Contains(Text.Get(key, (TextArg.Path, string.Empty))));
            }
        var at = DateTimeOffset.UnixEpoch;
        var waiting = LogText.Format(new RunEvent(RunEventKind.ProviderRequest, "", "", "activity\ncontinued", ""), at);
        bool activity = waiting.Contains(Text.Get(TextKey.HumanStageActive, (TextArg.Time, "00:00:00"))) && waiting.Contains(@"activity\ncontinued");
        var stage = LogText.Format(new RunEvent(RunEventKind.Stage, nameof(TextKey.StageRows), "", "last progress", nameof(RunEventKind.Failed)), at);
        bool interrupted = stage.Contains(Text.Get(TextKey.HumanStageInterrupted, (TextArg.Indent, Text.Get(TextKey.HumanIndent)),
            (TextArg.Name, Text.Get(TextKey.StageRows)), (TextArg.Elapsed, Text.Get(TextKey.HumanNotMeasured)))) && stage.Contains("last progress");
        Check.Require(safe && neutral && activity && interrupted,
            $"readable safe failure={safe}; neutral pending endpoints={neutral}; activity retained={activity}; stage interrupted={interrupted}");
    }

    private static void EveryKindRenders()
    {
        foreach (var kind in Enum.GetValues<RunEventKind>())
        {
            var text = LogText.Format(new RunEvent(kind, "what", string.Empty, "progress", string.Empty));
            Check.Require(text.Length > 0, $"the {kind} projection is never empty");
        }
    }

    private static void ProjectPresentFacts()
    {
        var text = LogText.Format(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", "copied", "Dropbox"));
        Check.Require(text.Contains("alpha/report.txt", StringComparison.Ordinal) && text.Contains(Text.Get(TextKey.HumanOperationUpload), StringComparison.Ordinal) && !text.Contains(Text.Get(TextKey.HumanResultReplaced), StringComparison.Ordinal), "an untyped upload is not a successful replace");
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
        Check.Require(!text.Contains("boom", StringComparison.Ordinal) && text.Contains("HRESULT", StringComparison.Ordinal) &&
            text.Contains("alpha/report.txt", StringComparison.Ordinal), "a typed failure renders its reason and file");
    }

    private static void RenderTypedStageEnd()
    {
        var text = LogText.Format(new RunEvent(RunEventKind.Stage, "Reading both sides", string.Empty, "observations",
            string.Empty) { Ended = true, Count = 42, Total = 51 });
        Check.Require(text.Contains("Reading both sides", StringComparison.Ordinal) &&
            text.Contains("42", StringComparison.Ordinal) && text.Contains("51", StringComparison.Ordinal) &&
            text.Contains("observations", StringComparison.Ordinal) && text.Contains("Complete", StringComparison.Ordinal), "a typed stage end renders its name and completion");
    }

    private static void RenderEffectAsReadable()
    {
        var text = LogText.Format(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", string.Empty,
            "Dropbox") { Action = ActionKind.Upload });
        Check.Require(text.Contains(Text.Get(TextKey.HumanOkLabel), StringComparison.Ordinal),
            "an effect renders the ok label, not a raw kind word");
    }
}
