using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Massdex.SyncShark;

internal static class OAuthPresentationChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        const string state = "fixture-state-do-not-render";
        const string code = "fixture-code-do-not-render";
        var cases = new[]
        {
            (Name: "pending", Query: $"code={code}&state={state}", Heading: "Response received", Exception: (Type?)null),
            (Name: "not-approved", Query: $"error=access_denied&state={state}", Heading: "Connection not approved", Exception: (Type?)typeof(InvalidOperationException)),
            (Name: "invalid-state", Query: $"code={code}&state=wrong-state", Heading: "Connection could not be completed", Exception: (Type?)typeof(InvalidDataException)),
            (Name: "missing-code", Query: $"state={state}", Heading: "Connection could not be completed", Exception: (Type?)typeof(InvalidDataException))
        };
        foreach (var item in cases)
        {
            var allocation = new TcpListener(IPAddress.Loopback, 0);
            allocation.Start();
            var port = ((IPEndPoint)allocation.LocalEndpoint).Port;
            allocation.Stop();
            var redirect = new Uri($"http://127.0.0.1:{port}/fixture/callback/");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var receive = new DropboxLoopbackReceiver().ReceiveAsync(redirect, state, timeout.Token);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
            using var response = await http.GetAsync(new Uri(redirect.AbsoluteUri + "?" + item.Query), timeout.Token);
            var html = await response.Content.ReadAsStringAsync(timeout.Token);
            Require(response.StatusCode == HttpStatusCode.OK && html.Contains(item.Heading, StringComparison.Ordinal), item.Name + " real loopback response");
            Require(response.Headers.CacheControl?.NoStore == true &&
                response.Headers.GetValues("Referrer-Policy").Single() == "no-referrer", item.Name + " private response headers");
            Require(!html.Contains(state) && !html.Contains(code) && !html.Contains("wrong-state") &&
                !html.Contains("access_denied") && !html.Contains("protected account") &&
                !html.Contains("<script", StringComparison.OrdinalIgnoreCase) && !html.Contains("https://"),
                item.Name + " contains only fixed local product copy");
            Exception? observed = null;
            DropboxLoopbackSession? session = null;
            try { session = await receive; }
            catch (Exception exception) { observed = exception; }
            Require(item.Exception is null ? observed is null && session?.Code == code :
                observed?.GetType() == item.Exception && session is null, item.Name + " preserves the original acceptance/rejection decision");
            Require(!html.Contains("connected successfully", StringComparison.OrdinalIgnoreCase),
                item.Name + " does not imply verified success before the app binds the account");
            await File.WriteAllTextAsync(Path.Combine(root, item.Name + ".html"), html, timeout.Token);
        }
        Console.WriteLine("PASS OAuth presentation: four actual loopback cases; no credentials or provider access; final verified status remains app-authoritative.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        Console.WriteLine("PASS " + message);
    }
}
