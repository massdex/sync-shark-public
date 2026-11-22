using System.Globalization;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>The one human projection of a run event into a single readable log line: the event kind first, so a
/// reader can select per-file lines, then each non-empty fact. Data values are escaped by <see cref="RunLog"/> so a
/// path or message can never split one event across lines (D-1105-6, M22).</summary>
public static class LogText
{
    /// <summary>Projects one event to one log line: kind, what, path, detail, outcome, then progress counts.</summary>
    public static string Format(RunEvent value)
    {
        var fields = new List<string>(6) { value.Kind.ToString() };
        Append(fields, value.What);
        Append(fields, value.Path);
        Append(fields, value.Detail);
        Append(fields, value.Outcome);
        Append(fields, value.Count?.ToString(CultureInfo.InvariantCulture));
        Append(fields, value.Total?.ToString(CultureInfo.InvariantCulture));
        return string.Join(RunLog.FieldSeparator, fields);
    }

    private static void Append(List<string> fields, string? value)
    {
        if (!string.IsNullOrEmpty(value)) fields.Add(RunLog.EscapeField(value));
    }
}
