using System.Globalization;
using System.Text;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Checks for RunLog (`--engine-checks repair`), written by RunLog's writer.</summary>
public static class RunLogChecks
{
    // The shipped settings, not a local literal, supply the buffer every log opens with (SPEED 4.1/4.4).
    private static readonly int BufferCharacters = ShippedBufferCharacters();

    public static void Run()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            LineFormat(folder.FullName);
            Counts(folder.FullName);
            Escaping(folder.FullName);
            WaitLine(folder.FullName);
            FlushedBoundaries(folder.FullName);
            AggregatedListing(folder.FullName);
            AggregatedStages(folder.FullName);
            StageDuration(folder.FullName);
            ProviderRequestLifetime(folder.FullName);
            ProviderReason(folder.FullName);
            ClassifiedFailures(folder.FullName);
            FailureLocation(folder.FullName);
            ThrowingProgress(folder.FullName);
            FailureIdentity(folder.FullName);
            OrderedProgress(folder.FullName);
            ConcurrentBoundaries(folder.FullName);
            CompletionBeforePendingProgress(folder.FullName);
            CompleteLine(folder.FullName);
            NonterminalComplete(folder.FullName);
            Cancellation(folder.FullName);
            RepeatedDisposal(folder.FullName);
            OpenFailure(folder.FullName);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static void AggregatedStages(string folder)
    {
        var messages = new List<string>();
        using var log = Open(folder, "stage-progress", messages);
        foreach (var stage in new[] { TextKey.StageRenames, TextKey.StageRows, TextKey.StagePending })
        {
            log.Event(RunEvent.Stage(stage));
            var before = messages.Count;
            const int unchanged = 1000;
            for (var done = 1; done <= unchanged; done++)
                log.Event(RunEvent.Stage(stage, count: done, inProgress: true, path: "Parent/Child", total: unchanged));
            Check.Require(messages.Count == before, "unchanged observations do not create per-item lines");
            log.Event(RunEvent.Stage(stage, count: unchanged, inProgress: true, total: unchanged));
            var reported = log.ReportProgress();
            var quiet = messages[^1];
            Check.Require(reported && messages.Count == before + 1 &&
                quiet.Contains("1000 of 1000", StringComparison.Ordinal) &&
                quiet.Contains("Parent/Child", StringComparison.Ordinal),
                "quiet signal reports the active stage count and subject for every active stage");
            Check.Require(ReadShared(log.Path).Contains("1000 of 1000"), "quiet stage snapshot is flushed");
            log.Event(RunEvent.Stage(stage, ended: true, count: unchanged));
            Check.Require(!log.ReportProgress(), "ended stage cannot leave a stale quiet snapshot");
        }
    }

    private static void ClassifiedFailures(string folder)
    {
        using var log = Open(folder, "classified", []);
        foreach (var key in new[] { TextKey.RootLocalIdentityChanged, TextKey.RootDropboxScopeChanged })
            log.Failed(new EngineRequestException(key, (TextArg.Path, "secret-value")));
        log.Failed(new ItemFailedException("Parent/Child", TextKey.FailChanged,
            new IOException("secret-value")));
        foreach (var code in new[] { 5, 32, 112 })
            log.Failed(new System.ComponentModel.Win32Exception(code, "secret-value"));
        log.Failed(new InvalidOperationException("secret-value"));
        var text = ReadShared(log.Path);
        Check.Require(text.Contains(nameof(TextKey.RootLocalIdentityChanged)) &&
            text.Contains(nameof(TextKey.RootDropboxScopeChanged)) && text.Contains(nameof(TextKey.FailChanged)),
            "classified root and item failures keep their distinct keys");
        foreach (var code in new[] { 5, 32, 112 })
            Check.Require(text.Contains("native code " + code), "native failures retain the actual OS code");
        Check.Require(text.Contains("HRESULT ") && !text.Contains("secret-value"),
            "fallback is accurately labelled and arbitrary exception arguments/messages stay private");
    }

    private static void FailureIdentity(string folder)
    {
        using var log = Open(folder, "failure-identity", []);
        var failure = new IOException();
        log.Failed(failure);
        log.Failed(failure);
        log.Failed(new IOException());
        log.Dispose();
        Check.Require(File.ReadLines(log.Path).Count(line => line.Contains(nameof(RunEventKind.Failed))) == 2,
            "one propagated exception is recorded once, while distinct failures are retained");
    }

