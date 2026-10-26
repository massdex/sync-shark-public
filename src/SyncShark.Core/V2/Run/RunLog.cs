using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Massdex.SyncShark.V2;

// The background task is the sole owner of files, rotation, flushing and disposal. Producers only TryWrite.
internal sealed class RunLog : RunTrace, IDisposable
{
    internal const string AutoSyncCommand = "autosync";
    internal const int MaximumFiles = 50;
    internal const long MaximumBytes = 200L * 1024 * 1024;
    internal const long MaximumRunBytes = 100L * 1024 * 1024;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> SkippedRotation = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex RunName = new(@"^\d{8}-\d{6}-\d{3}-[0-9a-f]{8}-[\p{L}\p{N}_-]+-[\p{L}\p{N}_-]+\.log$", RegexOptions.CultureInvariant);
    private readonly Channel<(string Text, string Json)> _traces = Queue(10_000);
    private readonly Channel<(string Text, string Json)> _summaries = Queue(256);
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RunTrace? _previous;
    private readonly Task _writer;
    private readonly string _directory;
    private readonly bool _verbose;
    private int _disposed, _failed, _truncated, _dropped, _pendingDrops;
    private long _bytes;
    private volatile bool _fileAvailable;

    private static Channel<(string Text, string Json)> Queue(int capacity) =>
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private RunLog(string root, string pair, string command, bool verbose,
        Func<string, Stream>? open)
    {
        _verbose = verbose;
        _previous = RunTrace.Slot.Value;
        RunTrace.Slot.Value = this;
        try
        {
            _directory = LogsDirectory(root, command == AutoSyncCommand);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var stem = stamp + "-" + Guid.NewGuid().ToString("N")[..8] + "-" + Safe(pair) + "-" + Safe(command);
            Path = System.IO.Path.Combine(_directory, stem + ".log");
            JsonPath = System.IO.Path.Combine(_directory, stem + ".jsonl");
            _writer = Task.Run(() => DrainAsync(open));
        }
        catch (Exception error)
        {
            _directory = string.Empty;
            _writer = Task.CompletedTask;
            Fail(error);
        }
    }

    internal static RunLog? Current => RunTrace.Slot.Value as RunLog;

