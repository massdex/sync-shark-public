using System.Globalization;
using System.Text;
using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Contract checks for RunLog: the stored record is a UTC timestamp plus five reversible fields in six
/// fixed columns; a literal backslash-n never collides with a control newline; a typed outcome survives; a failed
/// write, flush or close still closes the writer exactly once; and the detached read needs only the file seam
/// (F029, F054, M22).</summary>
public static class RunLogChecks
{
    public static Task RunAsync()
    {
        WritesCanonicalTimestampedRecord();
        EscapesLiteralBackslashDistinctFromControl();
        TypedOutcomeSurvives();
        DisposeClosesAfterWriteFailure();
        DisposeClosesAfterFlushFailure();
        CloseFailureIsContained();
        DisposeIsIdempotent();
        WriteAfterDisposeIsIgnored();
        OpenFailureIsContained();
        ReadIsDetachedFromAnyWriter();
        ReadForwardsStatusRecordsAndTrailing();
        return Task.CompletedTask;
    }

    private static EngineSettings Settings(int bufferCharacters = 4096) => new(
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        30, FirstRunPolicy.NewestWins, 0, string.Empty, bufferCharacters, 512);

    private static void WritesCanonicalTimestampedRecord()
    {
        var files = new FakeLogFiles();
        var log = new RunLog(files, "logs/pair-run.log", Settings(8192));
        log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", "copied", "Dropbox"));
        log.Write(new RunEvent(RunEventKind.Wait, "Waiting", string.Empty, string.Empty, string.Empty));
        log.Dispose();

        Check.Require(files.OpenCount == 1 && files.OpenedBuffer == 8192, "the configured buffer reaches the seam");
        Check.Require(files.Written.Count == 2, "both events reach the file");
        var full = files.Written[0].Split(RunLog.FieldSeparator);
        Check.Require(full.Length == RunLog.RecordFieldCount, "a record has six fixed columns");
        Check.Require(DateTimeOffset.TryParse(full[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out var at) && at.Offset == TimeSpan.Zero, "the first column is a UTC round-trip timestamp");
        Check.Require(full[1] == "Effect" && full[2] == "Uploaded" && full[3] == "alpha/report.txt" &&
            full[4] == "copied" && full[5] == "Dropbox", "kind, what, path, detail and outcome keep their positions");
        var empty = files.Written[1].Split(RunLog.FieldSeparator);
        Check.Require(empty.Length == RunLog.RecordFieldCount && empty[3].Length == 0 && empty[4].Length == 0 &&
            empty[5].Length == 0, "empty fields keep their column positions");
    }

    private static void EscapesLiteralBackslashDistinctFromControl()
    {
        var files = new FakeLogFiles();
        var log = new RunLog(files, "logs/pair-run.log", Settings());
        log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "a\\nb", "a\nb", string.Empty));
        log.Dispose();