    private static void ThrowingProgress(string folder)
    {
        var calls = 0;
        using var log = RunLog.Open(folder, "throwing-progress", "throwing-progress", _ =>
        {
            calls++;
            throw new InvalidOperationException("private-message");
        }, BufferCharacters);
        log.Event(RunEvent.Stage(TextKey.StageJournal));
        log.ReportProgress();
        log.Event(RunEvent.Stage(TextKey.StageJournal, ended: true, count: 1));
        log.Dispose();
        var lines = File.ReadAllLines(log.Path);
        Check.Require(calls == 1 && log.HasFailed && lines.Count(line => line.Contains(nameof(RunEventKind.Failed))) == 1 &&
            lines.Any(line => line.Contains(nameof(RunEvent.Ended))) && !lines.Any(line => line.Contains("private-message")),
            "throwing progress is diagnosed once without interrupting subsequent stage work or leaking its message");
    }

    private static void FailureLocation(string folder)
    {
        using var log = Open(folder, "failure-location", []);
        try { ThrowItemFailure(); }
        catch (ItemFailedException error) { log.Failed(error); }
        Exception original;
        try { ThrowOriginalFailure(); throw new InvalidOperationException(); }
        catch (IOException error) { original = error; }
        try { throw new ItemFailedException("Parent/Wrapped", TextKey.FailChanged, original); }
        catch (ItemFailedException error) { log.Failed(error); }
        log.Failed(new AggregateException(original));
        var text = ReadShared(log.Path);
        Check.Require(text.Split(Environment.NewLine).Count(line => line.Contains(nameof(ThrowOriginalFailure) + ":")) == 2,
            "wrapped and unthrown aggregate failures retain the original throw method and line");
        Check.Require(text.Contains(nameof(RunLogChecks) + "." + nameof(ThrowItemFailure) + ":") &&
            text.Contains("Parent/Child") && !text.Contains("private-message") && !text.Contains("RunLogChecks.cs"),
            "failed record contains the throw method and line plus carried item path, without source paths or arbitrary messages");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowItemFailure() => throw new ItemFailedException("Parent/Child", TextKey.FailChanged,
        new IOException("private-message"));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowOriginalFailure() => throw new IOException("private-message");

    private static void OrderedProgress(string folder)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var messages = new List<string>();
        using var log = RunLog.Open(folder, "ordered", "ordered", message =>
        {
            messages.Add(message);
            if (messages.Count == 1)
            {
                entered.Set();
                Check.Require(release.Wait(TimeSpan.FromSeconds(10)), "release blocked projection");
            }
        }, BufferCharacters);
        log.Observe(new ListingProgress(Side.Local, "First", 1, 1, 0));
        log.Observe(new ListingProgress(Side.Dropbox, "Stale", 1, 1, 0));
        var quiet = Task.Run(() => log.ReportProgress());
        Task? completion = null;
        using var completing = new ManualResetEventSlim();
        try
        {
            Check.Require(entered.Wait(TimeSpan.FromSeconds(10)), "quiet projection entered callback");
            completion = Task.Run(() =>
            {
                completing.Set();
                Complete(log);
            });
            Check.Require(completing.Wait(TimeSpan.FromSeconds(10)), "completion is scheduled");
            Check.Require(!completion.Wait(TimeSpan.FromMilliseconds(100)),
                "completion joins an admitted callback rather than returning ahead of it");
            // The transport join must not hold the state/file gate.
            Check.Require(!log.HasFailed, "state remains readable during the transport join");
        }
        finally { release.Set(); }
        quiet.GetAwaiter().GetResult();
        completion?.GetAwaiter().GetResult();
        Check.Require(messages.Count == 2 && messages[^1].Contains("FINISHED", StringComparison.Ordinal) &&
            !messages.Skip(1).Any(message => message.Contains("Stale")),
            "an admitted quiet snapshot is delivered once and completion publishes its summary afterwards");
        Check.Require(!log.ReportProgress(), "completion removes the quiet snapshot");
        RunLog? reentrant = null;
        var nested = new List<string>();
        using (reentrant = RunLog.Open(folder, "nested", "nested", message =>
        {
            nested.Add(message);
            if (nested.Count == 1) reentrant!.Event(RunEvent.Stage(TextKey.StageRows));
        }, BufferCharacters))
        {
            reentrant.Observe(new ListingProgress(Side.Local, "First", 1, 1, 0));
            reentrant.Observe(new ListingProgress(Side.Dropbox, "Stale", 1, 1, 0));
            reentrant.ReportProgress();
            Check.Require(nested.Count == 2 && !nested[^1].Contains("Stale"),
                "a newer stage cannot be overwritten by the remainder of a reentrant quiet delivery");
        }
    }

