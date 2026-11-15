using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

// Over the 300-line review prompt, not split: one physical test file owns one job, the complete
// ResponseStream read, lifetime and attempt contract; its fakes are private to that contract.
/// <summary>Checks for ResponseStream (`--engine-checks dropbox`), written by ResponseStream's writer.</summary>
public static class ResponseStreamChecks
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        foreach (var (disposeAsync, sharedContentStream) in new[]
        {
            (false, false), (false, true), (true, false), (true, true)
        })
        {
            using var inner = new MemoryStream(new byte[] { 0, 1 });
            using var contentStream = sharedContentStream ? inner : new MemoryStream();
            using var response = new HttpResponseMessage { Content = new StreamContent(contentStream) };
            var attempt = new RequestAttempt(new object(), AttemptOwner.Transfer);
            using var stream = new ResponseStream(response, inner, attempt, CancellationToken.None);
            Check.Require(stream.CanRead && stream.CanSeek && !stream.CanWrite, nameof(ResponseStream.CanRead));
            Check.Require(stream.Length == inner.Length && stream.Position == inner.Position, nameof(ResponseStream.Length));

            byte[] buffer = [1, 1, 1];
            Check.Require(stream.Read(buffer, 0, 0) == 0 && stream.Read(Span<byte>.Empty) == 0 &&
                await stream.ReadAsync(Memory<byte>.Empty, CancellationToken.None) == 0 &&
                await stream.ReadAsync(Memory<byte>.Empty) == 0 &&
                await stream.ReadAsync(buffer, 0, 0, CancellationToken.None) == 0 && stream.Position == 0,
                nameof(ResponseStream.Read));

            Check.Require(stream.Read(buffer, 1, 1) == 1 && buffer[0] == 1 && buffer[1] == 0 && buffer[^1] == 1,
                nameof(ResponseStream.Read));
            Check.Require(stream.Position == inner.Position && stream.Position == 1, nameof(ResponseStream.Position));
            Check.Require(await stream.ReadAsync(buffer.AsMemory(1, 1), CancellationToken.None) == 1 && buffer[1] == 1,
                nameof(ResponseStream.ReadAsync));
            Check.Require(await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None) == 0,
                nameof(ResponseStream.ReadAsync));
            Check.Require(stream.ReadByte() == -1 && stream.Position == 2, nameof(ResponseStream.ReadByte));

            stream.Position = 0;
            Check.Require(inner.Position == 0, nameof(ResponseStream.Position));
            Check.Require(await stream.ReadAsync(buffer, 1, 1, CancellationToken.None) == 1 && buffer[1] == 0,
                nameof(ResponseStream.ReadAsync));
            Check.Require(stream.Read(new Span<byte>(buffer, 2, 1)) == 1 && buffer[2] == 1, nameof(ResponseStream.Read));
            Check.Require(stream.Read(new Span<byte>(buffer, 0, 1)) == 0 && stream.ReadByte() == -1,
                nameof(ResponseStream.Read));
            Check.Require(stream.Seek(0, SeekOrigin.End) == inner.Length, nameof(ResponseStream.Seek));
            Check.Require(stream.Read(buffer, 0, buffer.Length) == 0, nameof(ResponseStream.Read));
            stream.Flush();
            Check.Require(attempt.Completion is null, "reads, EOF, flush and seek must not publish a completion");

            foreach (Action write in new Action[]
            {
                () => stream.Write(buffer, 0, buffer.Length),
                () => stream.SetLength(0),
                () => stream.WriteByte(0)
            })
                RequireThrows<NotSupportedException>(write);
            Check.Require(inner.ToArray().SequenceEqual(new byte[] { 0, 1 }), nameof(ResponseStream.CanWrite));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Exception? canceled = null;
            try
            {
                await stream.ReadAsync(buffer, 0, buffer.Length, cancellation.Token);
            }
            catch (Exception error)
            {
                canceled = error;
            }
            Check.Require(canceled is OperationCanceledException canceledError &&
                canceledError.CancellationToken == cancellation.Token, nameof(OperationCanceledException));
            Check.Require(attempt.Completion?.Result == RequestResult.CallerStopped &&
                ReferenceEquals(attempt.Completion?.Error, canceled), nameof(RequestResult.CallerStopped));
            Exception? repeated = null;
            try
            {
                await stream.ReadAsync(buffer.AsMemory(), cancellation.Token);
            }
            catch (Exception error)
            {
                repeated = error;
            }
            Check.Require(repeated is OperationCanceledException &&
                ReferenceEquals(attempt.Completion?.Error, canceled),
                "a second failure must not replace the first completion");

            if (disposeAsync)
                await stream.DisposeAsync();
            else
                stream.Dispose();

            Check.Require(!inner.CanRead && !contentStream.CanRead, nameof(ResponseStream.Dispose));
            Check.Require(!stream.CanRead && !stream.CanSeek && !stream.CanWrite, nameof(ResponseStream.Dispose));
            Check.Require(attempt.Completion?.Result == RequestResult.CallerStopped, "dispose must not publish success");
            RequireThrows<ObjectDisposedException>(() => response.Content.ReadAsStream());
            RequireThrows<ObjectDisposedException>(() => stream.Read(buffer, 0, buffer.Length));
            RequireThrows<ObjectDisposedException>(() => stream.Read(Span<byte>.Empty));
            RequireThrows<ObjectDisposedException>(() => _ = stream.Length);
            RequireThrows<ObjectDisposedException>(() => _ = stream.Position);
            RequireThrows<ObjectDisposedException>(() => stream.Position = 0);
            RequireThrows<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
            RequireThrows<ObjectDisposedException>(() => stream.Flush());
            await RequireThrowsAsync<ObjectDisposedException>(() =>
                stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());
            stream.Dispose();
            await stream.DisposeAsync();
        }

        CheckRangeArguments();
        CheckNonSeekable();
        CheckAttemptClassification();
        await CheckCallerCancellationAsync();
        CheckSyncContext();
        await CheckBodyCloseReleasesResponseAsync();
    }

    private static void CheckRangeArguments()
    {
        using var response = new HttpResponseMessage();
        using var inner = new MemoryStream(new byte[] { 0, 1 });
        using var stream = new ResponseStream(response, inner, null, CancellationToken.None);
        RequireThrows<ArgumentNullException>(() => stream.Read(null!, 0, 0));
        RequireThrows<ArgumentOutOfRangeException>(() => stream.Read(new byte[2], -1, 0));
        RequireThrows<ArgumentOutOfRangeException>(() => stream.Read(new byte[2], 0, -1));
        RequireThrows<ArgumentException>(() => stream.Read(new byte[2], 1, 2));
        RequireThrows<ArgumentNullException>(() => _ = stream.ReadAsync(null!, 0, 0, CancellationToken.None));
        RequireThrows<ArgumentOutOfRangeException>(() => _ = stream.ReadAsync(new byte[2], -1, 0, CancellationToken.None));
        RequireThrows<ArgumentOutOfRangeException>(() => _ = stream.ReadAsync(new byte[2], 0, -1, CancellationToken.None));
        RequireThrows<ArgumentException>(() => _ = stream.ReadAsync(new byte[2], 1, 2, CancellationToken.None));
    }

    private static void CheckNonSeekable()
    {
        using var backing = new MemoryStream();
        using var nonSeekable = new GZipStream(backing, CompressionMode.Decompress);
        using var nonSeekableResponse = new HttpResponseMessage();
        using var forwarded = new ResponseStream(nonSeekableResponse, nonSeekable, null, CancellationToken.None);
        Check.Require(forwarded.CanRead && !forwarded.CanSeek && !forwarded.CanWrite, nameof(ResponseStream.CanSeek));
        RequireThrows<NotSupportedException>(() => _ = forwarded.Length);
        RequireThrows<NotSupportedException>(() => _ = forwarded.Position);
        RequireThrows<NotSupportedException>(() => forwarded.Position = 0);
        RequireThrows<NotSupportedException>(() => forwarded.Seek(0, SeekOrigin.Begin));
        RequireThrows<ArgumentNullException>(() => new ResponseStream(null!, backing, null, CancellationToken.None));
        RequireThrows<ArgumentNullException>(() =>
            new ResponseStream(nonSeekableResponse, null!, null, CancellationToken.None));
    }

    private static void CheckAttemptClassification()
    {
        var transient = new IOException("stalled body");
        var (transientError, transientAttempt) = ReadFailure(transient);
        Check.Require(ReferenceEquals(transientError, transient) &&
            transientAttempt.Completion?.Result == RequestResult.TransientFailure, nameof(RequestResult.TransientFailure));
        var completion = transientAttempt.Completion!.Value;
        Check.Require(completion.ProviderWait is null && completion.NoEffectProven &&
            ReferenceEquals(completion.Error, transient), nameof(RequestAttempt.Completion));
        Check.Require(transientAttempt.TryClaimCompletion() && !transientAttempt.TryClaimCompletion(),
            nameof(RequestAttempt.TryClaimCompletion));

        Check.Require(ReadFailure(new InvalidDataException("truncated")).Attempt.Completion?.Result ==
            RequestResult.PermanentFailure, nameof(RequestResult.PermanentFailure));
        Check.Require(ReadFailure(new SocketException()).Attempt.Completion?.Result ==
            RequestResult.TransientFailure, nameof(SocketException));
        Check.Require(ReadFailure(new OperationCanceledException(null, new TimeoutException())).Attempt.Completion?.Result ==
            RequestResult.TransientFailure, nameof(TimeoutException));
        Check.Require(ReadFailure(new Exception("provider refused")).Attempt.Completion?.Result ==
            RequestResult.PermanentFailure, nameof(RequestResult.PermanentFailure));

        var argument = new ArgumentException("caller error");
        var (argumentError, argumentAttempt) = ReadFailure(argument);
        Check.Require(ReferenceEquals(argumentError, argument) && argumentAttempt.Completion is null,
            "argument errors are not attempt outcomes");
        var disposed = new ObjectDisposedException("body");
        var (disposedError, disposedAttempt) = ReadFailure(disposed);
        Check.Require(ReferenceEquals(disposedError, disposed) && disposedAttempt.Completion is null,
            "disposed errors are not attempt outcomes");
    }

    private static async Task CheckCallerCancellationAsync()
    {
        using (var caller = new CancellationTokenSource())
        {
            using var response = new HttpResponseMessage();
            var attempt = new RequestAttempt(new object(), AttemptOwner.Transfer);
            using var stream = new ResponseStream(response, new StallingStream(), attempt, caller.Token);
            var read = stream.ReadAsync(new byte[1].AsMemory(), CancellationToken.None).AsTask();
            caller.Cancel();
            var error = await AwaitFaultAsync(read);
            Check.Require(error is OperationCanceledException canceled && canceled.CancellationToken == caller.Token,
                nameof(OperationCanceledException));
            Check.Require(attempt.Completion?.Result == RequestResult.CallerStopped &&
                ReferenceEquals(attempt.Completion?.Error, error), nameof(RequestResult.CallerStopped));
        }

        using (var stall = new CancellationTokenSource())
        using (var onCall = new CancellationTokenSource())
        {
            using var response = new HttpResponseMessage();
            var attempt = new RequestAttempt(new object(), AttemptOwner.Transfer);
            using var stream = new ResponseStream(response, new StallingStream(stall.Token), attempt, CancellationToken.None);
            var read = stream.ReadAsync(new byte[1].AsMemory(), onCall.Token).AsTask();
            onCall.Cancel();
            stall.Cancel();
            var error = await AwaitFaultAsync(read);
            Check.Require(error is OperationCanceledException canceled && canceled.CancellationToken == onCall.Token &&
                canceled.InnerException is OperationCanceledException inner && inner.CancellationToken == stall.Token,
                "caller cancellation wins a race with stall cancellation");
            Check.Require(ReferenceEquals(attempt.Completion?.Error, error), nameof(RequestAttempt.Completion));
        }
    }

    private static void CheckSyncContext()
    {
        using var response = new HttpResponseMessage();
        using var inner = new PoolReadStream(new byte[] { 7 });
        using var stream = new ResponseStream(response, inner, null, CancellationToken.None);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BlockedSynchronizationContext());
            try
            {
                var buffer = new byte[1];
                if (stream.Read(buffer, 0, 1) != 1 || buffer[0] != 7)
                    failure = new InvalidOperationException(nameof(ResponseStream.Read));
            }
            catch (Exception error)
            {
                failure = error;
            }
        }) { IsBackground = true };
        thread.Start();
        Check.Require(thread.Join(TimeSpan.FromSeconds(15)),
            "a synchronous read must not wait on a captured synchronization context");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task CheckBodyCloseReleasesResponseAsync()
    {
        var response = new HttpResponseMessage { Content = new StreamContent(new MemoryStream(new byte[] { 0 })) };
        var stream = new ResponseStream(response, new ThrowingCloseStream(), null, CancellationToken.None);
        RequireThrows<IOException>(() => stream.Dispose());
        Check.Require(!stream.CanRead, nameof(ResponseStream.Dispose));
        RequireThrows<ObjectDisposedException>(() => response.Content.ReadAsStream());

        var asyncResponse = new HttpResponseMessage { Content = new StreamContent(new MemoryStream(new byte[] { 0 })) };
        var asyncStream = new ResponseStream(asyncResponse, new ThrowingCloseStream(), null, CancellationToken.None);
        await RequireThrowsAsync<IOException>(() => asyncStream.DisposeAsync().AsTask());
        Check.Require(!asyncStream.CanRead, nameof(ResponseStream.Dispose));
        RequireThrows<ObjectDisposedException>(() => asyncResponse.Content.ReadAsStream());
    }

    private static (Exception? Error, RequestAttempt Attempt) ReadFailure(Exception failure)
    {
        using var response = new HttpResponseMessage();
        var attempt = new RequestAttempt(new object(), AttemptOwner.Transfer);
        using var stream = new ResponseStream(response, new ThrowingStream(failure), attempt, CancellationToken.None);
        Exception? error = null;
        try
        {
            stream.Read(new byte[1], 0, 1);
        }
        catch (Exception caught)
        {
            error = caught;
        }
        return (error, attempt);
    }

    private static async Task<Exception?> AwaitFaultAsync(Task task)
    {
        Check.Require(await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) == task,
            "a stalled read must observe cancellation");
        try
        {
            await task;
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static void RequireThrows<TException>(Action action) where TException : Exception
    {
        var threw = false;
        try
        {
            action();
        }
        catch (TException)
        {
            threw = true;
        }
        Check.Require(threw, typeof(TException).Name);
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        var threw = false;
        try
        {
            await action();
        }
        catch (TException)
        {
            threw = true;
        }
        Check.Require(threw, typeof(TException).Name);
    }

    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingStream(Exception failure) : ReadOnlyStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(failure);
    }

    private sealed class StallingStream(CancellationToken stall = default) : ReadOnlyStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            StallAsync(stall.CanBeCanceled ? stall : cancellationToken);

        private static async Task<int> StallAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class ThrowingCloseStream : ReadOnlyStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new IOException("body close failed");
        }
    }

    private sealed class PoolReadStream(byte[] content) : MemoryStream(content)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Run(() => Read(buffer, offset, count), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() =>
            {
                var bytes = new byte[buffer.Length];
                var count = Read(bytes, 0, bytes.Length);
                bytes.AsSpan(0, count).CopyTo(buffer.Span);
                return count;
            }, cancellationToken));
    }

    internal sealed class BlockedSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
        }

        public override void Send(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("Send on a blocked synchronization context");
    }
}
