using System.Security.Cryptography;

namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Known Dropbox digest for an empty stream and a chunk boundary split across appends.</summary>
public static class ContentHashChecks
{
    public static Task RunAsync()
    {
        using var empty = new ContentHash();
        Check.Require(empty.Finish() == Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(), "Empty digest");
        var full = new byte[ContentHash.DropboxBlockSize];
        var tail = new byte[] { 1 };
        var expected = SHA256.HashData(SHA256.HashData(full).Concat(SHA256.HashData(tail)).ToArray());
        using var hash = new ContentHash();
        hash.Append(full.AsSpan(0, full.Length - 1)); hash.Append(new byte[] { 0, 1 });
        Check.Require(hash.Finish() == Convert.ToHexString(expected).ToLowerInvariant(), "Block boundary");
        return Task.CompletedTask;
    }
}
