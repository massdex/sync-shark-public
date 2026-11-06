using System.Diagnostics;
using System.Globalization;
using System.Text;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Golden-text checks for the shared human projection (D-1105-6, R1): every section 3 block, the
/// historical file view, malformed/unfinished records, width and hostile control characters.</summary>
public static class HumanLogChecks
{
    private static readonly DateTimeOffset Started =
        DateTimeOffset.Parse("2026-11-05T00:40:39Z", CultureInfo.InvariantCulture);

    public static void Run()
    {
        HeaderAndStages();
        ActionCards();
        AttentionAndWait();
        Completion();
        FileView();
        MalformedRecordsDoNotAbort();
        WidthAndControlCharacters();
        LiveConsoleThroughRunLog();
        StreamedFileView();
        HiddenEffectsNeverWaitOnDelivery();
        HiddenEffectStartsSpan();
        MidnightDivider();
    }

    private static HumanLog Open(bool perFile = false, int width = RunLog.HumanWidth) =>
        new("Pair A", "example-run-id", "C:/Data/engine/logs/Pair A-example-run-id.log",
            TextKey.HumanTitleRepair, perFile, width, TextKey.HumanModeDirect);

    private static string Join(params string?[] blocks) =>
        string.Join(Environment.NewLine, blocks.Where(block => block is not null));

    private static RunEvent StageStart(TextKey key) =>
        new(RunEventKind.Stage, key.ToString(), string.Empty, string.Empty, string.Empty);

    private static RunEvent StageEnd(TextKey key, long count) =>
        new(RunEventKind.Stage, key.ToString(), string.Empty, string.Empty, nameof(RunEvent.Ended)) { Count = count };

    private static void HeaderAndStages()
    {
        var log = Open();
        var text = Join(
            log.Accept(StageStart(TextKey.StageListing), Started),
            log.Accept(StageStart(TextKey.StageLocalWalk), Started.AddSeconds(1)),
            log.Accept(StageEnd(TextKey.StageLocalWalk, 1000), Started.AddSeconds(80)),
            log.Accept(StageEnd(TextKey.StageListing, 1000), Started.AddSeconds(81)));
        Check.Require(text.Contains("SYNC SHARK | REPAIR", StringComparison.Ordinal), "header title");
        Check.Require(text.Contains("Pair:       Pair A", StringComparison.Ordinal), "header pair");
        Check.Require(text.Contains("Started:    2026-11-05 00:40:39 UTC", StringComparison.Ordinal), "header start");
        Check.Require(text.Contains("Changes applied | Per-file detail off", StringComparison.Ordinal), "header mode");
        Check.Require(text.Contains("00:40:39  READING BOTH SIDES", StringComparison.Ordinal), "section heading");
        Check.Require(text.Contains("Local walk: 1,000 entry observations  Complete  01:19", StringComparison.Ordinal),
            "subphase count, unit and elapsed");
        Check.Require(text.Contains("Reading both sides: 1,000 entry observations  Complete  01:21",
            StringComparison.Ordinal), "parent phase count and elapsed");
    }

