using System.Globalization;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Durable, bounded event log shared by the AutoSync supervisor and host. One append-only line per event.
/// A detached launch has no console and the host used to discard its stderr, so this file is the only
/// evidence that survives an external stop; every failure to write it is therefore ignored, never thrown.
/// </summary>
public static class AutoSyncLog
{
    internal const string FileName = "autosync.log";
    // A failing host can write stderr continuously; reuse the run-log byte bound and move the previous
    // generation aside rather than growing forever.
    internal const long FileBound = RunLog.MaximumRunBytes;
    internal const int LongestLine = 2048;

    /// <summary>The log file in the product logs directory that RunLog already owns.</summary>
    public static string FilePath(string productRoot) =>
        Path.Combine(RunLog.LogsDirectory(productRoot, autoSync: true), FileName);

    /// <summary>Appends one UTC-stamped line. Never throws: a diagnostic cannot terminate supervision.</summary>
    public static void Append(string productRoot, string message)
    {
        try
        {
            var path = FilePath(productRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RotateIfFull(path);
            var stamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var bytes = new UTF8Encoding(false).GetBytes(stamp + " " + OneLine(message) + Environment.NewLine);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch (Exception)
        {
            // A detached diagnostic stream cannot terminate the pair (the existing supervisor rule).
        }
    }

    /// <summary>Routes a discarded stderr stream to the durable log, one complete line per event.</summary>
    public static TextWriter CreateStderrWriter(string productRoot) =>
        TextWriter.Synchronized(new LineWriter(productRoot));

    private static void RotateIfFull(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= FileBound) return;
            // One previous generation is enough to explain the latest termination. A concurrent writer
            // can hold the file briefly; a failed move only delays bounding until the next event.
            File.Move(path, path + ".previous", overwrite: true);
        }
        catch (Exception)
        {
            // Bounding is best effort; the event being appended is more valuable than the cap.
        }
    }

    private static string OneLine(string message)
    {
        var text = message.ReplaceLineEndings(" ");
        return text.Length <= LongestLine ? text : text[..LongestLine] + "...";
    }

    private sealed class LineWriter(string productRoot) : TextWriter
    {
        private readonly StringBuilder _line = new();
        public override Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n') { FlushLine(); return; }
            if (value == '\r') return;
            if (_line.Length < LongestLine) _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var character in value) Write(character);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            FlushLine();
        }

        public override void Flush() => FlushLine();

        private void FlushLine()
        {
            if (_line.Length == 0) return;
            var text = _line.ToString();
            _line.Clear();
            Append(productRoot, text);
        }
    }
}
