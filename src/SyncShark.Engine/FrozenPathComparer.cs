namespace Massdex.SyncShark.Engine;

/// <summary>Path identity as a comparer: equality and hash follow the frozen case table PathNames owns, so a
/// dictionary and <see cref="PathNames.Key"/> agree. Holds no mapping of its own.</summary>
internal sealed class FrozenPathComparer : IEqualityComparer<string>
{
    /// <summary>True when both spellings have the same frozen identity.</summary>
    public bool Equals(string? left, string? right) => PathNames.Same(left, right);

    /// <summary>The frozen identity hash of one path, the same value <see cref="PathNames.Hash"/> returns.</summary>
    public int GetHashCode(string value) => value is null ? default : PathNames.Hash(value);
}