    /// <summary>The one logs directory under the product root; AutoSync events share the autosync folder.</summary>
    internal static string LogsDirectory(string root, bool autoSync = false) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "v2", "logs", autoSync ? AutoSyncCommand : string.Empty));
    internal string? Path { get; }
    internal string? JsonPath { get; }
    internal bool Verbose => _verbose;
    internal bool FileAvailable => _fileAvailable;
    internal bool Failed => Volatile.Read(ref _failed) != 0;
    internal int DroppedLines => Volatile.Read(ref _dropped);
    internal long ByteLimit { get; set; } = MaximumRunBytes;
    internal Task Completion => _writer;
    internal static RunLog Open(string root, string pair, string command, bool verbose,
        Func<string, Stream>? open = null) => new(root, pair, command, verbose, open);

    internal static Action<string> Sink(RunLog log, Action<string> progress)
    {
        if (log.Path is { } path) RunOutput.Write(progress, "Run log: " + path);
        return line => { RunOutput.Write(progress, line); log.Summary(line); };
    }

    internal static void Trace(string line) => RunTrace.Emit(line);

    internal override void WriteTrace(string line) => Detailed(line);

    internal static string Line(RunProgress value) => value.Message ??
        (string.IsNullOrEmpty(value.Path) ? $"{value.Phase} ({value.Done}/{value.Total}){Extra(value.Extra)}"
            : $"{value.Phase}: {value.Path} ({value.Done}/{value.Total}){Extra(value.Extra)}");

    // The Executor's child results, kept beside the planned done/total rather than inside it.
    private static string Extra(long extra) =>
        extra > 0 ? $" (+{extra:N0} files inside deleted folders)" : string.Empty;

    internal void Summary(string line) => Enqueue("run", line, _summaries);
    internal void Detailed(string line)
    {
        if (_verbose) RunOutput.Write(Console.Error.WriteLine, "Sync Shark trace: " + line);
        Enqueue("trace", line, _traces);
    }

    private static (string Text, string Json) Encode(string kind, string message)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return (stamp + " " + kind + " " + message, JsonSerializer.Serialize(new { ts = stamp, kind, message }));
    }

    private void Enqueue(string kind, string message, Channel<(string Text, string Json)> queue)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || Failed || Volatile.Read(ref _truncated) != 0) return;
            if (!queue.Writer.TryWrite(Encode(kind, message)))
            { Interlocked.Increment(ref _dropped); Interlocked.Increment(ref _pendingDrops); }
        }
        catch (Exception error) { RunOutput.Observe(error); }
    }

    private async Task DrainAsync(Func<string, Stream>? open)
    {
        StreamWriter? text = null, json = null;
        try
        {
            Directory.CreateDirectory(_directory);
            Rotate(_directory, MaximumFiles, MaximumBytes);
            open ??= path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            text = new StreamWriter(open(Path!), new UTF8Encoding(false));
            json = new StreamWriter(open(JsonPath!), new UTF8Encoding(false));
            _fileAvailable = true;
            while (true)
            {
                // No producer wakeups: flush at most once per 500 ms, plus the final dispose flush.
                await Task.WhenAny(Task.Delay(500), _stop.Task).ConfigureAwait(false);
                var stopping = _stop.Task.IsCompleted;
                var dropped = Interlocked.Exchange(ref _pendingDrops, 0);
                if (dropped > 0) Write(Encode("run", $"Run log overflow: dropped {dropped} lines."), text, json);
                // Bound a batch even when producers continuously refill; summaries have reserved capacity and priority.
                for (var n = 0; n < 10_256; n++)
                {
                    if (!_summaries.Reader.TryRead(out var item) && !_traces.Reader.TryRead(out item)) break;
                    Write(item, text, json);
                }
                text.Flush();
                json.Flush();
                if (stopping) break;
            }
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            try { text?.Dispose(); } catch (Exception error) { Fail(error); }
            try { json?.Dispose(); } catch (Exception error) { Fail(error); }
            _fileAvailable = false;
            if (!Failed) Rotate(_directory, MaximumFiles, MaximumBytes);
        }
    }

    private void Write((string Text, string Json) item, StreamWriter text, StreamWriter json)
    {
        if (Volatile.Read(ref _truncated) != 0) return;
        var bytes = Encoding.UTF8.GetByteCount(item.Text + text.NewLine) + Encoding.UTF8.GetByteCount(item.Json + json.NewLine);
        if (_bytes + bytes > ByteLimit)
        {
            // This terminal record is the only bounded allowance beyond the cap, in both formats.
            item = Encode("run", "run log truncated: this run's log reached its size limit.");
            Volatile.Write(ref _truncated, 1);
            bytes = Encoding.UTF8.GetByteCount(item.Text + text.NewLine) + Encoding.UTF8.GetByteCount(item.Json + json.NewLine);
        }
        text.WriteLine(item.Text);
        json.WriteLine(item.Json);
        _bytes += bytes;
    }

    private void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        _fileAvailable = false;
        RunOutput.Write(Console.Error.WriteLine, "Sync Shark: file logging unavailable: " + error.Message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _summaries.Writer.TryComplete();
        _traces.Writer.TryComplete();
        _stop.TrySetResult();
        try { _writer.Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception error) { RunOutput.Observe(error); }
        if (ReferenceEquals(RunTrace.Slot.Value, this)) RunTrace.Slot.Value = _previous;
    }

    internal static void Rotate(string directory, int maximumFiles, long maximumBytes)
    {
        try
        {
            var folder = new DirectoryInfo(directory);
            var logs = folder.Name == AutoSyncCommand ? folder.Parent : folder;
            if (!folder.Exists || logs?.Name != "logs" || logs.Parent?.Name != "v2") return;
            // A junction/symlink must not turn the dedicated directory into a foreign target.
            for (DirectoryInfo? ancestor = folder; ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (SkippedRotation.TryAdd(directory, 0)) RunOutput.Write(Console.Error.WriteLine,
                        "Sync Shark: log rotation skipped: reparse ancestor " + ancestor.FullName);
                    return;
                }
            foreach (var orphan in folder.GetFiles("*.jsonl"))
                if (RunName.IsMatch(System.IO.Path.ChangeExtension(orphan.Name, ".log")) &&
                    (orphan.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    !File.Exists(System.IO.Path.ChangeExtension(orphan.FullName, ".log"))) orphan.Delete();
            var files = folder.GetFiles("*.log").Where(f => RunName.IsMatch(f.Name) &&
                (f.Attributes & FileAttributes.ReparsePoint) == 0).OrderByDescending(f => f.Name, StringComparer.Ordinal).ToArray();
            long bytes = 0;
            for (var n = 0; n < files.Length; n++)
            {
                var file = files[n];
                var twin = new FileInfo(System.IO.Path.ChangeExtension(file.FullName, ".jsonl"));
                if (twin.Exists && (twin.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                bytes += file.Length + (twin.Exists ? twin.Length : 0);
                if (n < maximumFiles && bytes <= maximumBytes) continue;
                file.Delete();
                if (twin.Exists) twin.Delete();
            }
        }
        catch (Exception error) { RunOutput.Observe(error); }
    }

    private static string Safe(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.Take(80)) builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return builder.Length == 0 ? "run" : builder.ToString();
    }
}
