using System.Net;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for ResponseStream.</summary>
public static class ResponseStreamChecks
{
    /// <summary>Verifies body completion, failure, pending cancellation, abandonment and cleanup.</summary>
    public static async Task RunAsync()
    {
        // Completion: reading to EOF reports Completed once; disposal releases the source.
        var inner = new TrackingStream(new byte[] { 1, 2, 3 });
        var completed = new List<ResponseStream.BodyOutcome>();
        await using (var body = new ResponseStream().Open(new HttpResponseMessage(HttpStatusCode.OK), inner,
            CancellationToken.None, completed.Add))
        {
            var buffer = new byte[8];
            var total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory())) > 0) total += read;
            Check.Require(total == 3, "body bytes were not forwarded");
        }
        Check.Require(completed.Count == 1 && completed[0] == ResponseStream.BodyOutcome.Completed,
            "body completion was not reported once");
        Check.Require(inner.Disposed, "disposing the body did not release the source");

        // Source failure: the exception is rethrown and reported as Failed.
        var failed = new List<ResponseStream.BodyOutcome>();
        await using (var body = new ResponseStream().Open(new HttpResponseMessage(HttpStatusCode.OK),
            new FailingStream(), CancellationToken.None, failed.Add))
        {
            try
            {
                await body.ReadAsync(new byte[4]);
                throw new InvalidDataException("source failure swallowed");
            }
            catch (IOException)
            {
            }
        }
        Check.Require(failed.Count == 1 && failed[0] == ResponseStream.BodyOutcome.Failed,
            "source failure was not reported");

        // Cancellation while a body read is pending surfaces and is reported as Cancelled, not Failed.
        using var pendingCancel = new CancellationTokenSource();
        var pendingOutcomes = new List<ResponseStream.BodyOutcome>();
        await using (var body = new ResponseStream().Open(new HttpResponseMessage(HttpStatusCode.OK),
            new BlockingStream(), pendingCancel.Token, pendingOutcomes.Add))
        {
            var reading = body.ReadAsync(new byte[4]).AsTask();
            pendingCancel.Cancel();
            try
            {
                await reading;
                throw new InvalidDataException("pending cancellation swallowed");
            }
            catch (OperationCanceledException)
            {
            }
        }
        Check.Require(pendingOutcomes.Count == 1 && pendingOutcomes[0] == ResponseStream.BodyOutcome.Cancelled,
            "pending cancellation was not reported as cancelled");

        // Disposing before EOF abandons the body: never Completed, source released.
        var abandonedInner = new TrackingStream(new byte[] { 1, 2, 3 });
        var abandoned = new List<ResponseStream.BodyOutcome>();
        await using (var body = new ResponseStream().Open(new HttpResponseMessage(HttpStatusCode.OK),
            abandonedInner, CancellationToken.None, abandoned.Add))
        {
            _ = await body.ReadAsync(new byte[] { 0 }.AsMemory());
        }
        Check.Require(abandoned.Count == 1 && abandoned[0] == ResponseStream.BodyOutcome.Abandoned,
            "an abandoned body was not reported as abandoned");
        Check.Require(abandonedInner.Disposed, "abandoning the body did not release the source");

        // A source-dispose failure must not block response release and is rethrown after cleanup (not lost).
        var responseContent = new TrackingContent();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = responseContent };
        var thrown = new List<ResponseStream.BodyOutcome>();
        try
        {
            await using (var body = new ResponseStream().Open(response, new ThrowingDisposeStream(),
                CancellationToken.None, thrown.Add))
            {
            }
        }
        catch (IOException)
        {
        }
        Check.Require(responseContent.Disposed, "response content survived a source-dispose failure");
        Check.Require(thrown.Count == 1 && thrown[0] == ResponseStream.BodyOutcome.Abandoned,
            "a source-dispose failure did not end the body as abandoned");

        // A throwing observer runs outside the read classifier: its exception propagates as itself, once.
        var observerCalls = 0;
        await using (var body = new ResponseStream().Open(new HttpResponseMessage(HttpStatusCode.OK),
            new TrackingStream(new byte[] { 9 }), CancellationToken.None,
            _ => { observerCalls++; throw new InvalidOperationException("observer"); }))
        {
            try
            {
                var buffer = new byte[2];
                while (await body.ReadAsync(buffer.AsMemory()) > 0)
                {
                }
                throw new InvalidDataException("observer throw swallowed");
            }
            catch (InvalidOperationException)
            {
            }
        }
        Check.Require(observerCalls == 1, "observer was not invoked exactly once");
    }

    private sealed class TrackingStream : MemoryStream
    {
        internal bool Disposed { get; private set; }

        internal TrackingStream(byte[] bytes) : base(bytes, writable: false)
        {
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            throw new IOException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingDisposeStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) => throw new IOException();

        public override ValueTask DisposeAsync() => throw new IOException();
    }

    private sealed class TrackingContent : HttpContent
    {
        internal bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
