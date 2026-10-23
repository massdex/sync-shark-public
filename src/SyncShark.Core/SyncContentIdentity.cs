using System.Buffers;
using System.Security.Cryptography;

namespace Massdex.SyncShark;

// Provider-aware content identity.
//
// Dropbox and Google Drive do not share a content-identity algorithm: Dropbox publishes a 4 MiB block
// hash and Drive publishes whole-file SHA-256/MD5. The two are never compared, relabelled or converted.
// The bound provider of a pair selects the algorithm for that pair's local identity, and a retained
// identity always carries the algorithm it was produced with so a stale or foreign identity refuses
// instead of being reinterpreted.
public static class SyncContentIdentity
{
    public const string DropboxBlockSha256 = "dropbox-block-sha256-v1";
    public const string Sha256 = "sha256";
    public const string Md5 = "md5";
    public const string Unknown = "unknown";

    public static string AlgorithmFor(string? provider)
    {
        var id = SyncProviders.Normalize(provider);
        if (id == SyncProviders.Local) return SyncEndpointProviders.Get(SyncEndpointKind.Local).ContentHashAlgorithm;
        return SyncEndpointProviders.TryGet(id, out var definition) ? definition!.ContentHashAlgorithm :
            throw new NotSupportedException("The provider has no registered content identity algorithm.");
    }

    // Identity stamps change persisted identity bytes, so Dropbox identities stay exactly as they were
    // and only a non-Dropbox provider records the algorithm it was produced with.
    public static string? IdentityStampFor(string? provider) =>
        AlgorithmFor(provider) is DropboxBlockSha256 ? null : AlgorithmFor(provider);

    public static bool IsKnown(string? algorithm) => algorithm is DropboxBlockSha256 or Sha256 or Md5;