    private static void ActionCards()
    {
        var off = Open();
        var headerOnly = off.Accept(new RunEvent(RunEventKind.Effect, nameof(EffectKind.Uploaded),
            "Documents/Example/report.pdf", string.Empty, nameof(Side.Dropbox)), Started);
        Check.Require(headerOnly is null,
            "the default console prints no successful per-file card and no output for it");

        var on = Open(perFile: true);
        var upload = on.Accept(new RunEvent(RunEventKind.Effect, nameof(EffectKind.Uploaded),
            "Documents/Example/report.pdf", string.Empty, nameof(Side.Dropbox)), Started);
        Check.Require(upload!.Contains("[OK] UPLOAD", StringComparison.Ordinal), "upload heading");
        Check.Require(upload.Contains("Documents/Example/report.pdf", StringComparison.Ordinal), "upload file");
        Check.Require(upload.Contains("This PC -> Dropbox", StringComparison.Ordinal), "upload direction");
        Check.Require(upload.Contains("Result:     Synced", StringComparison.Ordinal), "upload result");

        var move = on.Accept(new RunEvent(RunEventKind.Effect, nameof(EffectKind.MovedLocal),
            "Documents/Example/report-final.pdf", "Documents/Example/draft.pdf", nameof(Side.Local)),
            Started.AddSeconds(1));
        Check.Require(move!.Contains("[OK] MOVE", StringComparison.Ordinal) &&
            move.Contains("From:       Documents/Example/draft.pdf", StringComparison.Ordinal) &&
            move.Contains("To:         Documents/Example/report-final.pdf", StringComparison.Ordinal) &&
            move.Contains("Side:       This PC", StringComparison.Ordinal) &&
            move.Contains("Move committed", StringComparison.Ordinal), "move card keeps both paths and side");

        var forget = on.Accept(new RunEvent(RunEventKind.Effect, nameof(EffectKind.Forgotten),
            "Documents/Example/report.pdf", string.Empty, string.Empty), Started.AddSeconds(2));
        Check.Require(forget!.Contains("[OK] REMOVED SAVED RELATIONSHIP", StringComparison.Ordinal) &&
            forget.Contains("Removed saved relationship", StringComparison.Ordinal),
            "state-only effects are never described as a copied or deleted file");

        var keep = on.Accept(new RunEvent(RunEventKind.Effect, nameof(EffectKind.KeptBoth),
            "Documents/Example/copy.pdf", "Documents/Example/original.pdf", nameof(Side.Dropbox)),
            Started.AddSeconds(3));
        Check.Require(keep!.Contains("[OK] KEEP BOTH", StringComparison.Ordinal) &&
            keep.Contains("Original:   Documents/Example/original.pdf", StringComparison.Ordinal) &&
            keep.Contains("Copy:       Documents/Example/copy.pdf", StringComparison.Ordinal) &&
            keep.Contains("Both versions kept", StringComparison.Ordinal), "keep-both names both versions and side");
    }

    private static void AttentionAndWait()
    {
        var log = Open();
        var issue = new RunEvent(RunEventKind.Issue, nameof(IssueKind.Failure),
            "Documents/Example/report.pdf", "Dropbox does not allow the name Documents/Example/report.pdf.",
            nameof(TextKey.FailDropboxNameRejected));
        var text = Join(
            log.Accept(issue, Started),
            log.Accept(new RunEvent(RunEventKind.Issue, nameof(IssueKind.Info),
                "Documents/Example/edited.pdf", "The edited version was kept.", nameof(TextKey.InfoEditBeatsDelete)),
                Started.AddSeconds(1)),
            log.Wait(Text.Get(TextKey.WaitTooManyRequests, (TextArg.Seconds, "3")), Started.AddSeconds(2)));
        Check.Require(text.Contains("[ATTENTION] FILE NOT SYNCED", StringComparison.Ordinal), "failure label");
        Check.Require(text.Contains("[NOTICE]", StringComparison.Ordinal), "information label");
        Check.Require(text.Contains("Reason:     Dropbox does not allow the name", StringComparison.Ordinal),
            "failure reason");
        Check.Require(text.Contains("This item remains unsynced; other work continues.", StringComparison.Ordinal),
            "failure result does not stop unrelated work");
        Check.Require(text.Contains("[WAITING]", StringComparison.Ordinal) &&
            text.Contains("Dropbox asked Sync Shark to slow down", StringComparison.Ordinal) &&
            text.Contains("Other independent work can continue.", StringComparison.Ordinal), "wait block");
    }