    private static void Complete(RunLog log) => log.Complete(new RepairSummary(log.RunId,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, false, null, false,
        log.Effects, log.IssuesByKind, log.Path, log.HasFailed));

    private static void ConcurrentBoundaries(string folder)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var messages = new List<string>();
        using var log = RunLog.Open(folder, "history", "history", message =>
        {
            messages.Add(message);
            if (messages.Count == 1)
            {
                entered.Set();
                Check.Require(release.Wait(TimeSpan.FromSeconds(10)), "release history subscriber");
            }
        }, BufferCharacters);
        log.Observe(new ListingProgress(Side.Local, "LocalFinal", 7, 1, 0));
        log.Observe(new ListingProgress(Side.Dropbox, "RemoteFinal", 9, 1, 0));
        var blocker = Task.Run(() => log.Event(new RunEvent(RunEventKind.Stage, "", "", "blocked", "")));
        var local = new Thread(() => log.FinishListing(Side.Local));
        var remote = new Thread(() => log.FinishListing(Side.Dropbox));
        var stage = new Thread(() => log.Event(new RunEvent(RunEventKind.Stage, "", "", "boundary", "")));
        var wait = new Thread(() => log.Wait(new WaitEvent(TextKey.WaitTooManyRequests, TimeSpan.FromSeconds(3))));
        var producers = new[] { local, remote, stage, wait };
        try
        {
            Check.Require(entered.Wait(TimeSpan.FromSeconds(10)), "history subscriber is blocked");
            foreach (var producer in producers) producer.Start();
            Check.Require(SpinWait.SpinUntil(() => producers.All(producer =>
                (producer.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0), TimeSpan.FromSeconds(10)),
                "all concurrent boundary producers are waiting behind the subscriber");
        }
        finally
        {
            release.Set();
            foreach (var producer in producers)
                if (producer.ThreadState != System.Threading.ThreadState.Unstarted)
                    Check.Require(producer.Join(TimeSpan.FromSeconds(10)), "boundary producer drains");
        }
        blocker.GetAwaiter().GetResult();
        Check.Require(messages.Count == 5 && messages.Count(message => message.Contains("LocalFinal")) == 1 &&
            messages.Count(message => message.Contains("RemoteFinal")) == 1 &&
            messages.Any(message => message.Contains("boundary", StringComparison.Ordinal)),
            "both final counts, stage boundary and Wait survive concurrent delivery without duplication");
        Check.Require(!log.ReportProgress(), "finished listings leave no stale quiet data");
    }

    private static void CompletionBeforePendingProgress(string folder)
    {
        using var entered = new ManualResetEventSlim();
        using var completeNow = new ManualResetEventSlim();
        var messages = new List<string>();
        RunLog? log = null;
        using (log = RunLog.Open(folder, "pending", "pending", message =>
        {
            messages.Add(message);
            if (messages.Count != 1) return;
            entered.Set();
            Check.Require(completeNow.Wait(TimeSpan.FromSeconds(10)), "release terminal boundary");
            Complete(log!);
        }, BufferCharacters))
        {
            log.Observe(new ListingProgress(Side.Local, "Pending", 1, 1, 0));
            var blocker = Task.Run(() => log.Event(new RunEvent(RunEventKind.Stage, "", "", "blocked", "")));
            var pending = new Thread(() => log.ReportProgress());
            try
            {
                Check.Require(entered.Wait(TimeSpan.FromSeconds(10)), "subscriber owns transport");
                pending.Start();
                Check.Require(SpinWait.SpinUntil(() =>
                    (pending.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)),
                    "quiet delivery is pending at transport admission");
            }
            finally { completeNow.Set(); }
            blocker.GetAwaiter().GetResult();
            Check.Require(pending.Join(TimeSpan.FromSeconds(10)), "pending quiet delivery drains");
            Check.Require(messages.Count == 2 && messages[0].Contains("blocked", StringComparison.Ordinal) &&
                messages[1].Contains("FINISHED", StringComparison.Ordinal),
                "no pending quiet callback starts after completion returns");
        }
    }

