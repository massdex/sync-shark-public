using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Orders strings by their UTF-8 bytes, matching SQLite's BINARY collation.</summary>
internal sealed class PathOrder : IComparer<string>
{
    /// <summary>The shared comparer; ordering is stateless.</summary>
    public static PathOrder Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (string.Equals(x, y, StringComparison.Ordinal)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var leftLength = Encoding.UTF8.GetByteCount(x);
        var rightLength = Encoding.UTF8.GetByteCount(y);
        Span<byte> left = leftLength <= 1024 ? stackalloc byte[leftLength] : new byte[leftLength];
        Span<byte> right = rightLength <= 1024 ? stackalloc byte[rightLength] : new byte[rightLength];
        Encoding.UTF8.GetBytes(x, left);
        Encoding.UTF8.GetBytes(y, right);
        return left.SequenceCompareTo(right);
    }
}
