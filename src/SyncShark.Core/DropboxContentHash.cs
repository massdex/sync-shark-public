using System.Buffers;
using System.Security.Cryptography;

namespace Massdex.SyncShark;

public static class DropboxContentHash
{
    public const int BlockSize = V2.ContentBlockHasher.DropboxBlockSize;

    public static string Compute(ReadOnlySpan<byte> input)
    {
        var hasher = new V2.ContentBlockHasher();
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
        var hasher = new V2.ContentBlockHasher();
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

}
