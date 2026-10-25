using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Massdex.SyncShark;

namespace Massdex.SyncShark.DropboxCli;

/// <summary>A scripted, recording HTTP handler for the Dropbox route caller checks (design dropbox-cli.md §5).</summary>
internal sealed class DropboxRouteWire : HttpMessageHandler
{
    /// <summary>The fixed token the checks assert never leaves the process.</summary>
    internal const string SecretToken = "test-token-SECRET";

    /// <summary>One recorded request: its host, path, headers and body.</summary>
    internal sealed record Request(string Host, string Route, string? Authorization, string? PathRoot,
        string? ApiArg, string? ContentType, byte[] Body)
    {
        internal string BodyText => Encoding.UTF8.GetString(Body);
    }

    private sealed record Scripted(HttpStatusCode Status, string? Json, byte[]? Bytes, string? ApiResult,
        TimeSpan? RetryAfter, Exception? Fault, Exception? BodyFault, byte[]? BodyPrefix);

    private readonly Dictionary<string, Queue<Scripted>> _scripts = new(StringComparer.Ordinal);

    /// <summary>Every request the caller made, in order.</summary>
    internal List<Request> Requests { get; } = new();

    /// <summary>Queues one scripted response for a route, named by its table name or absolute path.</summary>
    internal void Respond(string route, HttpStatusCode status, string? json = null, byte[]? bytes = null,
        string? apiResult = null, TimeSpan? retryAfter = null) =>
        Enqueue(route, new Scripted(status, json, bytes, apiResult, retryAfter, null, null, null));

    /// <summary>Queues a transport fault (connection drop or HttpClient timeout) instead of a response.</summary>
    internal void RespondFault(string route, Exception fault) =>
        Enqueue(route, new Scripted(HttpStatusCode.OK, null, null, null, null, fault, null, null));

    /// <summary>Queues a response whose body read throws after an optional prefix (a mid-response drop).</summary>
    internal void RespondBodyFault(string route, HttpStatusCode status, Exception fault, byte[]? prefix = null) =>
        Enqueue(route, new Scripted(status, null, null, null, null, null, fault, prefix));

    private void Enqueue(string route, Scripted scripted)
    {
        var key = Normalize(route);
        if (!_scripts.TryGetValue(key, out var queue)) _scripts[key] = queue = new Queue<Scripted>();
        queue.Enqueue(scripted);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(token);
        Requests.Add(new Request(request.RequestUri.Host, path, request.Headers.Authorization?.ToString(),
            Header(request, "Dropbox-API-Path-Root"), Header(request, "Dropbox-API-Arg"),
            request.Content?.Headers.ContentType?.MediaType, body));
        if (!_scripts.TryGetValue(path, out var queue) || queue.Count == 0)
            throw new InvalidOperationException($"The Dropbox route {path} was called without a scripted response.");
        var script = queue.Dequeue();
        if (script.Fault is not null) throw script.Fault;
        var response = new TrackedResponse(script.Status)
        {
            Content = script.BodyFault is not null
                ? new StreamContent(new FaultingBodyStream(script.BodyPrefix ?? Array.Empty<byte>(), script.BodyFault))
                : script.Bytes is not null
                    ? new ByteArrayContent(script.Bytes)
                    : new StringContent(script.Json ?? string.Empty, Encoding.UTF8, "application/json")
        };
        if (script.ApiResult is not null)
            response.Headers.TryAddWithoutValidation("Dropbox-API-Result", script.ApiResult);
        if (script.RetryAfter is { } retryAfter) response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        Responses.Add(response);
        return response;
    }

    /// <summary>Every response handed to the caller; the checks assert each one was disposed.</summary>
    internal List<TrackedResponse> Responses { get; } = new();

    /// <summary>A response that records whether the caller released it.</summary>
    internal sealed class TrackedResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        internal bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // A table route name gets the /2/ prefix; an absolute path (such as /oauth2/token) is kept as given.
    private static string Normalize(string route) =>
        route.StartsWith('/') ? route : "/2/" + route;

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>A read-only body that yields an optional prefix and then throws, simulating a mid-response drop.</summary>
    private sealed class FaultingBodyStream(byte[] prefix, Exception fault) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var take = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, take).CopyTo(buffer);
                _position += take;
                return ValueTask.FromResult(take);
            }
            throw fault;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw fault;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A fixed Dropbox token source whose secret must never appear on any output stream (design §3.4).</summary>
internal sealed class SecretDropboxTokenSource : IDropboxAccessTokenSource
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DropboxRouteWire.SecretToken);
}
