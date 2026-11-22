using System.Net;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for ResponseStream.</summary>
public static class ResponseStreamChecks
{
    /// <summary>Verifies body completion, source failure and caller cancellation are reported once.</summary>
    public static async Task RunAsync()
    {
        // Completion: reading to EOF reports Completed once; disposal releases the inner stream and the response.
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

        // Caller cancellation: it surfaces and is reported as Cancelled, not Failed.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var outcomes = new List<ResponseStream.BodyOutcome>();
        await using (var body = new ResponseStream().Open(new HttpResponseMessage(HttpStatusCode.OK),
            new MemoryStream(new byte[] { 1 }), cancelled.Token, outcomes.Add))
        {
            try
            {
                await body.ReadAsync(new byte[4]);
                throw new InvalidDataException("cancellation swallowed");
            }
            catch (OperationCanceledException)
            {
            }
        }
        Check.Require(outcomes.Count == 1 && outcomes[0] == ResponseStream.BodyOutcome.Cancelled,
            "caller cancellation was not reported");
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
}
