using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Stages source bytes once, closing a session before any selected folder is removed.</summary>
public sealed class UploadContent
{
    /// <summary>Only a seekable small file with no selected folder may retain the source until publication.
    /// A large file (even alone) or a nonseekable recovery stream is sent in bounded session requests.</summary>
    public Task<DropboxStaged> StageAsync(DropboxApi api, string path, Stream content, Entry source, Entry? selectedTarget, CancellationToken ct)
        => StageAsync(api.SendAsync, Routes.Endpoint, Routes.ContentRequestBytes, path, content, source, selectedTarget, ct, api.Clock, api);

    /// <summary>Stages against the supplied transport and provider limits without owning either.</summary>
    public Task<DropboxStaged> StageAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Func<string, Uri> endpoint, long limit, string path, Stream content, Entry source, Entry? selectedTarget, CancellationToken ct, TimeProvider? clock = null, DropboxApi? api = null)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (!content.CanRead || source.Size < 0 || source.IsFolder ||
            (content.CanSeek && (content.Position != 0 || content.Length != source.Size)))
            throw new ChangedUnderneathException(path);
        return content.CanSeek && selectedTarget?.IsFolder != true && source.Size <= limit
            ? Task.FromResult(DropboxStaged.Single(path, content, source))
            : SessionAsync(send, endpoint, limit, path, content, source, ct, clock ?? TimeProvider.System, api);
    }

    internal static async Task<DropboxStaged> SessionAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Func<string, Uri> endpoint, long limit, string path, Stream content, Entry source, CancellationToken ct, TimeProvider clock, DropboxApi? api)
    {
        using var hash = new ContentHash();
        string? session = null;
        long offset = 0;
        do
        {
            (session, offset) = await AppendSliceAsync(send, endpoint, limit, path, content, source, ct, clock, api, hash, session, offset).ConfigureAwait(false);
        } while (offset < source.Size);
        var digest = hash.Finish();
        RequireSelectedDigest(path, source, digest);
        if (content.CanSeek && content.Length != source.Size) throw new ChangedUnderneathException(path);
        return DropboxStaged.Session(path, session!, offset, source, digest);
    }

    private static async Task<(string Session, long Offset)> AppendSliceAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Func<string, Uri> endpoint, long limit, string path, Stream content, Entry source, CancellationToken ct, TimeProvider clock,
        DropboxApi? api, ContentHash hash, string? current, long start)
    {
        var length = Math.Min(source.Size - start, limit);
        var close = length == source.Size - start;
        var route = current is null ? DropboxProtocol.UploadSessionStart : DropboxProtocol.UploadSessionAppend;
        object argument = current is null ? new { close } : new { cursor = new { session_id = current, offset = start }, close };
        using var body = new Body(content, start, length, hash, close);
        using var request = Request(endpoint(route), argument, body);
        try
        {
            using var response = await send(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var received = api is null ? clock.GetTimestamp() : DropboxHttp.ReceivedAt(response, clock);
                var header = DropboxHttp.RetryAfter(response, clock);
                var refusal = await DropboxHttp.ReadErrorAsync(response, ct).ConfigureAwait(false);
                var wait = DropboxProtocol.ProviderWait(header, refusal);
                var status = response.StatusCode;
                // Staging cannot publish the target even if the server received this slice.
                var classified = new DropboxErrors().Classify(status, refusal, route, true,
                    new(false, true, true, status == HttpStatusCode.TooManyRequests || (int)status >= 500));
                api?.ReportBusy(response.StatusCode, classified, wait, received);
                throw Dispatcher.ItemFailure(path, classified, wait, received);
            }
            await body.CompleteAsync(ct).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            if (current is null)
            {
                current = await DropboxReply.ReadAsync(stream, DropboxReplyShapes.SessionId(), ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(current)) throw new InvalidDataException();
            }
            else if (response.Content.Headers.ContentLength != 0)
                await DropboxReply.ReadAsync(stream, DropboxReplyShapes.Void(), ct).ConfigureAwait(false);
        }
        catch (Exception error) when ((error is ArgumentOutOfRangeException { ParamName: "offset" or "length" } or
            EndOfStreamException or HttpRequestException { InnerException: EndOfStreamException } or
            HttpRequestException { InnerException: ArgumentOutOfRangeException { ParamName: "offset" or "length" } }) && content.CanSeek && content.Length < source.Size)
        {
            ct.ThrowIfCancellationRequested();
            throw new ChangedUnderneathException(path, error);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or HttpRequestException or OperationCanceledException
            || ReferenceEquals(error, body.Resend))
        {
            ct.ThrowIfCancellationRequested();
            // A session slice cannot publish a file. Only a known transient transport failure, or this body's own re-send refusal (the session offset
            // is then unproved; the item restages from a fresh session), warrants timed retry.
            bool resent = body.Resend != null && ReferenceEquals(error.GetBaseException(), body.Resend);
            bool transient = resent || error is OperationCanceledException or HttpRequestException { HttpRequestError:
                HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded };
            throw new ItemFailedException(path, transient ? TextKey.RequestIncomplete : TextKey.DropboxRefused, error)
                { Disposition = transient ? FailureDisposition.TemporaryNoEffect : FailureDisposition.Permanent };
        }
        finally { await body.StopAsync().ConfigureAwait(false); }
        return (current!, start + length);
    }

    internal static void RequireSelectedDigest(string path, Entry source, string digest)
    {
        if (!string.IsNullOrEmpty(source.ContentHash) && !StringComparer.OrdinalIgnoreCase.Equals(source.ContentHash,digest))
            throw new ChangedUnderneathException(path);
    }

    internal static HttpRequestMessage Request(Uri endpoint, object argument, HttpContent body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = body };
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(argument));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    /// <summary>A single-use exact window; only bytes actually written contribute to the digest.</summary>
    internal sealed class Body(Stream source, long offset, long length, ContentHash hash, bool last) : HttpContent
    {
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The sender is either unclaimed (0), serializing (1), or claimed by completion/stop before serialization.
        private const int ClosedBeforeStart = 2;
        private int started;
        internal Exception? Resend; // the one refusal every transport re-send of this single-use body receives; set once, never replaced
        private readonly CancellationTokenSource stop = new();
        protected override bool TryComputeLength(out long result) { result = length; return true; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) =>
            SerializeToStreamAsync(destination, context, default);
        protected override async Task SerializeToStreamAsync(Stream destination, TransportContext? context, CancellationToken ct)
        {
            var prior = Interlocked.CompareExchange(ref started, 1, 0);
            if (prior == 1) throw Interlocked.CompareExchange(ref Resend, new InvalidOperationException(), null) ?? Resend!; // every re-send, any order
            if (prior != 0) throw new InvalidOperationException(); // 2 = ClosedBeforeStart: not a re-send
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
                using var slice = source.CanSeek ? new UploadSlice(source, offset, length) : null;
                var input = slice ?? source;
                using var owner = MemoryPool<byte>.Shared.Rent(EngineSettings.StreamCopyBufferBytes);
                var buffer = owner.Memory[..EngineSettings.StreamCopyBufferBytes];
                for (long left = length; left > 0;)
                {
                    var count = await input.ReadAsync(buffer[..(int)Math.Min(left, buffer.Length)], linked.Token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException();
                    await destination.WriteAsync(buffer[..count], linked.Token).ConfigureAwait(false);
                    hash.Append(buffer.Span[..count]);
                    left -= count;
                }
                if (last && !source.CanSeek && await input.ReadAsync(buffer[..sizeof(byte)], linked.Token).ConfigureAwait(false) != 0)
                    throw new InvalidDataException();
                done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); throw; }
        }
        /// <summary>True once every selected byte was written and hashed, whether or not a reply ever arrives.</summary>
        internal bool Sent => done.Task.IsCompletedSuccessfully;
        internal Task CompleteAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref started, ClosedBeforeStart, 0) == 0)
            {
                if (length != 0 || !source.CanSeek) throw new EndOfStreamException();
                return Task.CompletedTask;
            }
            return done.Task.WaitAsync(ct);
        }
        internal async Task StopAsync()
        {
            if (Interlocked.CompareExchange(ref started, ClosedBeforeStart, 0) != 1) return;
            try { if (!done.Task.IsCompleted) await stop.CancelAsync().ConfigureAwait(false); }
            finally
            {
                try { await done.Task.ConfigureAwait(false); }
                catch when (done.Task.IsFaulted || done.Task.IsCanceled) { /* Join without replacing the sender's classified failure. */ }
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) stop.Dispose(); base.Dispose(disposing); }
    }
}
