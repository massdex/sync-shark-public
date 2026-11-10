using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>
/// The one bounded response-body reader shared by every provider: a byte ceiling, the caller's cancellation and
/// an idle deadline. Provider-specific callers pass their own size, idle limit and oversized-body text; nothing
/// here names a provider.
/// </summary>
internal static class SyncResponseBody
{
    /// <summary>The size ceiling an error body is never read past when a caller has no narrower fact.</summary>
    internal const int MaximumErrorBodyBytes = 64 * 1024;

    /// <summary>The idle deadline used when a caller has no configured read-stall bound.</summary>
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    internal static void RequireWithinSize(HttpResponseMessage response, int maximumBytes, string oversizedMessage)
    {
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException(oversizedMessage);
    }

    // One copy-free body buffer: callers read text/JSON straight out of the returned stream and only the small
    // error-body path materialises a byte array.
    private static async Task<MemoryStream> ReadBufferAsync(Stream content, int maximumBytes, TimeSpan idleTimeout,
        string oversizedMessage, CancellationToken cancellationToken)
    {
        var bounded = new MemoryStream();
        try
        {
            using var idle = new IdleBound(cancellationToken, idleTimeout);
            var buffer = new byte[8 * 1024];
            while (true)
            {
                var read = await idle.ReadAsync(content, buffer);
                if (read == 0) break;
                if (bounded.Length > maximumBytes - read) throw new InvalidDataException(oversizedMessage);
                await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return bounded;
        }
        catch
        {
            bounded.Dispose();
            throw;
        }
    }

    internal static async Task<byte[]> ReadBytesAsync(Stream content, int maximumBytes, TimeSpan idleTimeout,
        string oversizedMessage, CancellationToken cancellationToken)
    {
        using var bounded = await ReadBufferAsync(content, maximumBytes, idleTimeout, oversizedMessage,
            cancellationToken).ConfigureAwait(false);
        return bounded.ToArray();
    }

    internal static async Task<string> ReadTextAsync(Stream content, int maximumBytes, TimeSpan idleTimeout,
        string oversizedMessage, CancellationToken cancellationToken)
    {
        using var bounded = await ReadBufferAsync(content, maximumBytes, idleTimeout, oversizedMessage,
            cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bounded.GetBuffer(), 0, checked((int)bounded.Length));
    }

    internal static async Task<JsonDocument> ReadJsonAsync(Stream content, int maximumBytes, TimeSpan idleTimeout,
        string oversizedMessage, CancellationToken cancellationToken, int maximumDepth = 64,
        bool emptyObject = false)
    {
        using var bounded = await ReadBufferAsync(content, maximumBytes, idleTimeout, oversizedMessage,
            cancellationToken).ConfigureAwait(false);
        // JsonDocument holds this exact memory (no pooled buffer is returned here). Disposing the MemoryStream
        // wrapper does not clear its byte array, and the document keeps that array reachable, so the returned
        // document stays valid after the stream is disposed by this method.
        if (emptyObject && bounded.Length == 0) return JsonDocument.Parse("{}");
        return JsonDocument.Parse(bounded.GetBuffer().AsMemory(0, checked((int)bounded.Length)),
            new JsonDocumentOptions { MaxDepth = maximumDepth });
    }

    /// <summary>
    /// Reads one error body under the same bounds and replaces the response content with the bytes already read,
    /// so the adapter that consumes the response can still inspect the same body after classification. A failed
    /// bounded read still leaves a readable body behind: an empty content carrying the original headers. The
    /// original exception is always rethrown, so the caller never mistakes a classification failure for success.
    /// </summary>
    internal static async Task<string> ReadAndReplaceAsync(HttpResponseMessage response, int maximumBytes,
        TimeSpan idleTimeout, string oversizedMessage, CancellationToken cancellationToken)
    {
        var original = response.Content;
        byte[] bytes;
        try
        {
            RequireWithinSize(response, maximumBytes, oversizedMessage);
            await using var stream = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            bytes = await ReadBytesAsync(stream, maximumBytes, idleTimeout, oversizedMessage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ReplaceContent(response, original, []);
            throw;
        }
        ReplaceContent(response, original, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    // Replace first, dispose second: a concurrent reader never observes a disposed or consumed body.
    private static void ReplaceContent(HttpResponseMessage response, HttpContent original, byte[] bytes)
    {
        var replacement = new ByteArrayContent(bytes);
        foreach (var header in original.Headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        response.Content = replacement;
        original.Dispose();
    }

    // One cancellable idle bound shared by every network body read: the bound is armed immediately before each
    // read and disarmed immediately after, so time spent throttling or writing between reads never counts toward
    // the idle window. A stall that is not the caller's own cancellation raises TimeoutException; the caller's
    // cancellation still surfaces as OperationCanceledException, and when both are observed together the caller's
    // cancellation takes precedence.
    internal sealed class IdleBound(CancellationToken callerToken, TimeSpan timeout) : IDisposable
    {
        private readonly CancellationTokenSource _stall =
            CancellationTokenSource.CreateLinkedTokenSource(callerToken);

        internal async ValueTask<int> ReadAsync(Stream input, Memory<byte> buffer)
        {
            if (timeout != Timeout.InfiniteTimeSpan) _stall.CancelAfter(timeout);
            try
            {
                return await input.ReadAsync(buffer, _stall.Token);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                callerToken.ThrowIfCancellationRequested();
                throw new ProviderTransportTimeoutException(
                    $"The provider stopped sending the response body for {timeout.TotalSeconds:N0} seconds; the read was aborted.");
            }
            finally
            {
                if (timeout != Timeout.InfiniteTimeSpan && !_stall.IsCancellationRequested)
                    _stall.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose() => _stall.Dispose();
    }
}
