using System.Net;

namespace Massdex.SyncShark;

// Streams a whole source or one session segment; caller keeps its handle through retries.
// Admission happens at serialization, after provider pacing, and ends before checkpoint/batch waits.
internal sealed class UploadPayloadContent(Stream source, long? length = null) : HttpContent
{
    // Below the large-object heap threshold; exact-size allocation avoids retaining a shared pool high-water.
    internal const int ChunkBytes = 64 * 1024;
    private readonly long _start = source.CanSeek ? source.Position : 0;
    private int _serialized;
    private readonly long? _length = length ?? (source.CanSeek ? source.Length - source.Position : null);
    protected override bool TryComputeLength(out long result)
    {
        result = _length ?? 0;
        return _length.HasValue;
    }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
        CancellationToken cancellationToken)
    {
        // Authentication/redirect handlers may serialize the SAME request again, including a nonzero segment.
        if (source.CanSeek) source.Position = _start;
        else if (Interlocked.Exchange(ref _serialized, 1) != 0)
            throw new InvalidOperationException("A non-seekable upload source cannot be replayed.");
        var chunk = (int)Math.Clamp(_length ?? ChunkBytes, 1, ChunkBytes);
        using var lease = await UploadPayloadBudget.RentAsync(chunk, cancellationToken).ConfigureAwait(false);
        long sent = 0;
        while (!_length.HasValue || sent < _length.Value)
        {
            var take = (int)Math.Min(lease.Buffer.Length, _length.HasValue ? _length.Value - sent : int.MaxValue);
            var count = await source.ReadAsync(lease.Buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            await stream.WriteAsync(lease.Buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            sent += count;
        }
        if (_length is { } expected && sent != expected)
            throw new IOException("The upload source ended before its retained length.");
    }
}
