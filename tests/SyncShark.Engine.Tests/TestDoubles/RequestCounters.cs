using System.Collections.Concurrent;
using System.Net;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Counts every Dropbox attempt at the SendAsync boundary reached by the real API, below limiter retries:
/// per-route attempts, statuses, errors, started/settled/in-flight, response bytes read and response
/// completion/disposal. It never buffers a body and preserves seekability, partial reads and disposal by
/// forwarding to the response's own stream. Authentication and account setup are a separate surface.</summary>
internal sealed class RequestCounters(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const string SetupRoute = "setup";
    private const string VersionPrefix = "/2/";
    private readonly ConcurrentDictionary<string, RouteCounts> _routes = new(StringComparer.Ordinal);
    private readonly RouteCounts _setup = new();
    private int _suspended;

    /// <summary>True while a caller reads its own account state, so those attempts stay out of measured snapshots.</summary>
    public bool SuspendObservation
    {
        get => Volatile.Read(ref _suspended) != 0;
        set => Volatile.Write(ref _suspended, value ? 1 : 0);
    }

    /// <summary>The separate authentication/account-setup surface, which <see cref="Snapshot()"/> never includes.</summary>
    public RouteSnapshot Setup => _setup.Snapshot(SetupRoute);

    /// <summary>Attempts recorded on one measured route, or zero when the route was not called.</summary>
    public long Attempts(string route) =>
        _routes.TryGetValue(route, out var counts) ? counts.Attempts : 0;

    /// <summary>Bounded measured totals at one instant; account setup is excluded.</summary>
    public Totals Snapshot()
    {
        var routes = new Dictionary<string, RouteSnapshot>(StringComparer.Ordinal);
        long started = 0, settled = 0, errors = 0, bytes = 0, completed = 0, disposed = 0;
        foreach (var (route, counts) in _routes)
        {
            var snapshot = counts.Snapshot(route);
            routes.Add(route, snapshot);
            started += snapshot.Attempts;
            settled += snapshot.Settled;
            errors += snapshot.Errors;
            bytes += snapshot.ResponseBytes;
            completed += snapshot.ResponsesCompleted;
            disposed += snapshot.ResponsesDisposed;
        }
        return new Totals(routes, started, settled, started - settled, errors, bytes, completed, disposed);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (SuspendObservation) return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var counts = CountsFor(RouteName(request));
        counts.Start();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            counts.Status((int)response.StatusCode);
            if (response.Content is { } content) response.Content = new ObservedContent(content, counts);
            return response;
        }
        catch
        {
            counts.Fail();
            throw;
        }
        finally
        {
            counts.Settle();
        }
    }

    private RouteCounts CountsFor(string route)
    {
        var registered = DropboxRouteTable.Embedded.Find(route);
        if (IsSetup(route))
        {
            // The OAuth token endpoint is outside the Dropbox API route table but is still authentication.
            if (registered is null && !route.StartsWith("oauth2/", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected Dropbox route '" + route + "'.");
            return _setup;
        }
        if (registered is null)
            throw new InvalidOperationException("Unexpected Dropbox route '" + route + "'.");
        return _routes.GetOrAdd(route, static _ => new RouteCounts());
    }

    private static string RouteName(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        return path.StartsWith(VersionPrefix, StringComparison.Ordinal)
            ? path[VersionPrefix.Length..]
            : path.TrimStart('/');
    }

    private static bool IsSetup(string route) =>
        route.StartsWith("users/", StringComparison.Ordinal) ||
        route.StartsWith("auth/", StringComparison.Ordinal) ||
        route.StartsWith("account/", StringComparison.Ordinal) ||
        route.StartsWith("oauth2/", StringComparison.Ordinal);

    /// <summary>One measured route's bounded totals; no per-request history is retained.</summary>
    internal sealed record RouteSnapshot(
        string Route,
        long Attempts,
        long Settled,
        long InFlight,
        long Errors,
        IReadOnlyDictionary<int, long> Statuses,
        long ResponseBytes,
        long ResponsesCompleted,
        long ResponsesDisposed);

    /// <summary>Every measured route's totals at one instant; account setup is excluded.</summary>
    internal sealed record Totals(
        IReadOnlyDictionary<string, RouteSnapshot> Routes,
        long Started,
        long Settled,
        long InFlight,
        long Errors,
        long ResponseBytes,
        long ResponsesCompleted,
        long ResponsesDisposed)
    {
        /// <summary>Attempts on one measured route, or zero when it was not called.</summary>
        public long Attempts(string route) => Routes.TryGetValue(route, out var counts) ? counts.Attempts : 0;
    }

    private sealed class RouteCounts
    {
        private readonly object _statusGate = new();
        private readonly Dictionary<int, long> _statuses = new();
        private long _attempts;
        private long _settled;
        private long _errors;
        private long _bytes;
        private long _completed;
        private long _disposed;

        public long Attempts => Interlocked.Read(ref _attempts);

        public void Start() => Interlocked.Increment(ref _attempts);
        public void Settle() => Interlocked.Increment(ref _settled);
        public void Fail() => Interlocked.Increment(ref _errors);
        public void AddBytes(int count) => Interlocked.Add(ref _bytes, count);
        public void Complete() => Interlocked.Increment(ref _completed);
        public void ObservedDispose() => Interlocked.Increment(ref _disposed);

        public void Status(int status)
        {
            lock (_statusGate) _statuses[status] = _statuses.GetValueOrDefault(status) + 1;
        }

        public RouteSnapshot Snapshot(string route)
        {
            var attempts = Interlocked.Read(ref _attempts);
            var settled = Interlocked.Read(ref _settled);
            Dictionary<int, long> statuses;
            lock (_statusGate) statuses = new Dictionary<int, long>(_statuses);
            return new RouteSnapshot(route, attempts, settled, attempts - settled,
                Interlocked.Read(ref _errors), statuses, Interlocked.Read(ref _bytes),
                Interlocked.Read(ref _completed), Interlocked.Read(ref _disposed));
        }
    }

    /// <summary>The response's own content with a byte-counting read stream; nothing is buffered and
    /// headers are kept.</summary>
    private sealed class ObservedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly RouteCounts _counts;

        public ObservedContent(HttpContent inner, RouteCounts counts)
        {
            _inner = inner;
            _counts = counts;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new ObservedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _counts);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _inner.CopyToAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            if (_inner.Headers.ContentLength is { } value)
            {
                length = value;
                return true;
            }
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Passes every read and disposal to the response stream while counting bytes and the terminal event.</summary>
    private sealed class ObservedStream(Stream inner, RouteCounts counts) : Stream
    {
        private int _completed;
        private int _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanTimeout => inner.CanTimeout;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
        public override int WriteTimeout { get => inner.WriteTimeout; set => inner.WriteTimeout = value; }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override void WriteByte(byte value) => inner.WriteByte(value);

        public override int Read(byte[] buffer, int offset, int count) =>
            Observed(inner.Read(buffer, offset, count), count);
        public override int Read(Span<byte> buffer) => Observed(inner.Read(buffer), buffer.Length);

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value < 0) Complete(); else counts.AddBytes(1);
            return value;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Observed(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);

        private int Observed(int count, int requested)
        {
            if (count > 0) counts.AddBytes(count);
            else if (requested > 0) Complete();
            return count;
        }

        private void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) counts.Complete();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { inner.Dispose(); }
                finally { counts.ObservedDispose(); }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                finally { counts.ObservedDispose(); }
            }
        }
    }
}
