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
    // RunLog retention selects dated run names, not this lifecycle log. Its separate budget
    // is one current and one previous generation, each at most ten MiB.
    internal const long FileBound = 10 * 1024 * 1024;
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
            var stamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var bytes = new UTF8Encoding(false).GetBytes(stamp + " " + OneLine(message) + Environment.NewLine);
            // Deny other writers for the entire size-check/rotation/append transaction. Contention
            // drops a diagnostic rather than blocking shutdown. No separate lock file is needed.
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete);
            if (bytes.Length > FileBound) return;
            if (stream.Length > FileBound - bytes.Length) Rotate(stream, path);
            stream.Position = stream.Length;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch (Exception)
        {
            // A detached diagnostic stream cannot terminate the pair (the existing supervisor rule).
        }
    }

    /// <summary>Records an early host exit using the shared catalog's shutdown reason.</summary>
    public static void HostShutdown(string productRoot, string reasonKey) =>
        Append(productRoot, "host shutdown: " + MessageCatalog.Default.Message(reasonKey));

    /// <summary>Routes a discarded stderr stream to the durable log, one complete line per event.</summary>
    public static TextWriter CreateStderrWriter(string productRoot) =>
        TextWriter.Synchronized(new LineWriter(productRoot));

    private static void Rotate(FileStream current, string path)
    {
        // Keep the current handle throughout rotation so another process cannot append between
        // the budget check and write. Failure propagates to Append's catch: never grow a full file.
        using var previous = new FileStream(path + ".previous", FileMode.Create, FileAccess.Write,
            FileShare.Read | FileShare.Delete);
        current.Position = current.Length > FileBound ? current.Length - FileBound : 0;
        current.CopyTo(previous);
        previous.Flush();
        current.SetLength(0);
    }

    private static string OneLine(string message)
    {
        var text = SyncActivityText.Safe(message, omitPrivateDetails: true);
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