    private static void Completion()
    {
        var log = Open();
        log.Accept(new RunEvent(RunEventKind.Effect, nameof(EffectKind.Uploaded), "Documents/Example/a.pdf",
            string.Empty, nameof(Side.Dropbox)), Started);
        log.Accept(new RunEvent(RunEventKind.Effect, nameof(EffectKind.Downloaded), "Documents/Example/b.pdf",
            string.Empty, nameof(Side.Local)), Started);
        log.Accept(new RunEvent(RunEventKind.Issue, nameof(IssueKind.Failure), "Documents/Example/c.pdf",
            "reason", nameof(TextKey.FailChanged)), Started);
        var text = log.Complete(true,
            new Dictionary<string, int> { ["Uploaded"] = 1, ["Downloaded"] = 1 },
            new Dictionary<string, int> { ["Failure"] = 1, ["Info"] = 1 }, Started.AddMinutes(52))!;
        Check.Require(text.Contains("REPAIR FINISHED - PROCESSING COMPLETE", StringComparison.Ordinal), "completion banner");
        Check.Require(text.Contains("Run processing: Complete", StringComparison.Ordinal), "processing status");
        Check.Require(text.Contains("Downloaded (This PC)", StringComparison.Ordinal) &&
            text.Contains("Uploaded (Dropbox)", StringComparison.Ordinal), "committed outcome per side");
        Check.Require(text.Contains("New notices opened: Not recorded", StringComparison.Ordinal) &&
            text.Contains("Unique files synced: Not measured", StringComparison.Ordinal) &&
            text.Contains("Peak memory: Not recorded", StringComparison.Ordinal),
            "facts this run does not record are labelled, never invented");
        Check.Require(text.Contains("ATTENTION SUMMARY", StringComparison.Ordinal) &&
            text.Contains("its observed state changed or could not be confirmed as current", StringComparison.Ordinal) &&
            Text.Get(TextKey.FailChanged).Contains("Sync will retry at the next sync opportunity.", StringComparison.Ordinal) &&
            text.Contains("Inspect notices: engine issues --pair Pair A", StringComparison.Ordinal),
            "the attention summary groups by the recorded cause and points at the durable notice view");
        Check.Require(text.Contains("Notice emissions:", StringComparison.Ordinal), "notice emissions");
        Check.Require(!text.Contains("New notices opened: 0", StringComparison.Ordinal),
            "an unknown notice transition is never reported as zero");

        var incomplete = Open().Complete(false, null, null, Started)!;
        Check.Require(incomplete.Contains("PROCESSING INCOMPLETE", StringComparison.Ordinal),
            "incomplete processing is stated, not called success");
    }

    private static void FileView()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(folder.FullName, "PairA-example.log");
            var line = new StringBuilder();
            line.Append("2026-11-05T00:40:39.0000000+00:00\tStage\tStageListing\t\tstarted\t").Append('\n');
            line.Append("2026-11-05T00:41:00.0000000+00:00\tStage\tStageListing\tDocuments/Example\tprogress 10\tInProgress")
                .Append('\n');
            line.Append("2026-11-05T00:41:02.0000000+00:00\tEffect\tUploaded\tDocuments/Example/report.pdf\t\tDropbox").Append('\n');
            line.Append("2026-11-05T00:41:03.0000000+00:00\tIssue\tFailure\tDocuments/Example/report.pdf\trejected\tFailChanged").Append('\n');
            line.Append("malformed").Append('\n');
            line.Append("2026-11-05T00:41:04.0000000+00:00\tcomplete\t\t\tDownloaded=1;Failure=1\ttrue").Append('\n');
            line.Append("2026-11-05T00:41:05.0000000+00:00\tEffect\tUploaded\tPARTIAL-MARKER");
            File.WriteAllText(path, line.ToString());

            var human = new HumanLog("Pair A", "example-run-id", path, TextKey.HumanTitleRun, perFile: false,
                RunLog.HumanWidth);
            var text = new StringBuilder();
            var number = 0;
            var status = LogFiles.Read(path, (record, wellFormed) =>
            {
                number++;
                var block = human.AcceptFileLine(number, record, wellFormed);
                if (block is not null) text.Append(block).Append(Environment.NewLine);
            }, CancellationToken.None, out var unfinished);
            var tail = human.FinishFile(unfinished);
            if (tail is not null) text.Append(tail).Append(Environment.NewLine);
            var rendered = text.ToString();
            Check.Require(status == LogFiles.ReadStatus.Found && unfinished, "the reader streams and reports a partial tail");
            Check.Require(rendered.Contains("SYNC SHARK | RUN", StringComparison.Ordinal), "file header");
            Check.Require(rendered.Contains("Mode:       Not recorded", StringComparison.Ordinal),
                "a stored log records no Direct/Hosted mode instead of inventing one");
            Check.Require(rendered.Contains("READING BOTH SIDES", StringComparison.Ordinal), "file section heading");
            Check.Require(rendered.Contains("progress 10", StringComparison.Ordinal), "file progress row");
            Check.Require(rendered.Contains("[ATTENTION] FILE NOT SYNCED", StringComparison.Ordinal), "file attention");
            Check.Require(rendered.Contains("RUN FINISHED - PROCESSING COMPLETE", StringComparison.Ordinal),
                "file completion");
            Check.Require(rendered.Contains("Skipped a malformed record at line 5.", StringComparison.Ordinal),
                "a malformed record is reported visibly where it occurs");
            Check.Require(rendered.Contains("Malformed records skipped: 1", StringComparison.Ordinal), "malformed report");
            Check.Require(rendered.Contains("Ignoring an unfinished trailing record.", StringComparison.Ordinal),
                "unfinished trailing record");
            Check.Require(!rendered.Contains("PARTIAL-MARKER", StringComparison.Ordinal),
                "an unfinished record is not presented as a complete fact");

