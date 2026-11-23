using System.Net;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for ResponseStream.</summary>
public static class ResponseStreamChecks
{
    /// <summary>Verifies body completion, failure, pending cancellation, abandonment and cleanup.</summary>
    public static async Task RunAsync()
    {
        await CheckProgressExpiryAsync();

        // Completion: reading to EOF reports Completed once; disposal releases the source.
        var inner = new TrackingStream(new byte[] { 1, 2, 3 });
        var completed = new List<ResponseStream.BodyOutcome>();
        await using (var body = new ResponseStream().Open(new HttpResponseMessage(HttpStatusCode.OK), inner,
            CancellationToken.None, completed.Add))
        {
            var buffer = new byte[8];
            var total = await body.ReadAsync(buffer.AsMemory(0, 1));
            Check.Require(total == 1 && completed.Count == 0, "a positive prefix was credited as a completed transfer");
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory())) > 0) total += read;
            Check.Require(total == 3, "body bytes were not forwarded");
            Check.Require(completed is [ResponseStream.BodyOutcome.Completed], "EOF did not finish the transfer before disposal");
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
        var closeError = new IOException();
        var thrown = new List<ResponseStream.BodyOutcome>();
        try
        {
            await using (var body = new ResponseStream().Open(response, new ThrowingDisposeStream(closeError),
                CancellationToken.None, thrown.Add))
            {
            }
            throw new InvalidDataException("source close error swallowed");
        }
        catch (IOException error) when (ReferenceEquals(error, closeError))
        {
        }
        Check.Require(responseContent.Disposed, "response content survived a source-dispose failure");
        Check.Require(thrown.Count == 1 && thrown[0] == ResponseStream.BodyOutcome.Abandoned,
            "a source-dispose failure did not end the body as abandoned");

        // A throwing observer runs outside the read classifier: its exception propagates as itself, once.
        using var observerStop = new CancellationTokenSource();
        var observerError = new InvalidOperationException();
        var observed = new List<ResponseStream.BodyOutcome>();
        await using (var body = new ResponseStream().Open(new HttpResponseMessage(HttpStatusCode.OK),
            new TrackingStream(new byte[] { 9 }), observerStop.Token,
            outcome => { observed.Add(outcome); observerStop.Cancel(); throw observerError; }))
        {
            try
            {
                var buffer = new byte[2];
                while (await body.ReadAsync(buffer.AsMemory()) > 0)
                {
                }
                throw new InvalidDataException("observer throw swallowed");
            }
            catch (InvalidOperationException error) when (ReferenceEquals(error, observerError))
            {
            }
        }
        Check.Require(observed is [ResponseStream.BodyOutcome.Completed], "observer did not receive one completion");
    }

    private static async Task CheckProgressExpiryAsync()
    {
        var clock = new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var cleanup = new CancellationTokenSource();
        var outcomes = new List<ResponseStream.BodyOutcome>();
        await using var body = new ResponseStream(clock).Open(new HttpResponseMessage(HttpStatusCode.OK),
            new BlockingStream(prefix: true), cleanup.Token, outcomes.Add);
        Check.Require(await body.ReadAsync(new byte[1]) == 1, "download did not deliver its prefix");
        clock.Advance(TimeSpan.FromSeconds(121));
        var pending = body.ReadAsync(new byte[1]).AsTask();
        try
        {
            Check.Require(!pending.IsCompleted && clock.ScheduledDelay == TimeSpan.FromSeconds(120),
                "next pending read did not get a fresh 120 seconds after consumer idle");
            clock.ElapseScheduledDelay();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException error) when (error.InnerException is TimeoutException &&
                outcomes is [ResponseStream.BodyOutcome.Failed] && !cleanup.IsCancellationRequested) { return; }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
            throw new InvalidDataException("stalled download did not fail and notify its terminal owner before disposal");
        }
        finally { cleanup.Cancel(); }
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

    private sealed class BlockingStream(bool prefix = false) : Stream
    {
        private bool _prefix = prefix;
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
            if (_prefix) { _prefix = false; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingDisposeStream(IOException error) : Stream
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

        protected override void Dispose(bool disposing) => throw error;

        public override ValueTask DisposeAsync() => throw error;
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