        var fields = files.Written[0].Split(RunLog.FieldSeparator);
        var escapedLiteral = fields[3];
        var escapedControl = fields[4];
        Check.Require(escapedLiteral == "a\\\\nb", "a literal backslash-n is reversibly escaped");
        Check.Require(escapedControl == "a\\nb", "a control newline is escaped");
        Check.Require(escapedLiteral != escapedControl, "a literal backslash-n never collides with a control newline");
        Check.Require(RunLog.TryUnescape(escapedLiteral, out var literal) && literal == "a\\nb",
            "the literal form round-trips");
        Check.Require(RunLog.TryUnescape(escapedControl, out var control) && control == "a\nb",
            "the control form round-trips");
    }

    private static void TypedOutcomeSurvives()
    {
        var files = new FakeLogFiles();
        var log = new RunLog(files, "logs/pair-run.log", Settings());
        log.Write(new RunEvent(RunEventKind.Issue, "Failure", "alpha/report.txt", "name rejected",
            "FailDropboxNameRejected"));
        log.Dispose();

        var fields = files.Written[0].Split(RunLog.FieldSeparator);
        Check.Require(fields[1] == "Issue" && fields[5] == "FailDropboxNameRejected",
            "a representative typed outcome is stored unchanged");
    }

    private static void DisposeClosesAfterWriteFailure()
    {
        var files = new FakeLogFiles { FailWrites = true };
        var log = new RunLog(files, "logs/pair-run.log", Settings());
        log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", string.Empty, "Dropbox"));
        log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/other.txt", string.Empty, "Dropbox"));
        log.Dispose();
        Check.Require(files.Written.Count == 0, "a write failure stops further writes");
        Check.Require(files.Writer is { DisposeCount: 1 }, "dispose still closes the writer exactly once");
    }

    private static void DisposeClosesAfterFlushFailure()
    {
        var files = new FakeLogFiles { FailFlush = true };
        var log = new RunLog(files, "logs/pair-run.log", Settings());
        log.Write(new RunEvent(RunEventKind.Stage, "Reading both sides", string.Empty, string.Empty, string.Empty));
        log.Dispose();
        Check.Require(files.Writer is { DisposeCount: 1 }, "a failed flush does not strand the handle");
    }

    private static void CloseFailureIsContained()
    {
        var files = new FakeLogFiles { FailClose = true };
        var log = new RunLog(files, "logs/pair-run.log", Settings());
        log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", string.Empty, "Dropbox"));
        log.Dispose();
        Check.Require(files.Writer is { DisposeCount: 1 }, "a close failure is contained and counted once");
    }

    private static void DisposeIsIdempotent()
    {
        var files = new FakeLogFiles();
        var log = new RunLog(files, "logs/pair-run.log", Settings());
        log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", string.Empty, "Dropbox"));
        log.Dispose();
        log.Dispose();
        Check.Require(files.Writer is { DisposeCount: 1 }, "dispose closes the writer only once");
        Check.Require(files.Written.Count == 1, "dispose flushes the buffered record instead of stranding it");
    }

    private static void WriteAfterDisposeIsIgnored()
    {
        var files = new FakeLogFiles();
        var log = new RunLog(files, "logs/pair-run.log", Settings());
        log.Dispose();
        log.Write(new RunEvent(RunEventKind.Stage, "late", string.Empty, string.Empty, string.Empty));
        Check.Require(files.Written.Count == 0, "a write after dispose is ignored, never thrown");
    }

    private static void OpenFailureIsContained()
    {
        var files = new FakeLogFiles { FailOpen = true };
        var log = new RunLog(files, "logs/pair-run.log", Settings());
        log.Write(new RunEvent(RunEventKind.Stage, "Reading both sides", string.Empty, string.Empty, string.Empty));
        log.Dispose();
        Check.Require(files.Written.Count == 0, "an open failure is contained and stops writes");
    }

    private static void ReadIsDetachedFromAnyWriter()
    {
        var files = new FakeLogFiles { Lines = ["alpha"] };
        var records = new List<string>();
        // Static call: no RunLog instance, so no writer-shaped object can be constructed for a read.
        var status = RunLog.Read(files, "logs/pair-run.log", (line, _) => records.Add(line), 4096,
            CancellationToken.None, out _);

        Check.Require(status == ILogFiles.ReadStatus.Found, "the read reports the seam's status");
        Check.Require(records.Count == 1 && records[0] == "alpha", "the read forwards each complete record");
        Check.Require(files.ReadCount == 1 && files.ReadPath == "logs/pair-run.log" && files.ReadBuffer == 4096,
            "the read passes the path and chunk size through the seam");
        Check.Require(files.OpenCount == 0, "the detached read opens no writer");
    }

    private static void ReadForwardsStatusRecordsAndTrailing()
    {
        var files = new FakeLogFiles
        {
            Status = ILogFiles.ReadStatus.NotFound, Trailing = true, Lines = ["a", "b"], Malformed = true,
        };
        var seen = new List<(string Line, bool WellFormed)>();
        var status = RunLog.Read(files, "logs/missing.log", (line, ok) => seen.Add((line, ok)), 1024,
            CancellationToken.None, out var trailing);

        Check.Require(status == ILogFiles.ReadStatus.NotFound, "a missing log is reported as not found");
        Check.Require(trailing, "an unfinished trailing record is reported");
        Check.Require(seen.Count == 3 && seen[0] == ("a", true) && seen[1] == ("b", true) && seen[2].WellFormed == false,
            "well-formed and malformed records both reach the callback");
    }

    private sealed class FakeLogFiles : ILogFiles
    {
        private readonly List<string> _written = [];
        public CapturingWriter? Writer { get; private set; }
        public int OpenCount { get; private set; }
        public int? OpenedBuffer { get; private set; }
        public int ReadCount { get; private set; }
        public string? ReadPath { get; private set; }
        public int? ReadBuffer { get; private set; }
        public bool FailOpen { get; init; }
        public bool FailWrites { get; init; }
        public bool FailFlush { get; init; }
        public bool FailClose { get; init; }
        public bool Malformed { get; init; }
        public bool Trailing { get; init; }
        public ILogFiles.ReadStatus Status { get; init; } = ILogFiles.ReadStatus.Found;
        public string[] Lines { get; init; } = [];
        public IReadOnlyList<string> Written => _written;

        public TextWriter Open(string path, int bufferCharacters)
        {
            OpenCount++;
            OpenedBuffer = bufferCharacters;
            if (FailOpen) throw new IOException("open failure");
            Writer = new CapturingWriter(this);
            return Writer;
        }

        public string? Resolve(string folder, string pairId, string runId) => null;

        public ILogFiles.ReadStatus Read(string path, Action<string, bool> record, int bufferBytes, CancellationToken ct,
            out bool unfinishedTrailingRecord)
        {
            ReadCount++;
            ReadPath = path;
            ReadBuffer = bufferBytes;
            ct.ThrowIfCancellationRequested();
            foreach (var line in Lines) record(line, true);
            if (Malformed) record("undecodable", false);
            unfinishedTrailingRecord = Trailing;
            return Status;
        }

        public void Prune(string folder, TimeSpan olderThan, DateTimeOffset now)
        {
        }

        public void Publish(string text)
        {
            foreach (var line in text.Split('\n'))
                if (line.Length > 0) _written.Add(line);
        }
    }

    private sealed class CapturingWriter(FakeLogFiles owner) : TextWriter
    {
        private readonly StringBuilder _pending = new();
        public int DisposeCount { get; private set; }
        public override Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (owner.FailWrites) throw new IOException("write failure");
            _pending.Append(value).Append('\n');
        }

        public override void Flush()
        {
            if (owner.FailFlush) throw new IOException("flush failure");
            Publish();
        }

        protected override void Dispose(bool disposing)
        {
            if (DisposeCount == 0)
            {
                DisposeCount++;
                if (owner.FailClose) throw new IOException("close failure");
                Publish();
            }
            base.Dispose(disposing);
        }

        private void Publish()
        {
            owner.Publish(_pending.ToString());
            _pending.Clear();
        }
    }
}
