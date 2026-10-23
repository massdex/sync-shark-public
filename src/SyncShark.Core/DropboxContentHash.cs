using System.Buffers;
using System.Security.Cryptography;

namespace Massdex.SyncShark;

public static class DropboxContentHash
{
    public const int BlockSize = 4 * 1024 * 1024;

    public static string Compute(ReadOnlySpan<byte> input)
    {
        var hasher = new BlockHasher();
        hasher.Append(input);
        return hasher.Finish();
    }

    public static Task<string> ComputeAsync(Stream input, CancellationToken cancellationToken = default) =>
        ComputeAsync(input, SyncContentIdentity.DefaultReadBufferBytes, cancellationToken);

    /// <summary>Streams the input through a pooled read buffer of the given size; the digest never depends on it.</summary>
    public static async Task<string> ComputeAsync(Stream input, int bufferBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferBytes);
        var hasher = new BlockHasher();
        // Pooled: one large buffer per hashed file would put every file's buffer on the large-object heap.
        var buffer = ArrayPool<byte>.Shared.Rent(bufferBytes);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, bufferBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hasher.Append(buffer.AsSpan(0, read));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return hasher.Finish();
    }

    /// <summary>
    /// The one reusable incremental Dropbox block hasher. Its output is preserved exactly: SHA256 over the
    /// concatenated SHA256 of each exact 4 MiB block, the final partial block hashed exactly once, and the
    /// empty aggregate for zero bytes. It retains only the running aggregate and the current block's own
    /// 32-byte state, so an arbitrarily large object can be streamed through bounded memory and the file is
    /// never buffered whole, copied to a temporary file or sought. The caller chooses the chunk sizes; every
    /// chunking of the same bytes produces the same digest.
    /// </summary>
    public sealed class BlockHasher
    {
        private readonly IncrementalHash _aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly IncrementalHash _block = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _blockLength;
        private bool _finished;

        /// <summary>The exact number of bytes appended so far, as a checked 64-bit count.</summary>
        public long TotalBytes { get; private set; }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            if (_finished) throw new ObjectDisposedException(nameof(BlockHasher));
            while (!bytes.IsEmpty)
            {
                var take = Math.Min(BlockSize - _blockLength, bytes.Length);
                _block.AppendData(bytes[..take]);
                _blockLength += take;
                TotalBytes += take;
                bytes = bytes[take..];
                if (_blockLength != BlockSize) continue;
                _aggregate.AppendData(_block.GetHashAndReset());
                _blockLength = 0;
            }
        }

        /// <summary>The exact digest of every appended byte, completed once.</summary>
        public string Finish()
        {
            if (_finished) throw new ObjectDisposedException(nameof(BlockHasher));
            _finished = true;
            // A final partial block is hashed exactly once; an exact multiple of the block size adds no
            // empty block, and zero appended bytes leave the aggregate empty.
            if (_blockLength > 0) _aggregate.AppendData(_block.GetHashAndReset());
            return Convert.ToHexString(_aggregate.GetHashAndReset()).ToLowerInvariant();
        }
    }
}
