using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox loopback callback: parses the browser return, receives one return on the fixed
/// callback, and serves the fixed receipt page. Static pure helpers plus one socket-lifetime receiver.</summary>
public static class SignInCallback
{
    // RFC 9112 section 3 recommends supporting request lines of at least 8000 octets.
    private const int MaximumRequestLineBytes = 8000;

    /// <summary>The authorization code of a verified loopback return; a wrong path, state, error return or absent code refuses.</summary>
    public static string ParseCallbackTarget(string target, string expectedPath, string expectedState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target, nameof(target));
        var uri = new Uri(SignIn.RedirectUri.GetLeftPart(UriPartial.Authority) + target);
        if (!uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal))
            throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
        var query = ParseQuery(uri.Query);
        if (query.ContainsKey("error")) throw new InvalidOperationException(Text.Get(TextKey.SignInReturnRetry));
        if (!query.TryGetValue("state", out var state) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
        return code;
    }

    /// <summary>The fixed receipt page: receipt when the return verified, retry when it did not; no callback values.</summary>
    public static string AcknowledgementPage(bool received)
    {
        var product = WebUtility.HtmlEncode(Text.Get(TextKey.ProductName));
        return Text.Get(TextKey.SignInPage, (TextArg.Name, product), (TextArg.Message, WebUtility.HtmlEncode(Text.Get(received ? TextKey.SignInReturnReceived : TextKey.SignInReturnRetry))));
    }

    /// <summary>Receives one loopback return on the fixed callback: starts listening, optionally opens the browser,
    /// verifies the return and serves the receipt page. A malformed or incomplete request serves the retry page when the
    /// socket is still writable. The supplied token bounds the whole attempt.</summary>
    public static async Task<string> ReceiveCallbackAsync(Uri redirectUri, string expectedPath, string expectedState,
        CancellationToken ct, Action? browser = null)
    {
        var listener = StartListener(redirectUri, expectedPath);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = lifetime.Token;
        var handlers = new List<Task<string?>>();
        var accept = AcceptAsync(listener, token);
        try
        {
            browser?.Invoke();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var next = await Task.WhenAny(handlers.Cast<Task>().Append(accept));
                if (next == accept)
                {
                    handlers.Add(HandleAsync(await accept, expectedPath, expectedState, token));
                    accept = AcceptAsync(listener, token);
                    continue;
                }
                var handled = (Task<string?>)next;
                handlers.Remove(handled);
                if (await handled is { } code) return code;
            }
        }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            // An accept can finish at the same time as the winning callback, before its client has a handler.
            await ((Task)accept).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (accept.IsCompletedSuccessfully) accept.Result.Dispose();
            await ((Task)Task.WhenAll(handlers)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task<string?> HandleAsync(TcpClient client, string path, string state, CancellationToken ct)
    {
        using var owned = client;
        await using var stream = client.GetStream();
        string target;
        try
        {
            target = await ReadCallbackTargetAsync(stream, ct);
            if (new Uri(SignIn.RedirectUri.GetLeftPart(UriPartial.Authority) + target).AbsolutePath != path)
            {
                await TryWriteRetryAsync(stream, ct);
                return null;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UriFormatException or SocketException)
        {
            await TryWriteRetryAsync(stream, ct);
            return null;
        }
        string code;
        try { code = ParseCallbackTarget(target, path, state); }
        catch { await TryWriteRetryAsync(stream, ct); throw; }
        await WritePageAsync(stream, AcknowledgementPage(received: true), ct);
        return code;
    }

    private static async Task<TcpClient> AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        try { return await listener.AcceptTcpClientAsync(ct); }
        catch (SocketException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    private static TcpListener StartListener(Uri redirectUri, string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(redirectUri, nameof(redirectUri));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath, nameof(expectedPath));
        var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        try { listener.Start(); }
        catch (SocketException) { throw new InvalidOperationException(Text.Get(TextKey.SignInReturnRetry)); }
        return listener;
    }

    private static async Task<string> ReadCallbackTargetAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaximumRequestLineBytes + 2];
        var offset = 0;
        var readingLine = true;
        string? target = null;
        var remaining = 16 * 1024;
        uint tail = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            remaining -= read;
            if (remaining < 0) throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
            if (read == 0) throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
            for (var i = offset; i < offset + read; i++)
            {
                // RFC 9112 section 2.1: the empty line ends the headers, even across reads.
                tail = (tail << 8) | buffer[i];
                if (readingLine && (ushort)tail == 0x0D0A)
                {
                    readingLine = false;
                    var parts = Encoding.ASCII.GetString(buffer, 0, i - 1).Split(' ');
                    if (parts.Length == 3 && parts[0] == HttpMethod.Get.Method && parts[2] == Routes.LoopbackHttpVersion)
                        target = parts[1];
                }
                if (tail == 0x0D0A0D0A) return target ?? throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
            }
            offset += read;
            // Drain even a rejected request before sending its retry page; never retain the header block.
            if (offset == buffer.Length) readingLine = false;
            if (!readingLine) offset = 0;
        }
    }

    // Best effort for a return that cannot be verified, bounded by the same attempt token; a closed socket or a
    // cancelled attempt simply ends it without waiting.
    private static async Task TryWriteRetryAsync(Stream stream, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try { await WritePageAsync(stream, AcknowledgementPage(received: false), ct); }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    private static async Task WritePageAsync(Stream stream, string html, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(CliCatalogResource.Fill(Routes.LoopbackResponseHead,
            [(nameof(Routes.LoopbackHttpVersion), Routes.LoopbackHttpVersion),
             (nameof(bytes.Length), bytes.Length.ToString(CultureInfo.InvariantCulture))]));
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            result[Uri.UnescapeDataString(pieces[0].Replace('+', ' '))] =
                pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1].Replace('+', ' ')) : string.Empty;
        }
        return result;
    }

}
