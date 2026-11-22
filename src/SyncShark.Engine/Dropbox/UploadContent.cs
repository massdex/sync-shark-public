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
        => StageAsync(api.SendAsync, Routes.Endpoint, Routes.ContentRequestBytes, path, content, source, selectedTarget, ct, api.Clock);

    /// <summary>Stages against the supplied transport and provider limits without owning either.</summary>
    public Task<DropboxStaged> StageAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Func<string, Uri> endpoint, long limit, string path, Stream content, Entry source, Entry? selectedTarget, CancellationToken ct, TimeProvider? clock = null)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (!content.CanRead || source.Size < 0 || source.IsFolder ||
            (content.CanSeek && (content.Position != 0 || content.Length != source.Size)))
            throw new ChangedUnderneathException(path);
        return content.CanSeek && selectedTarget?.IsFolder != true && source.Size <= limit
            ? Task.FromResult(DropboxStaged.Single(path, content, source))
            : SessionAsync(send, endpoint, limit, path, content, source, ct, clock ?? TimeProvider.System);
    }

    internal static async Task<DropboxStaged> SessionAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Func<string, Uri> endpoint, long limit, string path, Stream content, Entry source, CancellationToken ct, TimeProvider clock)
    {
        using var hash = new ContentHash();
        string? session = null;
        long offset = 0;
        do
        {
            var length = Math.Min(source.Size - offset, limit);
            var close = length == source.Size - offset;
            var route = session is null ? "files/upload_session/start" : "files/upload_session/append_v2";
            object argument = session is null ? new { close } : new { cursor = new { session_id = session, offset }, close };
            using var body = new Body(content, offset, length, hash, close);
            using var request = Request(endpoint(route), argument, body);
            try
            {
                using var response = await send(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var failure = await Dispatcher.WriteFailure.ReadAsync(response, clock, ct).ConfigureAwait(false);
                    throw failure.Error(path, failure.Classify(route, true));
                }
                await body.CompleteAsync(ct).ConfigureAwait(false);
                if (session is null)
                {
                    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                    session = json.RootElement.GetProperty("session_id").GetString();
                    if (string.IsNullOrEmpty(session)) throw new InvalidDataException();
                }
                else
                {
                    if (response.Content.Headers.ContentLength != 0)
                        await DropboxReply.ReadAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), DropboxReplyShapes.Void(), ct).ConfigureAwait(false);
                }
            }
            finally { await body.StopAsync().ConfigureAwait(false); }
            offset += length;
        } while (offset < source.Size);
        var digest = hash.Finish();
        if ((!string.IsNullOrEmpty(source.ContentHash) && !StringComparer.OrdinalIgnoreCase.Equals(source.ContentHash, digest)) ||
            (content.CanSeek && content.Length != source.Size)) throw new ChangedUnderneathException(path);
        return DropboxStaged.Session(path, session!, offset, source, digest);
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
        private int started;
        private readonly CancellationTokenSource stop = new();
        protected override bool TryComputeLength(out long result) { result = length; return true; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) =>
            SerializeToStreamAsync(destination, context, default);
        protected override async Task SerializeToStreamAsync(Stream destination, TransportContext? context, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0) throw new InvalidOperationException();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
                using var slice = source.CanSeek ? new UploadSlice(source, offset, length) : null;
                var input = slice ?? source;
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    for (long left = length; left > 0;)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), linked.Token).ConfigureAwait(false);
                        if (count == 0) throw new EndOfStreamException();
                        await destination.WriteAsync(buffer.AsMemory(0, count), linked.Token).ConfigureAwait(false);
                        hash.Append(buffer.AsSpan(0, count));
                        left -= count;
                    }
                    if (last && !source.CanSeek && await input.ReadAsync(buffer.AsMemory(0, 1), linked.Token).ConfigureAwait(false) != 0)
                        throw new InvalidDataException();
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); throw; }
        }
        internal Task CompleteAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref started, 2, 0) == 0)
            {
                if (length != 0 || !source.CanSeek) throw new EndOfStreamException();
                return Task.CompletedTask;
            }
            return done.Task.WaitAsync(ct);
        }
        internal async Task StopAsync()
        {
            if (Interlocked.CompareExchange(ref started, 2, 0) != 1) return;
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
