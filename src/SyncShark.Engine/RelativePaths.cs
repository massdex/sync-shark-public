using System.Text;

namespace Massdex.SyncShark.Engine;

internal static class RelativePaths
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    internal static string NormalizeRelative(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var span = value.AsSpan();
        var start = 0;
        var end = span.Length;
        while (start < end && (span[start] == '/' || span[start] == '\\')) start++;
        while (end > start && (span[end - 1] == '/' || span[end - 1] == '\\')) end--;
        if (start == end) throw new ArgumentException(Text.Get(TextKey.RelativePathRequired), nameof(value));
        var trimmed = span[start..end];
        if (Path.IsPathRooted(trimmed)) throw new ArgumentException(Text.Get(TextKey.RelativePathRequired), nameof(value));

        if (!ValidSegments(span, start, end, out var changed))
            throw new ArgumentException(Text.Get(TextKey.UnsafePathSegment), nameof(value));

        if (!changed) return value;
        var builder = new StringBuilder(trimmed.Length);
        var partStart = start;
        for (var i = start; i <= end; i++)
        {
            if (i != end && span[i] != '/' && span[i] != '\\') continue;
            var part = span[partStart..i];
            if (part.Length > 0)
            {
                if (builder.Length > 0) builder.Append('/');
                builder.Append(part);
            }
            partStart = i + 1;
        }
        return builder.ToString();
    }

    private static bool ValidSegments(ReadOnlySpan<char> span, int start, int end, out bool changed)
    {
        changed = start > 0 || end < span.Length;
        var partStart = start;
        for (var i = start; i <= end; i++)
        {
            if (i != end && span[i] != '/' && span[i] != '\\') continue;
            var part = span[partStart..i];
            if (part.Length == 0)
            {
                changed = true;
            }
            else
            {
                if (part.SequenceEqual(".") || part.SequenceEqual("..") ||
                    part.IndexOfAny(InvalidFileNameChars) >= 0 || IsUnsupportedWindowsSegment(part))
                    return false;
                if (i != end && span[i] == '\\') changed = true;
            }
            partStart = i + 1;
        }
        return true;
    }

    private static bool IsUnsupportedWindowsSegment(ReadOnlySpan<char> value)
    {
        if (value.EndsWith(" ") || value.EndsWith(".")) return true;
        var dot = value.IndexOf('.');
        var stem = dot < 0 ? value : value[..dot];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}
