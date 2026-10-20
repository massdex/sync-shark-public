using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Orders strings by their UTF-8 bytes, matching SQLite's BINARY collation.</summary>
internal sealed class PathOrder : IComparer<string>
{
    /// <summary>The shared comparer; ordering is stateless.</summary>
    public static PathOrder Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var left = Encoding.UTF8.GetBytes(x);
        var right = Encoding.UTF8.GetBytes(y);
        var shared = Math.Min(left.Length, right.Length);
        for (var i = 0; i < shared; i++)
        {
            var diff = left[i].CompareTo(right[i]);
            if (diff != 0) return diff;
        }
        return left.Length.CompareTo(right.Length);
    }
}
