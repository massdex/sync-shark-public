using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Massdex.SyncShark.Engine.Tests.Harness;

/// <summary>
/// Supplies the browser-launch callback that SignIn.ReceiveCallbackAsync already accepts, so a check drives the real
/// loopback return itself instead of opening a browser. <see cref="Action"/> connects to the listening loopback port
/// and sends one callback return in the background; <see cref="Started"/> and <see cref="Sent"/> record that the
/// product really invoked the callback and the request was written, and <see cref="Drain"/> completes once the
/// acknowledgement page is read. The product still verifies the path, state and code, so no outcome is faked.
/// </summary>
internal sealed class LoopbackBrowser
{
    private readonly Uri _redirect;
    private readonly string _request;
    private Task _drain = Task.CompletedTask;
    private bool _started;
    private bool _sent;

    private LoopbackBrowser(Uri redirect, string request) { _redirect = redirect; _request = request; }

    /// <summary>A synthetic browser return for one authorization code and state on the given loopback redirect.</summary>
    internal static LoopbackBrowser For(Uri redirect, string code, string state) =>
        new(redirect, "GET " + redirect.AbsolutePath + "?code=" + Uri.EscapeDataString(code) +
            "&state=" + Uri.EscapeDataString(state) + " HTTP/1.1\r\nHost: localhost\r\n\r\n");

    /// <summary>The browser-launch callback to pass to ReceiveCallbackAsync; returns at once and sends in the background.</summary>
    internal Action Action => () => { _started = true; _drain = SendAsync(); };

    /// <summary>True once the product invoked the callback (it binds the listener before invoking, so a start failure leaves this false).</summary>
    internal bool Started => _started;

    /// <summary>True once the callback return was written to the loopback socket.</summary>
    internal bool Sent => _sent;

    /// <summary>Completes once the return was sent and the acknowledgement page drained; rethrows a send failure.</summary>
    internal Task Drain => _drain;

    private async Task SendAsync()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _redirect.Port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(_request));
        _sent = true;
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await reader.ReadToEndAsync();
    }
}
