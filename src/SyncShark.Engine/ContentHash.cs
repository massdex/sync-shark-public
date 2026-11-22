using System.Security.Cryptography;

namespace Massdex.SyncShark.Engine;

/// <summary>Provider-compatible incremental block digest shared by transfer and provider adapters.</summary>
public sealed class ContentHash : IDisposable
{
    /// <summary>Dropbox SHA-256 digests are exactly two hexadecimal digits per digest byte.</summary>
    public static bool IsValid(string value) => value.Length == SHA256.HashSizeInBytes * 2 && value.All(Uri.IsHexDigit);

    public const int DropboxBlockSize = 4 * 1024 * 1024;
    private readonly IncrementalHash _aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash _block = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _blockLength;
    private bool _finished;

    /// <summary>The exact number of bytes appended so far, as a checked 64-bit count.</summary>
    public long TotalBytes { get; private set; }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_finished) throw new ObjectDisposedException(nameof(ContentHash));
        while (!bytes.IsEmpty)
        {
            var take = Math.Min(DropboxBlockSize - _blockLength, bytes.Length);
            _block.AppendData(bytes[..take]);
            _blockLength += take;
            TotalBytes += take;
            bytes = bytes[take..];
            if (_blockLength != DropboxBlockSize) continue;
            _aggregate.AppendData(_block.GetHashAndReset());
            _blockLength = 0;
        }
    }

    /// <summary>The exact digest of every appended byte, completed once.</summary>
    public string Finish()
    {
        if (_finished) throw new ObjectDisposedException(nameof(ContentHash));
        _finished = true;
        // A final partial block is hashed exactly once; an exact multiple of the block size adds no
        // empty block, and zero appended bytes leave the aggregate empty.
        if (_blockLength > 0) _aggregate.AppendData(_block.GetHashAndReset());
        var digest = Convert.ToHexString(_aggregate.GetHashAndReset()).ToLowerInvariant();
        Dispose();
        return digest;
    }

    /// <summary>Releases the two incremental hashes; Finish does this once the digest is complete.</summary>
    public void Dispose()
    {
        _finished = true;
        _aggregate.Dispose();
        _block.Dispose();
    }
}
