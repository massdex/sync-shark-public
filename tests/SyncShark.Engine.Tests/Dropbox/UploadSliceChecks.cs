using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks for UploadSlice (`--engine-checks dropbox`), written by UploadSlice's writer.</summary>
public static class UploadSliceChecks
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        byte[] content = [0, 1, 1, 0, 1];
        (int Offset, int Length)[] windows =
        [
            (0, 0), (0, 1), (1, 1), (1, content.Length - 1),
            (content.Length, 0), (0, content.Length)
        ];
        using var inner = new MemoryStream(content);
        foreach (var window in windows)
        {
            using var slice = new UploadSlice(inner, window.Offset, window.Length);
            Check.Require(slice.CanRead && slice.CanSeek && !slice.CanWrite, nameof(Stream.CanRead));
            Check.Require(slice.Length == window.Length && slice.Position == 0, nameof(Stream.Length));
            byte[] expected = content.AsSpan(window.Offset, window.Length).ToArray();
            byte[] buffer = new byte[content.Length + 1];
            int total = 0;
            while (total < window.Length)
            {
                Check.Require(slice.Read(buffer, total, 1) == 1, nameof(Stream.Read));
                total++;
                Check.Require(slice.Position == total, nameof(Stream.Position));
            }
            Check.Require(buffer.AsSpan(0, total).SequenceEqual(expected), nameof(Stream.Read));
            Check.Require(slice.Read(buffer, 0, buffer.Length) == 0, nameof(Stream.Read));

            Check.Require(slice.Seek(0, SeekOrigin.Begin) == 0, nameof(SeekOrigin.Begin));
            int read = slice.Read(buffer, 1, content.Length);
            Check.Require(read == window.Length && buffer.AsSpan(1, read).SequenceEqual(expected), nameof(Stream.Read));
            slice.Position = 0;
            read = await slice.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            Check.Require(read == window.Length && buffer.AsSpan(0, read).SequenceEqual(expected), nameof(Stream.ReadAsync));
            Check.Require(await slice.ReadAsync(buffer.AsMemory(), CancellationToken.None) == 0, nameof(Stream.ReadAsync));

            Check.Require(slice.Seek(-window.Length, SeekOrigin.Current) == 0, nameof(SeekOrigin.Current));
            read = await slice.ReadAsync(buffer, 1, content.Length, CancellationToken.None);
            Check.Require(read == window.Length && buffer.AsSpan(1, read).SequenceEqual(expected), nameof(Stream.ReadAsync));
            Check.Require(await slice.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None) == 0, nameof(Stream.ReadAsync));
            Check.Require(slice.Seek(-window.Length, SeekOrigin.End) == 0, nameof(SeekOrigin.End));
            Check.Require(slice.Seek(window.Length, SeekOrigin.Current) == window.Length, nameof(SeekOrigin.Current));
            Check.Require(slice.Seek(0, SeekOrigin.End) == window.Length, nameof(SeekOrigin.End));

            foreach (var origin in new[] { SeekOrigin.Begin, SeekOrigin.Current, SeekOrigin.End })
            {
                slice.Position = slice.Length;
                var offset = origin == SeekOrigin.Begin ? slice.Length + 1 : 1;
                Check.Require(slice.Seek(offset, origin) == slice.Length + 1, nameof(Stream.Seek));
                var innerPosition = inner.Position;
                Check.Require(slice.Read(buffer, 0, buffer.Length) == 0 && slice.Read(buffer.AsSpan()) == 0 &&
                    slice.ReadByte() == -1, "All synchronous reads past the window return EOF.");
                Check.Require(await slice.ReadAsync(buffer.AsMemory(), CancellationToken.None) == 0 &&
                    await slice.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None) == 0,
                    "All asynchronous reads past the window return EOF.");
                Check.Require(slice.Position == slice.Length + 1 && inner.Position == innerPosition,
                    "Reads past the window do not move either stream.");
            }
            slice.Position = long.MaxValue;
            Check.Require(slice.Read(buffer, 0, buffer.Length) == 0 &&
                await slice.ReadAsync(buffer.AsMemory(), CancellationToken.None) == 0,
                "A distant position past the window still returns EOF.");
            RequireThrows<OverflowException>(() => slice.Seek(1, SeekOrigin.Current));

            slice.Position = 0;
            Check.Require(slice.Read(buffer, 0, 0) == 0 && slice.Position == 0, nameof(Stream.Read));
            Check.Require(await slice.ReadAsync(Memory<byte>.Empty, CancellationToken.None) == 0, nameof(Stream.ReadAsync));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await RequireCanceledAsync(slice.ReadAsync(buffer.AsMemory(), cancellation.Token).AsTask());
            await RequireCanceledAsync(slice.ReadAsync(buffer, 0, buffer.Length, cancellation.Token));
            Check.Require(slice.Position == 0, nameof(CancellationToken));

            RequireThrows<NotSupportedException>(() => slice.Write(buffer, 0, buffer.Length));
            RequireThrows<NotSupportedException>(() => slice.SetLength(window.Length));
            RequireThrows<ArgumentNullException>(() => slice.Read(null!, 0, 0));
            RequireThrows<ArgumentOutOfRangeException>(() => slice.Read(buffer, -1, 1));
            RequireThrows<ArgumentOutOfRangeException>(() => slice.Read(buffer, 0, -1));
            RequireThrows<ArgumentException>(() => slice.Read(buffer, buffer.Length, 1));
            RequireThrows<ArgumentException>(() => slice.ReadAsync(buffer, buffer.Length, 1, CancellationToken.None));
            RequireThrows<IOException>(() => slice.Seek(-1, SeekOrigin.Begin));
            RequireThrows<IOException>(() => slice.Seek(long.MinValue, SeekOrigin.Current));
            RequireThrows<ArgumentOutOfRangeException>(() => slice.Seek(0, (SeekOrigin)(-1)));
            slice.Flush();
            slice.Dispose();
            Check.Require(inner.CanRead && inner.CanSeek, nameof(Stream.Dispose));
        }

        using (var slice = new UploadSlice(inner, 1, 1))
        {
            content[1] = 0;
            Check.Require(slice.ReadByte() == content[1], nameof(Stream.ReadByte));
            Check.Require(slice.ReadByte() == -1, nameof(Stream.ReadByte));
            slice.Position = 0;
            content[1] = 1;
            inner.Position = inner.Length;
            Check.Require(slice.ReadByte() == content[1], nameof(Stream.Position));
            await slice.DisposeAsync();
            Check.Require(inner.CanRead, nameof(Stream.DisposeAsync));
        }

        RequireThrows<ArgumentNullException>(() => new UploadSlice(null!, 0, 0));
        RequireThrows<ArgumentOutOfRangeException>(() => new UploadSlice(inner, -1, 0));
        RequireThrows<ArgumentOutOfRangeException>(() => new UploadSlice(inner, 0, -1));
        RequireThrows<ArgumentOutOfRangeException>(() => new UploadSlice(inner, inner.Length + 1, 0));
        RequireThrows<ArgumentOutOfRangeException>(() => new UploadSlice(inner, 1, inner.Length));
        RequireThrows<ArgumentOutOfRangeException>(() => new UploadSlice(inner, 1, long.MaxValue));
        using var nonSeekable = new System.IO.Compression.DeflateStream(
            inner, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
        RequireThrows<ArgumentException>(() => new UploadSlice(nonSeekable, 0, 0));
        using var disposed = new MemoryStream();
        disposed.Dispose();
        RequireThrows<ArgumentException>(() => new UploadSlice(disposed, 0, 0));

        using var shared = new MemoryStream(content);
        using var first = new UploadSlice(shared, 0, 3);
        using var second = new UploadSlice(shared, 2, 2);
        byte[] interleaved = new byte[3];
        byte[] secondBytes = new byte[2];
        Check.Require(first.Read(interleaved, 0, 2) == 2 && second.Read(secondBytes, 0, 2) == 2 &&
            first.Read(interleaved, 2, 1) == 1, "Interleaved slices read their own windows.");
        Check.Require(interleaved.AsSpan().SequenceEqual(content.AsSpan(0, 3)) &&
            secondBytes.AsSpan().SequenceEqual(content.AsSpan(2, 2)), nameof(Stream.Read));
        first.Position = 0;
        Check.Require(await first.ReadAsync(interleaved.AsMemory(), CancellationToken.None) == 3 &&
            interleaved.AsSpan().SequenceEqual(content.AsSpan(0, 3)), nameof(Stream.ReadAsync));

        using var shortSource = new ShortStream(content, 2);
        using var shortSlice = new UploadSlice(shortSource, 1, 3);
        byte[] partial = new byte[3];
        int filled = default;
        while (filled < partial.Length)
        {
            int read = shortSlice.Read(partial, filled, partial.Length - filled);
            if (read == 0) break;
            Check.Require(read <= 2, "A short source read is not padded to the requested count.");
            filled += read;
        }
        Check.Require(filled == partial.Length && partial.AsSpan().SequenceEqual(content.AsSpan(1, 3)) &&
            shortSlice.Position == partial.Length, "Short reads concatenate the exact window once.");
        Check.Require(shortSlice.Read(partial, 0, partial.Length) == 0, nameof(Stream.Read));
        shortSlice.Position = 0;
        filled = default;
        while (filled < partial.Length)
        {
            int read = await shortSlice.ReadAsync(partial.AsMemory(filled, partial.Length - filled),
                CancellationToken.None);
            if (read == 0) break;
            Check.Require(read <= 2, "A short asynchronous source read is not padded to the requested count.");
            filled += read;
        }
        Check.Require(filled == partial.Length && partial.AsSpan().SequenceEqual(content.AsSpan(1, 3)),
            nameof(Stream.ReadAsync));

        using var counted = new CountingStream(content);
        using var whole = new UploadSlice(counted, 0, content.Length);
        byte[] exact = new byte[content.Length];
        Check.Require(whole.Read(exact, 0, exact.Length) == exact.Length && counted.Requests.Count == 1 &&
            counted.Requests[0] == exact.Length, "One window read is one source read.");

        IOException failure = new("source-read-fixture");
        using var failing = new FailingReadStream(content, failure);
        using var failingSlice = new UploadSlice(failing, 0, 2);
        RequireThrows<IOException>(() => failingSlice.Read(partial, 0, partial.Length));
        Check.Require(failingSlice.Position == 0, "A failed source read does not advance the window.");
        bool failedRead = false;
        try { await failingSlice.ReadAsync(partial.AsMemory(), CancellationToken.None); }
        catch (IOException) { failedRead = true; }
        Check.Require(failedRead && failingSlice.Position == 0,
            "A failed asynchronous source read does not advance the window.");

        byte[] body = new byte[(SyncContentIdentity.DefaultReadBufferBytes * 2) + 1];
        for (int index = 0; index < body.Length; index++) body[index] = (byte)index;
        using var uploadSource = new CountingStream(body);
        var hasher = new ContentBlockHasher();
        using var upload = new UploadContent(uploadSource, hasher);
        using var uploaded = new MemoryStream();
        await upload.CopyToAsync(uploaded);
        Check.Require(uploaded.ToArray().AsSpan().SequenceEqual(body), "The upload body is the exact source window.");
        Check.Require(uploadSource.Requests.Count == 3 &&
            uploadSource.Requests[0] == SyncContentIdentity.DefaultReadBufferBytes &&
            uploadSource.Requests[1] == SyncContentIdentity.DefaultReadBufferBytes &&
            uploadSource.Requests[2] == 1, "The upload reads through one DefaultReadBufferBytes bucket.");
        Check.Require(hasher.TotalBytes == body.Length, nameof(ContentBlockHasher.TotalBytes));
        var expectedHasher = new ContentBlockHasher();
        expectedHasher.Append(body);
        Check.Require(hasher.Finish() == expectedHasher.Finish(),
            "The upload hashes exactly the bytes it sends.");
        int requestsBeforeReplay = uploadSource.Requests.Count;
        await RequireThrowsAsync<InvalidOperationException>(() => upload.CopyToAsync(uploaded));
        Check.Require(uploadSource.Requests.Count == requestsBeforeReplay,
            "A refused hashed replay reads no more source bytes.");
        upload.Dispose();
        Check.Require(uploadSource.CanRead, "The upload leaves its caller's source open.");

        using var positioned = new MemoryStream(content);
        positioned.Position = 2;
        using var positionedContent = new UploadContent(positioned);
        using var positionedDestination = new MemoryStream();
        await positionedContent.CopyToAsync(positionedDestination);
        Check.Require(positionedDestination.ToArray().AsSpan().SequenceEqual(content.AsSpan(2)),
            "The upload window starts at the source's current position.");

        var emptyHasher = new ContentBlockHasher();
        using var empty = new UploadContent(new MemoryStream(Array.Empty<byte>()), emptyHasher);
        using var emptyDestination = new MemoryStream();
        await empty.CopyToAsync(emptyDestination);
        Check.Require(emptyDestination.Length == 0 && emptyHasher.TotalBytes == 0 &&
            emptyHasher.Finish() == SyncContentIdentity.EmptyDigest(SyncContentIdentity.DropboxBlockSha256),
            "An empty body hashes the explicit empty digest.");

        using var truncated = new TruncatedStream(body);
        using var truncatedContent = new UploadContent(truncated);
        using var truncatedDestination = new MemoryStream();
        Exception? refused = null;
        try { await truncatedContent.CopyToAsync(truncatedDestination); }
        catch (Exception exception) { refused = exception; }
        Check.Require(refused is HttpRequestException { InnerException: IOException },
            "The short-source read failure is wrapped by HttpContent.CopyToAsync.");
        Check.Require(truncatedDestination.Length == body.Length,
            "A source shorter than its reported length is refused after its actual bytes.");
    }

    private static void RequireThrows<T>(Action action) where T : Exception
    {
        bool caught = false;
        try { action(); }
        catch (T) { caught = true; }
        Check.Require(caught, typeof(T).Name);
    }

    private static async Task RequireCanceledAsync(Task<int> read)
    {
        bool canceled = false;
        try { await read; }
        catch (OperationCanceledException) { canceled = true; }
        Check.Require(canceled && read.IsCanceled, nameof(OperationCanceledException));
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        bool caught = false;
        try { await action(); }
        catch (T) { caught = true; }
        Check.Require(caught, typeof(T).Name);
    }

    private sealed class ShortStream(byte[] bytes, int maxPerRead) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, maxPerRead));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer.Slice(0, Math.Min(buffer.Length, maxPerRead)), cancellationToken);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _spanRead;
        public List<int> Requests { get; } = new();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_spanRead) Requests.Add(count);
            return base.Read(buffer, offset, count);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // MemoryStream's span read funnels through the byte[] override; one logical source read counts once.
            Requests.Add(buffer.Length);
            _spanRead = true;
            try { return ValueTask.FromResult(base.Read(buffer.Span)); }
            finally { _spanRead = false; }
        }
    }

    private sealed class FailingReadStream(byte[] bytes, Exception error) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => throw error;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(error);
    }

    private sealed class TruncatedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override long Length => base.Length + 1;
    }
}
