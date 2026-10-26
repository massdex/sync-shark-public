using System.Globalization;

namespace Massdex.SyncShark.V2;

/// <summary>Builds the labelled path of a parked keep-both copy (planner.md section 6).</summary>
public static class KeepBothLabel
{
    private const string ExtToken = "{ext}";

    /// <summary>Renders <paramref name="rule"/>'s format for the clash at <paramref name="path"/> on one side.</summary>
    public static string For(string path, string sideName, DateTimeOffset mtime, LabelRule rule, int attempt)
    {
        var slash = path.LastIndexOf('/');
        var folder = slash < 0 ? string.Empty : path[..(slash + 1)];
        var fileName = slash < 0 ? path : path[(slash + 1)..];
        SplitName(fileName, out var name, out var ext);
        var time = mtime.ToOffset(rule.UtcOffset).ToString(rule.TimeFormat, CultureInfo.InvariantCulture);
        var attemptSuffix = attempt >= 2 ? " (" + attempt + ")" : string.Empty;
        var body = rule.Format
            .Replace("{name}", name)
            .Replace("{side}", sideName)
            .Replace("{time}", time)
            .Replace(ExtToken, attemptSuffix + ext);
        // The suffix goes before {ext}, or at the end of a format without {ext}, so every attempt is a new name.
        return folder + body + (rule.Format.Contains(ExtToken, StringComparison.Ordinal) ? string.Empty : attemptSuffix);
    }

    /// <summary>Splits a file name into the name and its last '.' suffix (a leading-dot name has no suffix).</summary>
    private static void SplitName(string fileName, out string name, out string ext)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0)
        {
            name = fileName;
            ext = string.Empty;
            return;
        }
        name = fileName[..dot];
        ext = fileName[dot..];
    }
}
