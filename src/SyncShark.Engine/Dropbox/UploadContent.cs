using System.Buffers;
using System.Net;
using System.Runtime.ExceptionServices;
using Massdex.SyncShark;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>
/// The one upload serializer (section 5.5): streams a seekable window through one pooled buffer and appends the
/// same reads to the optional block hasher, so content is read once (BATCHING 4.4).
/// </summary>
public sealed partial class UploadContent : HttpContent
{
    private readonly UploadSlice _source;
    private readonly ContentBlockHasher? _hash;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Phase _phase;
    private CancellationTokenSource? _stop;
    private long _bytes;
    private Exception? _failure;

    /// <summary>Wraps the bytes from the content's current position to its end; the caller keeps the source.</summary>
    public UploadContent(Stream content, ContentBlockHasher? hash = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _source = new UploadSlice(content, content.Position, content.Length - content.Position);
        _hash = hash;
    }

    /// <summary>Joins the single serializer state and returns the bytes written to the destination.</summary>
    internal Task<long> CompleteAsync(CancellationToken ct) => CompleteCoreAsync(ct);

    /// <summary>Requests cancellation and joins the serializer; repeating the request is harmless.</summary>
    internal ValueTask StopAsync() => new(StopCoreAsync());

    protected override bool TryComputeLength(out long length)
    {
        length = _source.Length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
        CancellationToken cancellationToken)
    {
        // One gate: a late, post-stop or second serialization is rejected before the source, buffer or hasher is touched.
        lock (_gate)
        {
            if (_phase != Phase.New) throw new InvalidOperationException();
            _stop = new CancellationTokenSource();
            _phase = Phase.Running;
        }
        await SerializeCoreAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> CompleteCoreAsync(CancellationToken ct)
    {
        Task? done = null;
        lock (_gate)
        {
            if (_phase == Phase.Terminal)
            {
                if (_failure is not null) ExceptionDispatchInfo.Throw(_failure);
                return _bytes;
            }
            if (_phase == Phase.Running)
            {
                done = _done.Task;
            }
            else if (_source.Length != default)
            {
                // A decoded reply without a serialized nonempty body is an incomplete transmission, never a wait.
                var incomplete = new IOException();
                Seal(default, incomplete);
                throw incomplete;
            }
            else
            {
                Seal(default, null);
                return default;
            }
        }
        await done!.WaitAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_failure is not null) ExceptionDispatchInfo.Throw(_failure);
            return _bytes;
        }
    }

    private async Task StopCoreAsync()
    {
        Task? done = null;
        Exception? stopFailure = null;
        lock (_gate)
        {
            if (_phase == Phase.Running)
            {
                // A throwing stop callback must not skip the join; keep the error and surface it afterwards.
                try { _stop!.Cancel(); }
                catch (Exception error) { stopFailure = error; }
                done = _done.Task;
            }
            else if (_phase == Phase.New)
            {
                // Unsent nonempty content is an incomplete transmission, never a successful zero-byte upload.
                Seal(default, _source.Length == default ? null : new IOException());
            }
        }
        if (done is not null) await done.ConfigureAwait(false);
        if (stopFailure is not null) ExceptionDispatchInfo.Throw(stopFailure);
    }

    // One serializer pass: streaming, hashing and writing share one pooled buffer and byte count, then seal one terminal outcome.
    private async Task SerializeCoreAsync(Stream stream, CancellationToken cancellationToken)
    {
        long bytes = 0L;
        Exception? failure = null;
        byte[]? buffer = null;
        CancellationTokenSource? linked = null;
        try
        {
            // Empty admitted content is a no-op: no read, seek, rent or hash append; the caller finishes the digest.
            if (_source.Length != default)
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop!.Token);
                var token = linked.Token;
                _source.Position = 0L;
                buffer = ArrayPool<byte>.Shared.Rent(SyncContentIdentity.DefaultReadBufferBytes);
                while (true)
                {
                    // One DefaultReadBufferBytes bucket even when the pool rents a larger array.
                    var count = await _source.ReadAsync(
                        buffer.AsMemory(0, SyncContentIdentity.DefaultReadBufferBytes), token).ConfigureAwait(false);
                    if (count == 0) break;
                    _hash?.Append(buffer.AsSpan(..count));
                    await stream.WriteAsync(buffer.AsMemory(..count), token).ConfigureAwait(false);
                    bytes += count;
                }
                if (bytes != _source.Length) throw new IOException();
            }
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            Exception? cleanup = null;
            try
            {
                if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
                linked?.Dispose();
            }
            catch (Exception error) { cleanup = error; }
            var primary = failure;
            if (cleanup is not null)
                failure = primary is null ? cleanup : new AggregateException(primary, cleanup);
            Seal(bytes, failure);
            if (cleanup is not null) ExceptionDispatchInfo.Throw(failure!);
        }
    }

    private void Seal(long bytes, Exception? failure)
    {
        CancellationTokenSource? stop;
        lock (_gate)
        {
            _bytes = bytes;
            _failure = failure;
            _phase = Phase.Terminal;
            _done.SetResult();
            stop = _stop;
            _stop = null;
        }
        stop?.Dispose();
    }

    private enum Phase { New, Running, Terminal }
}
