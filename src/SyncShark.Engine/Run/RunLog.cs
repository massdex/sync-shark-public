using System.Globalization;
using System.Text;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>One run's log file. Writes the 0.3.95 stored record: a UTC round-trip timestamp plus the five
/// canonical fields, always six tab-separated columns, each reversible-escaped so a data value can never split a
/// record or collide with an escape. Typed facts (a stage end, a count, a failure's type and reason, a cause, a
/// notice or action) are projected into the record instead of being lost. Important boundaries flush early. A failed
/// write or flush stops further writes but keeps the writer, and <see cref="Dispose"/> closes it exactly once
/// (M22, F029). Reads are a detached entry point needing only the file seam, never a writer (F054).</summary>
public sealed class RunLog : IDisposable
{
    /// <summary>Separates the six record columns; the timestamp is first.</summary>
    public const char FieldSeparator = '\t';
    /// <summary>The canonical record's fixed column count: timestamp, kind, what, path, detail, outcome.</summary>
    public const int RecordFieldCount = 6;
    /// <summary>UTC round-trip timestamp format of the first column.</summary>
    public const string TimestampFormat = "O";
    /// <summary>Outcome recorded for an ended stage.</summary>
    public const string EndedOutcome = "Ended";
    private const string EscapeTab = "\\t";
    private const string EscapeReturn = "\\r";
    private const string EscapeNewLine = "\\n";
    private const string EscapeBackslash = "\\\\";
    private const string UnicodeEscape = "\\u";
    private const string UnicodeFormat = "x4";
    private const int UnicodePrefixLength = 2;
    private const int UnicodeDigitCount = 4;
    private const int UnicodeEscapeLength = 5;
    private const string CountFact = "count";
    private const string TotalFact = "total";
    private const string SideFact = "side";
    private const char FactSeparator = '=';
    private const string FactJoiner = "; ";

    private readonly object _gate = new();
    private readonly string _path;
    private TextWriter? _writer;
    private bool _failed;
    private bool _disposed;

    /// <summary>Opens the run's log file through the writer seam with the configured buffer. A file that cannot be
    /// opened stops logging without failing the run.</summary>
    public RunLog(ILogFiles files, string path, EngineSettings settings)
    {
        _path = path;
        try
        {
            _writer = files.Open(path, settings.LogBufferCharacters);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _failed = true;
        }
    }

    /// <summary>The log file this writer owns.</summary>
    public string Path => _path;

    /// <summary>Appends one canonical record. A failed write stops further writes and never reaches the run.</summary>
    public void Write(RunEvent value)
    {
        lock (_gate)
        {
            if (_disposed || _failed || _writer is null) return;
            try
            {
                _writer.WriteLine(FormatRecord(value));
                if (value.Kind is RunEventKind.Stage or RunEventKind.Issue or RunEventKind.Failed) _writer.Flush();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // Keep the writer so Dispose can close the handle; only stop further writes.
                _failed = true;
            }
        }
    }

    /// <summary>Reads a stored log through the file seam. Independent of any writer, so the CLI can read a stopped
    /// run's file without constructing a writer-shaped object or starting the host (R19, F054).</summary>
    public static ILogFiles.ReadStatus Read(ILogFiles files, string path, Action<string, bool> record, int bufferBytes,
        CancellationToken ct, out bool trailing) => files.Read(path, record, bufferBytes, ct, out trailing);

