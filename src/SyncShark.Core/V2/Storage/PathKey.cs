using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>The stored case-insensitive key of a path or name: equal keys exactly when equal under OrdinalIgnoreCase.</summary>
/// <remarks>
/// . Upper-invariant alone is not it: "ſ".ToUpperInvariant() is "S", yet OrdinalIgnoreCase keeps
/// "ſ" and "S" apart. Each scalar maps to its invariant uppercase when OrdinalIgnoreCase calls the two equal, else to
/// itself; StorageChecks proves the equivalence over every Unicode scalar.
/// </remarks>
internal static class PathKey
{
    private const int AsciiLimit = 0x80;

    /// <summary>The key of a path or name; unpaired surrogates are kept as they are.</summary>
    public static string Of(string value)
    {
        if (IsAscii(value)) return AsciiUpper(value);
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length;)
        {
            if (Rune.TryGetRuneAt(value, i, out var rune))
            {
                builder.Append(Canonical(rune).ToString());
                i += rune.Utf16SequenceLength;
            }
            else
            {
                builder.Append(value[i]);
                i++;
            }
        }
        return builder.ToString();
    }

    // OrdinalIgnoreCase compares simple uppercase forms but leaves out some invariant mappings (ſ → S): the invariant
    // uppercase is the key only where OrdinalIgnoreCase agrees it is the same scalar.
    private static Rune Canonical(Rune rune)
    {
        var upper = Rune.ToUpperInvariant(rune);
        if (upper == rune || rune.Value < AsciiLimit) return upper;
        return string.Equals(rune.ToString(), upper.ToString(), StringComparison.OrdinalIgnoreCase) ? upper : rune;
    }

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
            if (c >= AsciiLimit) return false;
        return true;
    }

    private static string AsciiUpper(string value) => string.Create(value.Length, value, static (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
            span[i] = source[i] is >= 'a' and <= 'z' ? (char)(source[i] - ('a' - 'A')) : source[i];
    });
}
