using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox loopback callback: parses the browser return, receives one return on the fixed
/// callback, and serves the fixed receipt page. Static pure helpers plus one socket-lifetime receiver.</summary>
public static class SignInCallback
{
    private const int MaximumCallbackBytes = 16 * 1024;
    private const int MaximumCallbackLineBytes = 4096;

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
        try
        {
            using var registration = ct.Register(listener.Stop);
            browser?.Invoke();
            var client = await AcceptAsync(listener, ct);
            try
            {
                var stream = client.GetStream();
                try
                {
                    string target;
                    try { target = await ReadCallbackTargetAsync(stream, ct); }
                    catch
                    {
                        await TryWriteRetryAsync(stream, ct);
                        throw;
                    }
                    string code;
                    try { code = ParseCallbackTarget(target, expectedPath, expectedState); }
                    catch
                    {
                        await TryWriteRetryAsync(stream, ct);
                        throw;
                    }
                    await WritePageAsync(stream, AcknowledgementPage(received: true), ct);
                    return code;
                }
                finally { await stream.DisposeAsync(); }
            }
            finally { client.Dispose(); }
        }
        finally { listener.Stop(); }
    }

    private static TcpListener StartListener(Uri redirectUri, string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(redirectUri, nameof(redirectUri));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath, nameof(expectedPath));
        var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        try { listener.Start(1); }
        catch (SocketException) { throw new InvalidOperationException(Text.Get(TextKey.SignInReturnRetry)); }
        return listener;
    }

    private static async Task<TcpClient> AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        try { return await listener.AcceptTcpClientAsync(ct); }
        catch (SocketException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    private static async Task<string> ReadCallbackTargetAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaximumCallbackBytes];
        var length = 0;
        var lineLength = 0;
        var complete = false;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), ct);
            if (read == 0) break;
            length += read;
            if (lineLength == 0 && length >= 2 && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                lineLength = length - 2;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' &&
                buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
            {
                complete = true;
                break;
            }
        }
        if (!complete || lineLength == 0 || lineLength > MaximumCallbackLineBytes)
            throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
        var parts = Encoding.ASCII.GetString(buffer, 0, lineLength).Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || parts[2] != "HTTP/1.1")
            throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
        return parts[1];
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
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
            "Connection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
            "Content-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\n\r\n");
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