    /// <summary>Flushes and closes the buffered writer once; a failed write or flush still closes its handle.</summary>
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
            catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                _failed = true;
            }
            finally
            {
                _writer = null;
            }
        }
    }

    /// <summary>Projects typed facts into the fields a record or a readable view shows, so a stage end, a count, a
    /// failure or a cause delivered only in structured fields is never lost.</summary>
    internal static RunEvent Present(RunEvent value)
    {
        var what = value.What;
        if (what.Length == 0)
        {
            if (value.Notice is { } notice) what = notice.ToString();
            else if (value.Action is { } action) what = action.ToString();
            else if (value.Error is { } error) what = error.GetType().Name;
        }
        var detail = value.Detail;
        if (detail.Length == 0 && value.Error is { } failure) detail = failure.GetBaseException().Message;
        if (value.Count is { } count) detail = AppendFact(detail, CountFact, count.ToString(CultureInfo.InvariantCulture));
        if (value.Total is { } total) detail = AppendFact(detail, TotalFact, total.ToString(CultureInfo.InvariantCulture));
        if (value.Side is { } side) detail = AppendFact(detail, SideFact, side.ToString());
        var outcome = value.Outcome;
        if (outcome.Length == 0)
        {
            if (value.Cause is { } cause) outcome = cause.ToString();
            else if (value.Ended) outcome = EndedOutcome;
        }
        return value with { What = what, Detail = detail, Outcome = outcome };
    }

    private static string AppendFact(string detail, string name, string value)
    {
        var fact = name + FactSeparator + value;
        return detail.Length == 0 ? fact : detail + FactJoiner + fact;
    }

    // One canonical record: UTC round-trip timestamp, then kind, what, path, detail and outcome, always six columns.
    private static string FormatRecord(RunEvent value)
    {
        var item = Present(value);
        var fields = new[]
        {
            DateTimeOffset.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            EscapeField(item.Kind.ToString()),
            EscapeField(item.What),
            EscapeField(item.Path),
            EscapeField(item.Detail),
            EscapeField(item.Outcome)
        };
        return string.Join(FieldSeparator, fields);
    }

    /// <summary>Reversibly escapes a stored field: backslash, tab, return, newline and other control characters, so a
    /// literal `\n` and a real newline stay distinct and any record round-trips.</summary>
    public static string EscapeField(string value) => EscapeValue(value, escapeBackslash: true);

    /// <summary>Escapes control characters for a human view, leaving a Windows path's own backslashes readable.</summary>
    public static string EscapeDisplay(string value) => EscapeValue(value, escapeBackslash: false);

    private static string EscapeValue(string value, bool escapeBackslash)
    {
        if (value.Length == 0) return value;
        var first = 0;
        while (first < value.Length && !NeedsEscape(value[first], escapeBackslash)) first++;
        if (first == value.Length) return value;
        var escaped = new StringBuilder(value.Length);
        escaped.Append(value.AsSpan(0, first));
        for (var index = first; index < value.Length; index++)
        {
            var character = value[index];
            if (NeedsEscape(character, escapeBackslash)) escaped.Append(EscapeChar(character));
            else escaped.Append(character);
        }
        return escaped.ToString();
    }

    /// <summary>Reverses <see cref="EscapeField"/>, or reports a malformed value rather than guessing.</summary>
    public static bool TryUnescape(string value, out string result)
    {
        result = value;
        if (value.IndexOf('\\') < 0) return true;
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }
            if (index + 1 >= value.Length) return false;
            switch (value[index + 1])
            {
                case 't': builder.Append('\t'); index++; break;
                case 'r': builder.Append('\r'); index++; break;
                case 'n': builder.Append('\n'); index++; break;
                case '\\': builder.Append('\\'); index++; break;
                case 'u':
                    if (index + UnicodeEscapeLength >= value.Length) return false;
                    if (!ushort.TryParse(value.AsSpan(index + UnicodePrefixLength, UnicodeDigitCount),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) return false;
                    builder.Append((char)code);
                    index += UnicodeEscapeLength;
                    break;
                default: return false;
            }
        }
        result = builder.ToString();
        return true;
    }

    private static bool NeedsEscape(char value, bool escapeBackslash) =>
        char.IsControl(value) || (escapeBackslash && value == '\\');

    private static string EscapeChar(char value) => value switch
    {
        '\\' => EscapeBackslash,
        '\t' => EscapeTab,
        '\r' => EscapeReturn,
        '\n' => EscapeNewLine,
        _ => UnicodeEscape + ((int)value).ToString(UnicodeFormat, CultureInfo.InvariantCulture),
    };
}
