using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark;

// A short-lived continuation of the existing loopback listener. Its sole effect is requesting
// the already-running GUI to show itself. No account state, auth code, OAuth state or tokens retained.
internal sealed class OAuthReturnBridge
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, OAuthReturnBridge> Active = [];
    private readonly TcpListener _listener;
    private readonly Uri _redirect;
    private readonly string _nonce;
    private readonly Action _activate;
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromMinutes(2));

    private OAuthReturnBridge(TcpListener listener, Uri redirect, string nonce, Action activate)
    { _listener = listener; _redirect = redirect; _nonce = nonce; _activate = activate; }

    internal static void StopPrevious(int port)
    {
        lock (Gate)
            if (Active.Remove(port, out var prior)) prior.Stop();
    }

    internal static void Start(TcpListener listener, Uri redirect, string nonce, Action activate)
    {
        var bridge = new OAuthReturnBridge(listener, redirect, nonce, activate);
        lock (Gate)
        {
            if (Active.Remove(redirect.Port, out var prior)) prior.Stop();
            Active.Add(redirect.Port, bridge);
        }
        _ = bridge.RunAsync(); // Internally observed, bounded and superseded by the next auth attempt.
    }

    private void Stop() { _lifetime.Cancel(); _listener.Stop(); }

    private async Task RunAsync()
    {
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                using var requestTime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                requestTime.CancelAfter(TimeSpan.FromSeconds(3));
                var stream = client.GetStream();
                ReturnRequestOutcome outcome;
                try { outcome = await ReadReturnRequestAsync(stream, requestTime.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { continue; }
                if (outcome == ReturnRequestOutcome.NotAReturnRequest) continue;
                if (outcome == ReturnRequestOutcome.Rejected)
                {
                    // V7/F16: a fully bounded, parseable /return POST that failed a check is answered with
                    // one fixed non-secret page instead of a silent socket reset. Nothing is relaxed: the
                    // same exact Host/Origin/method/body/nonce checks still decide acceptance, the activation
                    // stays unconsumed and the bounded accept loop keeps waiting for a correct request.
                    try
                    {
                        await DropboxLoopbackReceiver.WriteAcknowledgementAsync(stream,
                            DropboxLoopbackReceiver.ReturnRejectedHtml, requestTime.Token);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException) { }
                    continue;
                }
                // Consume only once, before notifying the window. A retry cannot re-run provider effects.
                _activate();
                await DropboxLoopbackReceiver.WriteAcknowledgementAsync(stream,
                    DropboxLoopbackReceiver.ReturnPage("Back to Sync Shark", "The app has been asked to open. If it stays behind this window, select Sync Shark on the taskbar."), requestTime.Token);
                return;
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            lock (Gate)
            {
                if (Active.TryGetValue(_redirect.Port, out var current) && ReferenceEquals(current, this)) Active.Remove(_redirect.Port);
                _listener.Stop(); _lifetime.Dispose();
            }
        }
    }

    // The bounded outcome of one accepted loopback connection. Only Accepted consumes the one-use
    // activation; Rejected is reserved for a request whose complete bounded bytes were received and whose
    // declared body length is small enough to be answered safely, so a rejection never becomes a reset.
    private enum ReturnRequestOutcome { NotAReturnRequest, Rejected, Accepted }

    private async Task<ReturnRequestOutcome> ReadReturnRequestAsync(NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[4096]; var length = 0;
        var complete = false;
        while (length < bytes.Length)
        {
            if (await stream.ReadAsync(bytes.AsMemory(length, 1), cancellationToken) == 0) return ReturnRequestOutcome.NotAReturnRequest;
            length++;
            if (length >= 4 && bytes.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8)) { complete = true; break; }
        }
        if (!complete) return ReturnRequestOutcome.NotAReturnRequest;
        var lines = Encoding.ASCII.GetString(bytes, 0, length).Split("\r\n", StringSplitOptions.None);
        if (lines[0] != "POST /return HTTP/1.1") return ReturnRequestOutcome.NotAReturnRequest;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length != 0))
        {
            var colon = line.IndexOf(':');
            // A malformed or duplicated header cannot be bounded safely, so it keeps the silent close.
            if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                return ReturnRequestOutcome.NotAReturnRequest;
        }
        // An unbounded body (chunked or an implausible length) cannot be answered after a full read, so it
        // stays a silent close rather than claiming a bounded rejection.
        if (headers.ContainsKey("Transfer-Encoding") ||
            !int.TryParse(headers.GetValueOrDefault("Content-Length"), out var count) || count is < 0 or > 4096)
            return ReturnRequestOutcome.NotAReturnRequest;
        var body = new byte[count];
        await stream.ReadExactlyAsync(body, cancellationToken);
        if (headers.GetValueOrDefault("Host") != _redirect.Authority ||
            headers.GetValueOrDefault("Origin") != _redirect.GetLeftPart(UriPartial.Authority) ||
            headers.GetValueOrDefault("Content-Type") != "application/x-www-form-urlencoded" ||
            count != 70)
            return ReturnRequestOutcome.Rejected;
        var expected = Encoding.ASCII.GetBytes("nonce=" + _nonce);
        return CryptographicOperations.FixedTimeEquals(body, expected)
            ? ReturnRequestOutcome.Accepted : ReturnRequestOutcome.Rejected;
    }
}