    // A missing or unrecognised algorithm is unknown, and unknown is never equality.
    public static bool IdentitiesMatch(string? leftAlgorithm, string? leftHash,
        string? rightAlgorithm, string? rightHash)
    {
        if (string.IsNullOrWhiteSpace(leftHash) || string.IsNullOrWhiteSpace(rightHash)) return false;
        if (!IsKnown(leftAlgorithm) || !IsKnown(rightAlgorithm)) return false;
        if (!string.Equals(leftAlgorithm, rightAlgorithm, StringComparison.Ordinal)) return false;
        return string.Equals(leftHash, rightHash, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IdentityMatchesAlgorithm(string? algorithm, string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || !IsKnown(algorithm)) return false;
        var expectedLength = string.Equals(algorithm, Md5, StringComparison.Ordinal) ? 32 : 64;
        return hash.Length == expectedLength && hash.All(character => Uri.IsHexDigit(character));
    }

    /// <summary>The read chunk a byte-stream digest uses when its caller names none; it never changes a digest.</summary>
    public const int DefaultReadBufferBytes = 128 * 1024;

    public static Task<string> ComputeAsync(string? provider, Stream source,
        CancellationToken cancellationToken = default) =>
        ComputeAsync(provider, source, DefaultReadBufferBytes, cancellationToken);

    /// <summary>The provider's digest of the stream, read in chunks of <paramref name="bufferBytes"/>.</summary>
    public static async Task<string> ComputeAsync(string? provider, Stream source, int bufferBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var operation = V2.RunOperations.Begin(V2.RunOperationKind.LocalIo,
            source is FileStream file ? V2.RunOperations.Short(file.Name) : "hashing file");
        using var tracked = V2.RunOperationStream.Touching(source, operation.Touch);
        await using var progress = new SyncByteProgressStream(tracked, "Hashing file contents");
        await progress.StartedAsync();
        return AlgorithmFor(provider) switch
        {
            Sha256 => await Sha256Async(progress, bufferBytes, cancellationToken),
            DropboxBlockSha256 => await DropboxContentHash.ComputeAsync(progress, bufferBytes, cancellationToken),
            _ => throw new NotSupportedException("The provider's content algorithm has no byte-stream implementation.")
        };
    }

    public static string Compute(string? provider, ReadOnlySpan<byte> bytes) => AlgorithmFor(provider) switch
    {
        Sha256 => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        DropboxBlockSha256 => DropboxContentHash.Compute(bytes),
        _ => throw new NotSupportedException("The provider's content algorithm has no byte-span implementation.")
    };

    public static Task<string> Sha256Async(Stream source, CancellationToken cancellationToken = default) =>
        Sha256Async(source, DefaultReadBufferBytes, cancellationToken);

    /// <summary>Whole-stream SHA-256 through a pooled read buffer of the given size.</summary>
    public static async Task<string> Sha256Async(Stream source, int bufferBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferBytes);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, bufferBytes), cancellationToken);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // Cross-provider work computes each side over the same bytes with its own algorithm. Comparing two
    // different algorithms is refused rather than attempted.
    public static void RequireSameAlgorithm(string? leftProvider, string? rightProvider, string operation)
    {
        var left = AlgorithmFor(leftProvider);
        var right = AlgorithmFor(rightProvider);
        if (!string.Equals(left, right, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "This " + operation + " spans two providers with different content-identity algorithms (" +
                left + " versus " + right + "). The operation was refused instead of comparing unrelated " +
                "identities.");
    }

    /// <summary>
    /// True only for a registered algorithm this build can compute incrementally over a byte stream. An
    /// algorithm that is not registered here is never routed to a different digest.
    /// </summary>
    public static bool SupportsIncrementalHashing(string? algorithm) =>
        algorithm is Sha256 or Md5 or DropboxBlockSha256;

    /// <summary>
    /// The one algorithm-capability seam for incremental content identity. Callers - including the
    /// revision-bound cloud digest resolver - ask this seam for their registered algorithm and never branch
    /// on a provider name; an unregistered algorithm is refused rather than substituted.
    /// </summary>
    public static IIncrementalContentHasher CreateIncrementalHasher(string? algorithm) =>
        algorithm switch
        {
            Sha256 => new HashAlgorithmIncrementalHasher(Sha256, HashAlgorithmName.SHA256),
            Md5 => new HashAlgorithmIncrementalHasher(Md5, HashAlgorithmName.MD5),
            DropboxBlockSha256 => new DropboxBlockIncrementalHasher(),
            _ => throw new NotSupportedException(
                "The content-identity algorithm has no incremental byte-stream implementation; it is refused rather than substituted.")
        };

    /// <summary>The exact digest of zero bytes for a registered algorithm, computed by the same seam.</summary>
    public static string EmptyDigest(string? algorithm) => CreateIncrementalHasher(algorithm).FinishDigest();

}


/// <summary>
/// One incremental content-identity computation over an ordered byte stream. The registered algorithm is
/// carried on the instance, so a caller never compares, relabels or substitutes two different algorithms.
/// </summary>
public interface IIncrementalContentHasher
{
    string Algorithm { get; }

    void Append(ReadOnlySpan<byte> bytes);

    string FinishDigest();
}

/// <summary>A whole-file digest produced by <see cref="System.Security.Cryptography.IncrementalHash"/>.</summary>
internal sealed class HashAlgorithmIncrementalHasher(string algorithm, HashAlgorithmName name)
    : IIncrementalContentHasher
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(name);
    private bool _finished;

    public string Algorithm { get; } = algorithm;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_finished) throw new ObjectDisposedException(nameof(HashAlgorithmIncrementalHasher));
        _hash.AppendData(bytes);
    }

    public string FinishDigest()
    {
        if (_finished) throw new ObjectDisposedException(nameof(HashAlgorithmIncrementalHasher));
        _finished = true;
        return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>
/// The Dropbox block-algorithm seam: the exact 4 MiB block policy lives in
/// <see cref="DropboxContentHash.BlockHasher"/> and is never reimplemented here.
/// </summary>
internal sealed class DropboxBlockIncrementalHasher : IIncrementalContentHasher
{
    private readonly DropboxContentHash.BlockHasher _hasher = new();
    private bool _finished;

    public string Algorithm => SyncContentIdentity.DropboxBlockSha256;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_finished) throw new ObjectDisposedException(nameof(DropboxBlockIncrementalHasher));
        _hasher.Append(bytes);
    }

    public string FinishDigest()
    {
        if (_finished) throw new ObjectDisposedException(nameof(DropboxBlockIncrementalHasher));
        _finished = true;
        return _hasher.Finish();
    }
}

