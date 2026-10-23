using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>A bounded, conservative three-way merge. Overlapping edits are displayed, never guessed.</summary>
public static class ConflictText
{
    public static string? Decode(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) return null;
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    public static string? Merge(string basis, string a, string b)
    {
        if (a == b) return a;
        if (a == basis) return b;
        if (b == basis) return a;
        var x = Change(basis, a);
        var y = Change(basis, b);
        if (x.Start > y.Start) (x, y) = (y, x);
        // Concurrent insertions at the same point are a conflict, including at EOF.
        if (x.End > y.Start || x.Start == y.Start) return null;
        return basis[..x.Start] + x.Text + basis[x.End..y.Start] + y.Text + basis[y.End..];
    }

    private static (int Start, int End, string Text) Change(string basis, string value)
    {
        var start = 0;
        while (start < basis.Length && start < value.Length && basis[start] == value[start]) start++;
        var end = basis.Length;
        var tail = value.Length;
        while (end > start && tail > start && basis[end - 1] == value[tail - 1]) { end--; tail--; }
        return (start, end, value[start..tail]);
    }
}

public sealed record NoticeComparison(byte[] A, byte[] B, string? BaseText, string? MergedText, string Note,
    string? SnapshotA = null, string? SnapshotB = null);
