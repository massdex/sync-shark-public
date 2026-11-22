namespace Massdex.SyncShark.Engine.Run;

/// <summary>The one readable projection of a run event for a human view: the event kind, then each non-empty fact,
/// with control characters escaped so one event stays one line. The stored canonical record keeps the timestamp and
/// reversible TSV (see <see cref="RunLog"/>); this projection is for display only (D-1105-6, M22).</summary>
public static class LogText
{
    /// <summary>Projects one event to one readable line: kind, what, path, detail and outcome.</summary>
    public static string Format(RunEvent value)
    {
        var fields = new List<string>(5) { value.Kind.ToString() };
        Append(fields, value.What);
        Append(fields, value.Path);
        Append(fields, value.Detail);
        Append(fields, value.Outcome);
        return string.Join(' ', fields);
    }

    private static void Append(List<string> fields, string? value)
    {
        if (!string.IsNullOrEmpty(value)) fields.Add(RunLog.EscapeDisplay(value));
    }
}