    private static RunLog Open(string folder, string name, List<string> progress) =>
        RunLog.Open(folder, name, name, progress.Add, BufferCharacters);

    private static string[] Read(RunLog log)
    {
        log.Dispose();
        return File.ReadAllText(log.Path).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[] Fields(string line) => line.Split(RunLog.FieldSeparator);

    private static void LineFormat(string folder)
    {
        var progress = new List<string>();
        var log = Open(folder, "line", progress);
        log.Event(new RunEvent(RunEventKind.Stage, "StageRows", "", "reading", ""));
        var lines = Read(log);
        Check.Require(lines.Length == 1, nameof(RunEventKind.Stage));
        var fields = Fields(lines[0]);
        Check.Require(fields.Length == 6, nameof(RunLog.FieldSeparator));
        Check.Require(DateTimeOffset.Parse(fields[0], CultureInfo.InvariantCulture).Offset == TimeSpan.Zero,
            nameof(DateTimeOffset));
        Check.Require(fields[1] == RunEventKind.Stage.ToString(), nameof(RunEventKind.Stage));
        Check.Require(fields[2] == "StageRows" && fields[3] == "" && fields[4] == "reading" && fields[5] == "",
            nameof(RunEvent));
        Check.Require(Path.GetFileName(log.Path) == "line-line" + ResourceNames.LogExtension, nameof(RunLog.Path));
        Check.Require(log.RunId == "line", nameof(RunLog.RunId));
        Check.Require(progress.Count == 1 && progress[0].Contains("COMPARING AND SYNCING ITEMS", StringComparison.Ordinal),
            nameof(RunLog.Event));
        Check.Require(!log.HasFailed, nameof(RunLog.HasFailed));
    }

    private static void Counts(string folder)
    {
        var progress = new List<string>();
        var log = Open(folder, "counts", progress);
        var uploaded = EffectKind.Uploaded.ToString();
        var failure = IssueKind.Failure.ToString();
        log.Event(new RunEvent(RunEventKind.Effect, uploaded, "a", "", Side.Local.ToString()));
        log.Event(new RunEvent(RunEventKind.Effect, uploaded, "b", "", Side.Local.ToString()));
        log.Event(new RunEvent(RunEventKind.Issue, failure, "c", "", TextKey.FailChanged.ToString()));
        log.Event(new RunEvent(RunEventKind.Stage, "StageRows", "", "reading", ""));
        Check.Require(log.Effects.Count == 1 && log.Effects[uploaded] == 2, nameof(RunLog.Effects));
        Check.Require(log.IssuesByKind.Count == 1 && log.IssuesByKind[failure] == 1, nameof(RunLog.IssuesByKind));
        Check.Require(progress.Count == 2, nameof(RunLog.Event));
        Read(log);
    }

    private static void Escaping(string folder)
    {
        var log = Open(folder, "escape", new List<string>());
        log.Event(new RunEvent(RunEventKind.Stage, "a\tb", "c\nd", "e\rf\\g", "h"));
        log.Event(new RunEvent(RunEventKind.Stage, "plain", "", "\tab", ""));
        var lines = Read(log);
        var fields = Fields(lines[0]);
        Check.Require(fields.Length == 6, nameof(RunLog.FieldSeparator));
        Check.Require(fields[2] == "a\\tb", nameof(RunLog.EscapeTab));
        Check.Require(fields[3] == "c\\nd", nameof(RunLog.EscapeNewLine));
        Check.Require(fields[4] == "e\\rf\\\\g", nameof(RunLog.EscapeReturn) + nameof(RunLog.EscapeBackslash));
        var plain = Fields(lines[1]);
        Check.Require(plain.Length == 6, nameof(RunLog.FieldSeparator));
        Check.Require(plain[2] == "plain" && plain[3] == "" && plain[4] == "\\tab" && plain[5] == "",
            nameof(RunLog.EscapeTab));
    }

    private static void WaitLine(string folder)
    {
        var progress = new List<string>();
        var log = Open(folder, "wait", progress);
        log.Wait(new WaitEvent(TextKey.WaitTooManyRequests, TimeSpan.FromSeconds(3)));
        var fields = Fields(Read(log)[0]);
        var detail = Text.Get(TextKey.WaitTooManyRequests, (TextArg.Seconds, "3"));
        Check.Require(fields.Length == 6, nameof(RunLog.FieldSeparator));
        Check.Require(fields[1] == RunEventKind.Wait.ToString() && fields[2] == TextKey.WaitTooManyRequests.ToString(),
            nameof(RunEventKind.Wait));
        Check.Require(fields[3] == "" && fields[4] == detail && fields[5] == "3", nameof(WaitEvent));
        Check.Require(progress.Count == 1 && progress[0].Contains(detail, StringComparison.Ordinal),
            nameof(RunLog.Wait));
    }

    // Stage, Issue, Failed and the completion line reach the file before Dispose; routine lines need not.
    private static void FlushedBoundaries(string folder)
    {
        var progress = new List<string>();
        var log = Open(folder, "boundaries", progress);
        Check.Require(ReadShared(log.Path) == "", nameof(RunLog.Open));

        log.Event(new RunEvent(RunEventKind.Stage, "StageRows", "", "reading", ""));
        Check.Require(ReadShared(log.Path).Contains("StageRows", StringComparison.Ordinal),
            nameof(RunEventKind.Stage) + " flushes before Dispose");

        var failure = IssueKind.Failure.ToString();
        log.Event(new RunEvent(RunEventKind.Issue, failure, "c", "", TextKey.FailChanged.ToString()));
        Check.Require(ReadShared(log.Path).Contains(failure, StringComparison.Ordinal),
            nameof(RunEventKind.Issue) + " flushes before Dispose");

        log.Event(new RunEvent(RunEventKind.Failed, nameof(OperationCanceledException), "", "stopped", ""));
        Check.Require(ReadShared(log.Path).Contains(nameof(OperationCanceledException), StringComparison.Ordinal),
            nameof(RunEventKind.Failed) + " flushes before Dispose");

        log.Complete(new RepairSummary("boundaries", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, false,
            null, false, log.Effects, log.IssuesByKind, log.Path, log.HasFailed));
        Check.Require(ReadShared(log.Path).Contains(RunLog.CompleteKind, StringComparison.Ordinal),
            nameof(RunLog.Complete) + " flushes before Dispose");
        log.Dispose();
    }

    private static void AggregatedListing(string folder)
    {
        var messages = new List<string>();
        using var log = Open(folder, "listing", messages);
        log.Event(RunEvent.Stage(TextKey.StageLocalWalk));
        var initial = ReadShared(log.Path);
        foreach (var count in new[] { 1L, 2L, 3L })
            log.Observe(new ListingProgress(Side.Local, "Folder", count, count, 0));
        Check.Require(ReadShared(log.Path) == initial && messages.Count == 1,
            "listing callbacks neither write nor flush nor notify per folder");
        Check.Require(log.ReportProgress(), "quiet signal publishes latest facts");
        Check.Require(messages.Count == 2 && messages[^1].Contains("3", StringComparison.Ordinal),
            "quiet signal renders the active stage and only the latest listing facts");
        log.Observe(new ListingProgress(Side.Local, "Last", 4, 5, 0));
        log.FinishListing(Side.Local);
        Check.Require(ReadShared(log.Path).Contains("Last") && messages[^1].Contains("5"),
            "final consumed counts are immediately readable");
        var beforeQuiet = messages.Count;
        Check.Require(log.ReportProgress() && messages.Count == beforeQuiet + 1 &&
            !messages[^1].Contains("Last"), "completed listings leave only the still-active stage snapshot");
        log.Event(RunEvent.Stage(TextKey.StageLocalWalk, ended: true, count: 5));
        Check.Require(!log.ReportProgress(), "completed stages and listings leave no stale quiet snapshot");
        Check.Require(ReadShared(log.Path).Contains("Ended"), "real end boundary is immediate");
    }

    private sealed class StageClock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    private static void StageDuration(string folder)
    {
        var clock = new StageClock();
        using var log = RunLog.Open(folder, "duration", "duration", _ => { }, BufferCharacters, clock);
        log.Event(RunEvent.Stage(TextKey.StageRows));
        clock.Ticks = TimeSpan.FromSeconds(7).Ticks;
        log.Event(RunEvent.Stage(TextKey.StageRows, ended: true, count: 3));
        var fields = Fields(ReadShared(log.Path).Split(Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries)[^1]);
        Check.Require(fields[4] == Text.Get(TextKey.StageEnded,
            (TextArg.Name, Text.Get(TextKey.StageRows)), (TextArg.Count, "3"), (TextArg.Seconds, "7")),
            "the owner renders actual monotonic duration and semantic count");
    }

    private static void ProviderRequestLifetime(string folder)
    {
        var clock = new StageClock();
        var messages = new List<string>();
        using var log = RunLog.Open(folder, "requests", "requests", messages.Add, BufferCharacters, clock);
        var request = new RunEvent(RunEventKind.ProviderRequest, Massdex.SyncShark.Engine.Dropbox.DropboxProtocol.GetMetadata,
            "Parent/Child\nname", string.Empty, string.Empty) { RequestId = new object() };
        log.Event(request);
        Check.Require(messages.Count == 0 && ReadShared(log.Path).Length == 0,
            "request start is an in-memory fact, not a second per-request record");
        clock.Ticks = TimeSpan.FromSeconds(7).Ticks;
        Check.Require(log.ReportProgress() && messages.Count == 1 && messages[0].Contains("waiting") &&
            messages[0].Contains("7 seconds") &&
            !messages[0].Contains("Child\nname", StringComparison.Ordinal),
            "quiet request snapshot shows elapsed wait and safely renders the subject");
        log.Event(request with { Ended = true, RequestResult = RequestResult.Success });
        Check.Require(messages.Count == 1 && !log.ReportProgress(),
            "a terminal request stays diagnostic; only the quiet waiting snapshot reached the human callback");
        Check.Require(ReadShared(log.Path).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length == 2,
            "only the requested quiet snapshot and terminal request record are written");
    }

    private static void ProviderReason(string folder)
    {
        using var log = Open(folder, "provider", []);
        var evidence = new ProviderFailureException("files/upload_session/finish_batch_v2", 200, "too_many_write_operations");
        var failure = new ItemFailedException("Item", TextKey.DropboxRefused, evidence);
        log.Event(RunEvent.Issue(IssueKind.Failure, TextKey.DropboxRefused, "Item", failure));
        log.Failed(failure);
        log.Failed(new IOException("secret-token-must-not-appear"));
        log.Failed(new ProviderFailureException(evidence.Route, 409, "bad\r\nAuthorization: secret-token-must-not-appear"));
        var text = ReadShared(log.Path);
        Check.Require(text.Contains(evidence.Route) && text.Contains("200") && text.Contains(evidence.Tag),
            "failure and committed issue keep actual provider route, HTTP status and tag");
        Check.Require(!text.Contains("secret-token-must-not-appear"), "arbitrary exception messages are not logged");
        log.Event(RunEvent.Stage(TextKey.StageRows));
        log.Complete(new RepairSummary("provider", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            false, false, null, false, log.Effects, log.IssuesByKind, log.Path, log.HasFailed));
        var lines = Read(log);
        Check.Require(Fields(lines[^2])[5] == nameof(RunEventKind.Failed) &&
            Fields(lines[^1])[1] == RunLog.CompleteKind,
            "interrupted stage closes before the final summary, including after disposal");
    }

    private static void CompleteLine(string folder)
    {
        var log = Open(folder, "done", new List<string>());
        log.Event(new RunEvent(RunEventKind.Effect, EffectKind.Uploaded.ToString(), "a", "", Side.Local.ToString()));
        log.Event(new RunEvent(RunEventKind.Issue, IssueKind.Failure.ToString(), "b", "", TextKey.FailChanged.ToString()));
        log.Complete(new RepairSummary("done", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, false, null,
            false, log.Effects, log.IssuesByKind, log.Path, log.HasFailed));
        var lines = Read(log);
        var last = Fields(lines[^1]);
        Check.Require(last[1] == RunLog.CompleteKind, nameof(RunLog.CompleteKind));
        Check.Require(last[5] == "true", nameof(RepairSummary.Completed));
        Check.Require(last[4].Contains("Uploaded=1") && last[4].Contains("Failure=1"), nameof(RunLog.Complete));
        Check.Require(!log.HasFailed, nameof(RunLog.HasFailed));

        var incomplete = Open(folder, "incomplete", new List<string>());
        incomplete.Complete(new RepairSummary("incomplete", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            false, false, null, false, incomplete.Effects, incomplete.IssuesByKind, incomplete.Path,
            incomplete.HasFailed));
        var stopped = Fields(Read(incomplete)[0]);
        Check.Require(stopped[1] == RunLog.CompleteKind && stopped[4] == "" && stopped[5] == "false",
            nameof(RepairSummary.Completed));
    }

    // The completion line is not terminal: a late failure after finishing still appends Failed.
    private static void NonterminalComplete(string folder)
    {
        var log = Open(folder, "late", new List<string>());
        log.Complete(new RepairSummary("late", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, false, null,
            false, log.Effects, log.IssuesByKind, log.Path, log.HasFailed));
        log.Event(new RunEvent(RunEventKind.Failed, nameof(IOException), "", "after completion", ""));
        var lines = Read(log);
        Check.Require(lines.Length == 2, "a late failure appends after the completion line");
        Check.Require(Fields(lines[0])[1] == RunLog.CompleteKind, nameof(RunLog.CompleteKind));
        var failed = Fields(lines[1]);
        Check.Require(failed[1] == nameof(RunEventKind.Failed) && failed[2] == nameof(IOException) &&
            failed[3] == "" && failed[4] == "after completion" && failed[5] == "", nameof(RunEventKind.Failed));
        Check.Require(!log.HasFailed, nameof(RunLog.HasFailed));
    }

    // A cancelled run writes no completion line; its cancelled and remaining buffered lines survive disposal.
    private static void Cancellation(string folder)
    {
        var progress = new List<string>();
        var log = Open(folder, "cancel", progress);
        log.Event(new RunEvent(RunEventKind.Failed, nameof(OperationCanceledException), "", "cancelled", ""));
        log.Wait(new WaitEvent(TextKey.WaitTooManyRequests, TimeSpan.FromSeconds(1)));
        var lines = Read(log);
        Check.Require(lines.Length == 2, "a cancelled run keeps its cancellation and wait lines");
        Check.Require(Fields(lines[0])[1] == nameof(RunEventKind.Failed) &&
            Fields(lines[0])[2] == nameof(OperationCanceledException), nameof(RunEventKind.Failed));
        Check.Require(Fields(lines[1])[1] == nameof(RunEventKind.Wait), nameof(RunEventKind.Wait));
        Check.Require(!log.HasFailed, nameof(RunLog.HasFailed));
    }

    private static void RepeatedDisposal(string folder)
    {
        var log = Open(folder, "twice", new List<string>());
        log.Event(new RunEvent(RunEventKind.Stage, "StageRows", "", "reading", ""));
        log.Dispose();
        var written = File.ReadAllText(log.Path);
        log.Dispose();
        Check.Require(File.ReadAllText(log.Path) == written, "a repeated disposal writes nothing more");
        Check.Require(!log.HasFailed, nameof(RunLog.HasFailed));
    }

    private static void OpenFailure(string folder)
    {
        var blocker = Path.Combine(folder, "blocked");
        File.WriteAllText(blocker, "");
        var progress = new List<string>();
        var log = RunLog.Open(blocker, "fail", "fail", progress.Add, BufferCharacters);
        Check.Require(log.HasFailed, nameof(RunLog.HasFailed));
        Check.Require(Path.GetFileName(log.Path) == "fail-fail" + ResourceNames.LogExtension, nameof(RunLog.Path));
        log.Event(new RunEvent(RunEventKind.Effect, EffectKind.Downloaded.ToString(), "a", "", Side.Dropbox.ToString()));
        log.Event(new RunEvent(RunEventKind.Stage, "StageRows", "", "reading", ""));
        log.Wait(new WaitEvent(TextKey.WaitTooManyRequests, TimeSpan.FromSeconds(1)));
        log.Complete(new RepairSummary("fail", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, false, null,
            false, log.Effects, log.IssuesByKind, log.Path, log.HasFailed));
        Check.Require(log.HasFailed, nameof(RunLog.HasFailed));
        Check.Require(log.Effects[EffectKind.Downloaded.ToString()] == 1, nameof(RunLog.Effects));
        Check.Require(progress.Count == 3 && progress[0].Contains("COMPARING AND SYNCING ITEMS", StringComparison.Ordinal),
            nameof(RunLog.Event) + nameof(RunLog.Wait));
        Check.Require(File.ReadAllText(blocker) == "", "a failed log writes nothing");
        log.Dispose();
        log.Dispose();
        Check.Require(log.HasFailed, nameof(RunLog.HasFailed));
    }

    // The shipped settings, not a local literal, supply the buffer RunLog.Open receives.
    private static int ShippedBufferCharacters()
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        return SettingsParser.Parse(document.RootElement.GetRawText(), null).LogBufferCharacters;
    }

    // The log stays open for writing, so a reader must share write access, as a log viewer would.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd();
    }
}