/// <summary>One completed hashing-sink result over exactly the bytes the sink retained.</summary>
public sealed record SyncContentHashingDigest(string NativeAlgorithm, long ByteCount, string CommonSha256,
    string NativeDigest);

/// <summary>
/// One writable, deliberately non-seekable and non-readable hashing sink. In one pass it computes the common
/// whole-file SHA-256 and the requested registered native algorithm over exactly the bytes written to it, with
/// a checked 64-bit count, and it refuses any byte that would exceed its reviewed bound BEFORE that byte is
/// hashed or retained. It never buffers the object, seeks, reads, caches or writes a temporary file, so it can
/// never be passed where a real seekable download destination is required, and every read, seek, length change
/// or post-dispose use is refused.
/// </summary>
public sealed class SyncContentHashingSink : Stream
{
    private readonly IncrementalHash _common = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IIncrementalContentHasher _native;
    private readonly long _expectedLength;
    private long _count;
    private bool _disposed;

    public SyncContentHashingSink(string nativeAlgorithm, long expectedLength)
    {
        if (!SyncContentIdentity.SupportsIncrementalHashing(nativeAlgorithm))
            throw new NotSupportedException(
                "The hashing sink requires a registered content-identity algorithm; it is refused rather than substituted.");
        if (expectedLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        NativeAlgorithm = nativeAlgorithm;
        _native = SyncContentIdentity.CreateIncrementalHasher(nativeAlgorithm);
        _expectedLength = expectedLength;
    }

    /// <summary>The provider-native algorithm this sink computes beside the common SHA-256.</summary>
    public string NativeAlgorithm { get; }

    /// <summary>The reviewed object length this sink refuses to exceed.</summary>
    public long ExpectedLength => _expectedLength;

    /// <summary>The exact number of bytes retained and hashed so far, as a checked 64-bit count.</summary>
    public long ByteCount => _count;

    /// <summary>True only after exactly the reviewed number of bytes was retained.</summary>
    public bool IsComplete => _count == _expectedLength;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => throw new NotSupportedException("The hashing sink has no length.");

    public override long Position
    {
        get => throw new NotSupportedException("The hashing sink has no position.");
        set => throw new NotSupportedException("The hashing sink has no position.");
    }

    /// <summary>Completes both digests over exactly the retained bytes, or refuses an incomplete stream.</summary>
    public SyncContentHashingDigest Finish()
    {
        RequireActive();
        if (_count != _expectedLength)
            throw new InvalidDataException(
                "The hashed byte count differs from the reviewed object size; no digest is produced.");
        _disposed = true;
        var common = Convert.ToHexString(_common.GetHashAndReset()).ToLowerInvariant();
        return new SyncContentHashingDigest(NativeAlgorithm, _count, common, _native.FinishDigest());
    }

    public override void Flush() => RequireActive();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireActive();
        return Task.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(
        "The hashing sink is deliberately not seekable; a full download destination must be a real seekable file.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("The hashing sink has no seekable length.");

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The hashing sink is write-only.");

    public override int Read(Span<byte> buffer) =>
        throw new NotSupportedException("The hashing sink is write-only.");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The hashing sink is write-only.");

    public override void Write(byte[] buffer, int offset, int count)
    {
        RequireActive();
        Append(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        RequireActive();
        Append(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireActive();
        Append(new ReadOnlySpan<byte>(buffer, offset, count));
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireActive();
        Append(buffer.Span);
        return ValueTask.CompletedTask;
    }

    // Excess bytes are refused BEFORE they are hashed or retained, so a provider that sends more than the
    // reviewed length can never produce a digest of the wrong bytes.
    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > _expectedLength - _count)
            throw new IOException(
                "The hashing sink received more bytes than the reviewed object length; the excess bytes were refused and no digest is produced.");
        if (bytes.IsEmpty) return;
        _common.AppendData(bytes);
        _native.Append(bytes);
        _count += bytes.Length;
    }

    private void RequireActive()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncContentHashingSink));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _common.Dispose();
        }
        base.Dispose(disposing);
    }
}


