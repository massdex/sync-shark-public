using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Deterministic short ids: a 1-4 letter prefix plus 16 lower-case hex of a SHA-256 digest.</summary>
public static class ShortId
{
    /// <summary>First 8 bytes of SHA-256 over the UTF-8 parts joined by newlines, as 16 lower-case hex chars.</summary>
    public static string Hex16(params string[] parts)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts)));
        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }

    /// <summary>A prefixed <see cref="Hex16"/> id; the prefix is 1-4 lower-case letters.</summary>
    public static string Make(string prefix, params string[] parts)
    {
        if (prefix.Length is < 1 or > 4 || !prefix.All(c => c is >= 'a' and <= 'z'))
            throw new ArgumentException("A short id prefix must be 1-4 lower-case letters.", nameof(prefix));
        return prefix + Hex16(parts);
    }
}
