using System.Text;

namespace Massdex.SyncShark.Engine;

internal static class RelativePaths
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    internal static string JoinSegments(ReadOnlySpan<char> span)
    {
        var builder = new StringBuilder(span.Length);
        var partStart = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != '/' && span[i] != '\\') continue;
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

    internal static bool ValidSegments(ReadOnlySpan<char> span, out bool changed)
    {
        changed = false;
        var partStart = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != '/' && span[i] != '\\') continue;
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
                if (i != span.Length && span[i] == '\\') changed = true;
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
               stem.Length == "COM1".Length &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[^1] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}
