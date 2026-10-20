using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static class OAuthReturnBridgeChecks
{
    internal static void RenderPreview(string destination)
    {
        // Output is immutable. These are exact production templates with a synthetic activation nonce.
        if (Directory.Exists(destination)) throw new IOException("Use a new preview destination.");
        Directory.CreateDirectory(destination);
        foreach (var item in new[]
        {
            ("response-received", "Response received", "Sync Shark is checking this connection. The verified result will appear in the app."),
            ("connection-not-approved", "Connection not approved", "This connection was cancelled or not approved. Return to the app to try again."),
            ("verification-failed", "Connection could not be completed", "This response could not be verified. Return to the app to start a new connection.")
        })
        {
            using var output = new StreamWriter(new FileStream(Path.Combine(destination, item.Item1 + ".html"), FileMode.CreateNew));
            output.Write(DropboxLoopbackReceiver.ReturnPage(item.Item2, item.Item3, new string('0', 64)));
        }
        Console.WriteLine("PASS exact callback templates rendered; synthetic nonce, no credentials, no provider effects");
    }

    internal static async Task RunAsync()
    {
        var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        var redirect = new Uri($"http://127.0.0.1:{port}/fixture/callback/");
        // The Host header the bridge compares against is the bare authority (no scheme); the Origin a
        // browser sends is the scheme plus host plus port. Keeping them separate is what makes the
        // null-Origin proof isolate the Origin check instead of failing an unrelated Host check.
        var origin = redirect.GetLeftPart(UriPartial.Authority);
        var host = redirect.Authority;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var receiver = new DropboxLoopbackReceiver(() => { Interlocked.Increment(ref count); activated.TrySetResult(); });
        try
        {
            var pending = receiver.ReceiveAsync(redirect, "synthetic-state", lifetime.Token);
            using var response = await http.GetAsync(redirect + "?state=synthetic-state&code=synthetic-code", lifetime.Token);
            var html = await response.Content.ReadAsStringAsync(lifetime.Token);
            Require((await pending).Code == "synthetic-code", "Original exact auth result remains unchanged");
            Require(html.Contains("class=\"mark\"") && html.Contains("Segoe UI") && html.Contains("Return to Sync Shark") &&
                !html.Contains("synthetic-state") && !html.Contains("synthetic-code"), "Large stacked brand and actual return action without OAuth data");
            Require(response.Headers.GetValues("Content-Security-Policy").Single().Contains("form-action 'self'") &&
                response.Headers.CacheControl!.NoStore, "Local-only form and private page headers");
            // V7/F16: a page carrying the Return to Sync Shark form must NOT be served with no-referrer. Under
            // the Fetch/HTML Origin serialization a no-referrer request sends Origin: null, so the real browser
            // form POST could never satisfy the bridge's exact Origin check and the callback always reset.
            // Origin-only keeps that check exact and still never sends the callback path or query.
            Require(response.Headers.GetValues("Referrer-Policy").Single() == "origin",
                "Form-bearing acknowledgement uses the origin-only referrer policy so the form POST keeps a real Origin");
            var nonce = Regex.Match(html, "name=nonce value=\"([A-F0-9]{64})\"").Groups[1].Value;
            Require(nonce.Length == 64 && count == 0, "Independent activation nonce; callback alone never foregrounds app");
            await RejectedAsync(nonce, "https://unrelated.example", "Cross-origin activation refused");
            await RejectedAsync(new string('0', 64), origin, "Wrong nonce refused");
            // V7/F16 isolated rejection: Host, target, method, content type, declared length and body are all
            // exactly valid; only the Origin is the "null" a real browser sends when the served page carried
            // no-referrer. The fixed rejection therefore comes from the Origin check alone.
            var nullOrigin = await RawPostAsync(nonce, "null");
            Require(nullOrigin.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal) &&
                nullOrigin.Contains("Return to Sync Shark was not completed", StringComparison.Ordinal) &&
                nullOrigin.Contains("Referrer-Policy: no-referrer", StringComparison.Ordinal) &&
                !nullOrigin.Contains(nonce, StringComparison.Ordinal) && !nullOrigin.Contains("<form", StringComparison.Ordinal),
                "An isolated null-Origin request with an otherwise valid Host and body is refused by the Origin check with the fixed page, never a silent reset");
            Require(count == 0, "The isolated null-Origin rejection consumed no activation and left the one-use nonce intact");
            // The same raw request with the real same-origin Origin is the one valid form.
            var corrected = await RawPostAsync(nonce, origin);
            Require(corrected.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal) &&
                corrected.Contains("Back to Sync Shark", StringComparison.Ordinal),
                "A raw same-origin form POST carrying the real Origin receives the fixed return acknowledgement");
            await activated.Task.WaitAsync(lifetime.Token);
            Require(count == 1, "GUI activation fires exactly once, only for the one valid same-origin raw form");
            // Starting another account flow immediately reclaims the same registered callback port.
            var next = receiver.ReceiveAsync(redirect, "next-state", lifetime.Token);
            var nextRaw = await RawAsync(port, "GET /fixture/callback/?state=next-state&code=next-code HTTP/1.1\r\nHost: " +
                host + "\r\nConnection: close\r\n\r\n", lifetime.Token);
            await next;
            // V7/F16 header contract: the whole callback response block - headers and page together - must
            // never carry the OAuth query, state or code, and the declared policy may only ever be the
            // origin-only form, never a full-path or full-query policy.
            Require(nextRaw.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal) &&
                nextRaw.Contains("Referrer-Policy: origin", StringComparison.Ordinal) &&
                !nextRaw.Contains("strict-origin", StringComparison.Ordinal) &&
                !nextRaw.Contains("unsafe-url", StringComparison.Ordinal) &&
                !nextRaw.Contains("/fixture/callback", StringComparison.Ordinal) &&
                !nextRaw.Contains("next-state", StringComparison.Ordinal) &&
                !nextRaw.Contains("next-code", StringComparison.Ordinal),
                "The complete callback response block can never carry the callback path, OAuth state or code back to the provider");
            Require(nextRaw.Contains("name=nonce") && count == 1, "Retry/new account does not wait for the prior return-page expiry");
            // No GUI attached (standalone CLI) remains an honest instruction, never a dead button.
            var cli = new DropboxLoopbackReceiver().ReceiveAsync(redirect, "cli-state", lifetime.Token);
            using var cliResponse = await http.GetAsync(redirect + "?state=cli-state&code=cli-code", lifetime.Token);
            var cliHtml = await cliResponse.Content.ReadAsStringAsync(lifetime.Token); await cli;
            Require(!cliHtml.Contains("<form") && cliHtml.Contains("Return to Sync Shark"), "Standalone CLI has no false GUI activation control");
            Require(cliResponse.Headers.GetValues("Referrer-Policy").Single() == "no-referrer",
                "A form-free acknowledgement keeps the original no-referrer policy");
        }
        finally { OAuthReturnBridge.StopPrevious(port); }

        HttpRequestMessage Post(string nonce, string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(redirect, "/return"))
                { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["nonce"] = nonce }) };
            request.Headers.Add("Origin", origin); return request;
        }
        async Task RejectedAsync(string nonce, string origin, string label)
        {
            using var request = Post(nonce, origin);
            using var rejected = await http.SendAsync(request, lifetime.Token);
            var body = await rejected.Content.ReadAsStringAsync(lifetime.Token);
            Require(rejected.StatusCode == HttpStatusCode.OK &&
                body.Contains("Return to Sync Shark was not completed", StringComparison.Ordinal) &&
                !body.Contains(nonce, StringComparison.Ordinal) && !body.Contains("<form", StringComparison.Ordinal) &&
                count == 0,
                label + ": fixed non-secret rejection page and no activation");
        }
        // The exact bytes a browser posts: bare-authority Host, the browser's Origin, the form content type
        // and a 70-byte nonce body.
        Task<string> RawPostAsync(string nonce, string originHeader) => RawAsync(port,
            "POST /return HTTP/1.1\r\nHost: " + host + "\r\nOrigin: " + originHeader + "\r\n" +
            "Content-Type: application/x-www-form-urlencoded\r\nContent-Length: 70\r\nConnection: close\r\n\r\n" +
            "nonce=" + nonce, lifetime.Token);
    }

    // One raw loopback exchange, so the exact bytes a browser would receive are what the checks inspect.
    private static async Task<string> RawAsync(int port, string request, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        using var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        var buffer = new byte[64 * 1024];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, length);
    }
    private static void Require(bool value, string label)
    { if (!value) throw new IOException(label); Console.WriteLine("PASS " + label); }
}
