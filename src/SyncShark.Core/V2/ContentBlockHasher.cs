using System.Security.Cryptography;

namespace Massdex.SyncShark.V2;

/// <summary>Provider-compatible incremental block digest shared by transfer and provider adapters.</summary>
public class ContentBlockHasher
{
    public const int DropboxBlockSize = 4 * 1024 * 1024;
    private readonly IncrementalHash _aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash _block = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _blockLength;
    private bool _finished;

    /// <summary>The exact number of bytes appended so far, as a checked 64-bit count.</summary>
    public long TotalBytes { get; private set; }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_finished) throw new ObjectDisposedException(nameof(ContentBlockHasher));
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
        if (_finished) throw new ObjectDisposedException(nameof(ContentBlockHasher));
        _finished = true;
        // A final partial block is hashed exactly once; an exact multiple of the block size adds no
        // empty block, and zero appended bytes leave the aggregate empty.
        if (_blockLength > 0) _aggregate.AppendData(_block.GetHashAndReset());
        return Convert.ToHexString(_aggregate.GetHashAndReset()).ToLowerInvariant();
    }
}
