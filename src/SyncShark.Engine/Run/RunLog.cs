using System.Globalization;
using System.Text;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>One run's log file. Each event's human projection is appended through the <see cref="ILogFiles"/> seam,
/// which stays the only code that opens a log file; important boundaries flush early. The buffered writer is
/// released on <see cref="Dispose"/>, which the host calls after callbacks drain on completion, failure and
/// cancellation, so buffered records are never stranded (M22, F029). Reads are a detached entry point needing only
/// the file seam, never a writer (F054).</summary>
public sealed class RunLog : IDisposable
{
    /// <summary>Separates log-line columns; the event kind is always the first column.</summary>
    public const char FieldSeparator = '\t';
    private const string EscapeTab = "\\t";
    private const string EscapeReturn = "\\r";
    private const string EscapeNewLine = "\\n";
    private const string UnicodeEscape = "\\u";
    private const string UnicodeFormat = "x4";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _bufferCharacters;
    private TextWriter? _writer;
    private Exception? _failure;
    private bool _disposed;

    /// <summary>Opens the run's log file through the writer seam with the configured buffer. A file that cannot be
    /// opened stops logging without failing the run.</summary>
    public RunLog(ILogFiles files, string path, EngineSettings settings)
    {
        _path = path;
        _bufferCharacters = settings.LogBufferCharacters;
        try
        {
            _writer = files.Open(path, _bufferCharacters);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _failure = error;
        }
    }

    /// <summary>The log file this writer owns.</summary>
    public string Path => _path;

    /// <summary>Appends one line for the event. A failed file stops further writes and never reaches the run.</summary>
    public void Write(RunEvent value)
    {
        lock (_gate)
        {
            if (_disposed || _failure is not null || _writer is null) return;
            try
            {
                _writer.WriteLine(LogText.Format(value));
                if (value.Kind is RunEventKind.Stage or RunEventKind.Issue or RunEventKind.Failed) _writer.Flush();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                _failure = error;
                _writer = null;
            }
        }
    }

    /// <summary>Reads a stored log through the file seam. Independent of any writer, so the CLI can read a stopped
    /// run's file without constructing a writer-shaped object or starting the host (R19, F054).</summary>
    public static ILogFiles.ReadStatus Read(ILogFiles files, string path, Action<string, bool> record, int bufferBytes,
        CancellationToken ct, out bool trailing) => files.Read(path, record, bufferBytes, ct, out trailing);

    /// <summary>Flushes and closes the buffered writer once; safe after normal completion, failure or cancellation.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_writer is null) return;
            try
            {
                _writer.Dispose();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _failure ??= error;
            }
            _writer = null;
        }
    }

    /// <summary>Escapes control characters in a data value so one event is always one line; a Windows path's own
    /// backslashes stay readable because the stored line is display text, never re-parsed.</summary>
    public static string EscapeField(string value)
    {
        if (value.Length == 0) return value;
        var first = 0;
        while (first < value.Length && !char.IsControl(value[first])) first++;
        if (first == value.Length) return value;
        var escaped = new StringBuilder(value.Length);
        escaped.Append(value.AsSpan(0, first));
        for (var index = first; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character)) escaped.Append(EscapeChar(character));
            else escaped.Append(character);
        }
        return escaped.ToString();
    }

    private static string EscapeChar(char value) => value switch
    {
        '\t' => EscapeTab,
        '\r' => EscapeReturn,
        '\n' => EscapeNewLine,
        _ => UnicodeEscape + ((int)value).ToString(UnicodeFormat, CultureInfo.InvariantCulture),
    };
}