            var partialPath = Path.Combine(folder.FullName, "PairB-partial.log");
            File.WriteAllText(partialPath,
                "2026-11-05T01:00:00.0000000+00:00\tStage\tStageRows\t\tstarted\t\n");
            var partial = new HumanLog("Pair B", "partial-run", partialPath, TextKey.HumanTitleRun, perFile: false,
                RunLog.HumanWidth);
            var partialText = new StringBuilder();
            var partialNumber = 0;
            LogFiles.Read(partialPath, (record, wellFormed) =>
            {
                partialNumber++;
                var block = partial.AcceptFileLine(partialNumber, record, wellFormed);
                if (block is not null) partialText.Append(block).Append(Environment.NewLine);
            }, CancellationToken.None, out var partialUnfinished);
            var partialTail = partial.FinishFile(partialUnfinished);
            if (partialTail is not null) partialText.Append(partialTail);
            var partialRendered = partialText.ToString();
            Check.Require(partialRendered.Contains("Unfinished", StringComparison.Ordinal) &&
                partialRendered.Contains("PROCESSING NOT RECORDED", StringComparison.Ordinal) &&
                partialRendered.Contains("No completion record", StringComparison.Ordinal),
                "a log without completion reports an unfinished stage and unknown processing, never success");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // R1 review 3: an invalid completion outcome or an overflowing count sum is a visible malformed notice, and the
    // next record still renders.
    private static void MalformedRecordsDoNotAbort()
    {
        var log = Open();
        var badOutcome = "2026-11-05T00:40:39.0000000+00:00\tcomplete\t\t\tUploaded=1\tmaybe";
        Check.Require(log.AcceptFileLine(1, badOutcome)!.Contains("Skipped a malformed record at line 1.",
                StringComparison.Ordinal),
            "an invalid completion outcome is malformed, never read as an incomplete run");

        var overflow = "2026-11-05T00:40:40.0000000+00:00\tcomplete\t\t\tUploaded=2147483647;Downloaded=1\ttrue";
        Check.Require(log.AcceptFileLine(2, overflow)!.Contains("Skipped a malformed record at line 2.",
                StringComparison.Ordinal),
            "a count sum that overflows is malformed instead of aborting the view");

        var next = log.Accept(new RunEvent(RunEventKind.Issue, nameof(IssueKind.Failure), "Documents/Example/a.pdf",
            "reason", nameof(TextKey.FailChanged)), Started.AddSeconds(1));
        Check.Require(next is not null && next.Contains("[ATTENTION] FILE NOT SYNCED", StringComparison.Ordinal),
            "output continues after a malformed record");
    }

