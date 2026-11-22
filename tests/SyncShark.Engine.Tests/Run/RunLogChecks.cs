using System.Text;
using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Contract checks for RunLog: every event reaches the file seam, a buffered record is flushed on dispose,
/// a file failure never reaches the run, and the detached read needs only the file seam (F029, F054).</summary>
public static class RunLogChecks
{
    public static Task RunAsync()
    {
        WritesEachEventThroughTheFileSeam();
        DisposeFlushesBufferedRecords();
        WriteAfterDisposeIsIgnored();
        FileFailureStopsWritingWithoutThrowing();
        ReadIsDetachedFromAnyWriter();
        ReadForwardsStatusRecordsAndTrailing();
        return Task.CompletedTask;
    }

    private static EngineSettings Settings(int bufferCharacters = 4096) => new(
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        30, FirstRunPolicy.NewestWins, 0, string.Empty, bufferCharacters, 512);

    private static RunEvent Stage(string what) => new(RunEventKind.Stage, what, string.Empty, string.Empty, string.Empty);

    private static void WritesEachEventThroughTheFileSeam()
    {
        var files = new FakeLogFiles();
        using var log = new RunLog(files, "logs/pair-run.log", Settings(8192));
        log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", "copied", "Dropbox")
        {
            Count = 2,
        });
        log.Write(Stage("Reading both sides") with { Ended = true, Count = 1000 });

        Check.Require(files.OpenCount == 1, "the writer opens the file exactly once");
        Check.Require(files.OpenedPath == "logs/pair-run.log", "the writer opens the run's own path");
        Check.Require(files.OpenedBuffer == 8192, "the configured character buffer reaches the seam");
        Check.Require(files.Written.Count == 2, "both events reach the file");
        Check.Require(files.Written[0].StartsWith("Effect\t", StringComparison.Ordinal),
            "the per-file line starts with its kind so a reader can select it");
        Check.Require(files.Written[1].StartsWith("Stage\t", StringComparison.Ordinal), "the stage line starts with its kind");
        Check.Require(files.Written[1].Contains("1000", StringComparison.Ordinal), "the stage count is logged");
    }

    private static void DisposeFlushesBufferedRecords()
    {
        var files = new FakeLogFiles();
        var log = new RunLog(files, "logs/pair-run.log", Settings());
        // An effect line is buffered, not flushed at write time.
        log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", string.Empty, "Dropbox"));
        Check.Require(files.Written.Count == 0, "an effect line is buffered until a flush");
        log.Dispose();
        Check.Require(files.Written.Count == 1, "dispose flushes the buffered record instead of stranding it");
        Check.Require(files.Writer is { Disposed: true }, "dispose closes the writer");
    }

    private static void WriteAfterDisposeIsIgnored()
    {
        var files = new FakeLogFiles();
        var log = new RunLog(files, "logs/pair-run.log", Settings());
        log.Dispose();
        log.Write(Stage("late"));
        Check.Require(files.Written.Count == 0, "a write after dispose is ignored, never thrown");
    }

    private static void FileFailureStopsWritingWithoutThrowing()
    {
        var failingWire = new FakeLogFiles { FailWrites = true };
        using (var log = new RunLog(failingWire, "logs/pair-run.log", Settings()))
        {
            log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/report.txt", string.Empty, "Dropbox"));
            log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha/other.txt", string.Empty, "Dropbox"));
        }
        Check.Require(failingWire.Written.Count == 0, "a write failure is contained and stops later writes");

        var failingOpen = new FakeLogFiles { FailOpen = true };
        using (var log = new RunLog(failingOpen, "logs/pair-run.log", Settings()))
        {
            log.Write(Stage("Reading both sides"));
        }
        Check.Require(failingOpen.Written.Count == 0, "an open failure is contained and stops writes");
    }

    private static void ReadIsDetachedFromAnyWriter()
    {
        var files = new FakeLogFiles { Lines = ["alpha"], Malformed = false };
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
        public string? OpenedPath { get; private set; }
        public int? OpenedBuffer { get; private set; }
        public int OpenCount { get; private set; }
        public int ReadCount { get; private set; }
        public string? ReadPath { get; private set; }
        public int? ReadBuffer { get; private set; }
        public bool FailOpen { get; init; }
        public bool FailWrites { get; init; }
        public bool Malformed { get; init; }
        public bool Trailing { get; init; }
        public ILogFiles.ReadStatus Status { get; init; } = ILogFiles.ReadStatus.Found;
        public string[] Lines { get; init; } = [];
        public IReadOnlyList<string> Written => _written;

        public TextWriter Open(string path, int bufferCharacters)
        {
            OpenCount++;
            OpenedPath = path;
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
        public bool Disposed { get; private set; }
        public override Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (owner.FailWrites) throw new IOException("write failure");
            _pending.Append(value).Append('\n');
        }

        public override void Flush()
        {
            owner.Publish(_pending.ToString());
            _pending.Clear();
        }

        public override void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            owner.Publish(_pending.ToString());
            _pending.Clear();
            base.Dispose();
        }
    }
}