    private static void WidthAndControlCharacters()
    {
        var log = Open(perFile: true, width: 60);
        var longPath = "Documents/Example/" + new string('a', 120) + "/report.pdf";
        var card = log.Accept(new RunEvent(RunEventKind.Effect, nameof(EffectKind.Uploaded), longPath,
            string.Empty, nameof(Side.Dropbox)), Started)!;
        Check.Require(card.Split(Environment.NewLine).All(line => line.Length <= 60),
            "no human line exceeds the width budget");
        Check.Require(card.Contains("report.pdf", StringComparison.Ordinal),
            "a wrapped path keeps every character");

        var hostile = "Documents/Example/report\u0001\u001b.pdf";
        var text = log.Accept(new RunEvent(RunEventKind.Effect, nameof(EffectKind.Uploaded), hostile,
            string.Empty, nameof(Side.Dropbox)), Started.AddSeconds(1))!;
        Check.Require(!text.Contains('\u001b') && !text.Contains('\u0001'),
            "control characters from data are never emitted");
        Check.Require(text.Contains("\\u0001", StringComparison.Ordinal) &&
            text.Contains("\\u001b", StringComparison.Ordinal), "control characters are visibly escaped");
    }

    // R1 review 1: the real delivery callback keeps the projection's layout newlines and escapes only data values.
    private static void LiveConsoleThroughRunLog()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var blocks = new List<string>();
            using var log = RunLog.Open(folder.FullName, "PairA", "live", blocks.Add, 4096, perFileDetail: true,
                title: TextKey.HumanTitleRepair, mode: TextKey.HumanModeDirect);
            log.Event(StageStart(TextKey.StageListing));
            log.Event(new RunEvent(RunEventKind.Effect, nameof(EffectKind.Uploaded),
                "Documents/Example/report\nnext\u0001\u001b.pdf", string.Empty, nameof(Side.Dropbox)));
            Check.Require(blocks.Any(block => block.Contains(Environment.NewLine)),
                "a live console block keeps real newlines instead of literal \\n escapes");
            var joined = string.Join(Environment.NewLine, blocks);
            Check.Require(joined.Contains("report\\nnext", StringComparison.Ordinal),
                "a newline inside a data value is escaped while layout newlines stay real");
            Check.Require(!joined.Contains('\u0001') && !joined.Contains('\u001b') &&
                joined.Contains("\\u0001", StringComparison.Ordinal),
                "hostile control characters from data never reach a live console");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // R1 review 2/3/4: LogFiles streams, is cancellable, reports an undecodable record and continues, and accepts
    // a long record whole with memory bounded by one record.
    private static void StreamedFileView()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(folder.FullName, "stream.log");
            var first = "2026-11-05T00:40:39.0000000+00:00\tStage\tStageListing\t\tstarted\t\n";
            byte[] bad = [0x41, 0x42, 0xFF, 0xFE, (byte)'\n'];
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(first).Concat(bad).ToArray());
            var records = new List<(string Text, bool WellFormed)>();
            var status = LogFiles.Read(path, (text, ok) => records.Add((text, ok)), CancellationToken.None,
                out var unfinished);
            Check.Require(status == LogFiles.ReadStatus.Found && !unfinished,
                "the reader delivers only complete records");
            Check.Require(records.Count == 2 && records[0].WellFormed && !records[1].WellFormed,
                "undecodable bytes are reported instead of discarding the earlier output");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var threw = false;
            try { LogFiles.Read(path, (_, _) => { }, cancelled.Token, out _); }
            catch (OperationCanceledException) { threw = true; }
            Check.Require(threw, "a cancelled log view stops reading instead of running to the end");

            // No product cap: a record buffers until its own newline, so a long one decodes whole and the next
            // record still renders.
            File.WriteAllText(path, new string('a', 200_000) + "\n" + first);
            var count = 0;
            var wellFormed = 0;
            var longest = 0;
            LogFiles.Read(path, (text, ok) =>
            {
                count++;
                if (!ok) return;
                wellFormed++;
                longest = Math.Max(longest, text.Length);
            }, CancellationToken.None, out _);
            Check.Require(count == 2 && wellFormed == 2 && longest == 200_000,
                "a long record is decoded whole; memory is bounded by one record, not the file");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // R1 review 8/9: a hidden effect starts the recorded span, and empty recorded totals mean zero, never the
    // observed fallback.
    private static void HiddenEffectStartsSpan()
    {
        var log = Open();
        log.CountEffect(new RunEvent(RunEventKind.Effect, nameof(EffectKind.Uploaded), "Documents/Example/a.pdf",
            string.Empty, nameof(Side.Dropbox)), Started);
        var text = log.Complete(true, new Dictionary<string, int>(),
            new Dictionary<string, int>(), Started.AddMinutes(3))!;
        Check.Require(text.Contains("Recorded log span: 03:00", StringComparison.Ordinal),
            "a hidden effect starts the span even though it renders nothing");
        Check.Require(text.Contains("Notice emissions: 0 | Failure: 0 | Information: 0", StringComparison.Ordinal),
            "a recorded zero notice total is shown as a known zero");
        Check.Require(!text.Contains("Committed outcome", StringComparison.Ordinal),
            "an authoritative zero total never falls back to the observed counter");
    }

    // R1 review 7: a run crossing UTC midnight prints a dated divider instead of a bare repeated clock time.
    private static void MidnightDivider()
    {
        var log = Open();
        Check.Require(log.Accept(StageStart(TextKey.StageListing), Started) is not null,
            "the first block sets the run's UTC date");
        var later = log.Wait(Text.Get(TextKey.WaitTooManyRequests, (TextArg.Seconds, "3")),
            Started.AddDays(1))!;
        Check.Require(later.Contains("--- 2026-11-06 UTC ---", StringComparison.Ordinal),
            "crossing midnight prints a date divider");
        Check.Require(!later.Contains("--- 2026-11-05 UTC ---", StringComparison.Ordinal),
            "the divider is printed only when the recorded date changes");
    }

    // R1 review 9: an effect the human view does not show never waits on the delivery gate; the same producer
    // with file cards on still waits (the baseline shape this change removed from the common path).
    private static void HiddenEffectsNeverWaitOnDelivery()
    {
        var folder = Directory.CreateTempSubdirectory();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var releaseCards = new ManualResetEventSlim();
        using var enteredCards = new ManualResetEventSlim();
        try
        {
            using var log = RunLog.Open(folder.FullName, "PairA", "gate", _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }, 4096);
            var blocked = Task.Run(() => log.Event(StageStart(TextKey.StageListing)));
            Check.Require(entered.Wait(TimeSpan.FromSeconds(10)), "the display subscriber holds the delivery gate");
            var elapsed = Stopwatch.StartNew();
            var effects = Task.Run(() =>
            {
                for (var index = 0; index < 1000; index++)
                    log.Event(new RunEvent(RunEventKind.Effect, nameof(EffectKind.Uploaded), "a/b.part",
                        string.Empty, nameof(Side.Dropbox)));
            });
            Check.Require(effects.Wait(TimeSpan.FromSeconds(5)),
                "an effect that produces no output never waits on the delivery gate");
            elapsed.Stop();
            var hidden = elapsed.ElapsedMilliseconds;

            using var cards = RunLog.Open(folder.FullName, "PairB", "gate-on", _ =>
            {
                enteredCards.Set();
                releaseCards.Wait(TimeSpan.FromSeconds(30));
            }, 4096, perFileDetail: true);
            var blockedCards = Task.Run(() => cards.Event(StageStart(TextKey.StageListing)));
            Check.Require(enteredCards.Wait(TimeSpan.FromSeconds(10)), "the card subscriber holds the delivery gate");
            var baseline = Task.Run(() => cards.Event(new RunEvent(RunEventKind.Effect,
                nameof(EffectKind.Uploaded), "a/b.part", string.Empty, nameof(Side.Dropbox))));
            Check.Require(!baseline.Wait(TimeSpan.FromMilliseconds(500)),
                "baseline: an effect the view must render still waits on the held delivery gate");
            releaseCards.Set();
            Check.Require(baseline.Wait(TimeSpan.FromSeconds(10)), "the card effect resumes after the release");
            release.Set();
            Check.Require(blocked.Wait(TimeSpan.FromSeconds(10)) && blockedCards.Wait(TimeSpan.FromSeconds(10)),
                "both blocked displays resume after the release");
            Console.WriteLine("hidden effects: 1000 in " + hidden +
                " ms while the display was blocked; baseline single carded effect blocked >500 ms");
        }
        finally
        {
            release.Dispose();
            entered.Dispose();
            releaseCards.Dispose();
            enteredCards.Dispose();
            folder.Delete(recursive: true);
        }
    }
}
