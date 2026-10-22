using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

internal static class AccountSharingChecks
{
    internal static async Task RunAsync()
    {
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "dbid:fixture",
            "root-fixture", "home-fixture", "full_dropbox", DropboxProviderConfiguration.RequestedScopes(true)).Normalize();
        using var transport = new Transport(); using var http = new HttpClient(transport);
        var client = new DropboxAccountSharingClient(http, new Tokens(), binding);
        var first = await client.ListLinksAsync("home-fixture");
        Check(first.Items.Count == 1 && first.Next is not null && first.Items[0].Path == "/outside-pairs/readme.txt", "Full account links include items outside pairs");
        Check(!transport.LastBody.Contains("\"path\"", StringComparison.Ordinal) && transport.LastNamespace == "home-fixture", "Account list is unbounded by pair path and explicitly namespace-bound");
        var second = await client.ListLinksAsync("home-fixture", first.Next);
        Check(second.Next is null && transport.LastBody.Contains("fixture-cursor", StringComparison.Ordinal), "Production account pagination consumes the returned cursor");
        var calls = transport.Calls;
        await Reject(() => client.ListLinksAsync("root-fixture", first.Next), "Namespace-switched cursor is rejected");
        await Reject(() => client.ListLinksAsync("home-fixture", first.Next! with { ConnectionId = "wrong-account" }), "Account-switched cursor is rejected");
        Check(transport.Calls == calls, "Rejected cursors send no provider request");
        var link = first.Items.Single();
        var item = await client.GetItemAsync("home-fixture", "/outside-pairs/readme.txt");
        var created = await client.CreateLinkAsync(item, "public");
        Check(created.ItemId == item.Id && transport.LastBody.Contains("\"path\":\"id:fixture\"", StringComparison.Ordinal), "Link creation uses the rechecked stable item identity");
        await Reject(() => client.CreateLinkAsync(item with { ConnectionId = "wrong-account" }, "public"), "A stale cross-account item cannot create a link");
        transport.Moved = true;
        var mutations = transport.Effects;
        await Reject(() => client.CreateLinkAsync(item, "public"), "Moved item requires reselection before link creation");
        Check(transport.Effects == mutations, "Moved-item rejection happens before the effect"); transport.Moved = false;
        await client.ChangeLinkAsync(link, new SharedLinkChange { Audience = "no_one", ChangeExpiry = true, Expires = DateTimeOffset.UtcNow.AddDays(1) });
        using (var sent = JsonDocument.Parse(transport.LastBody))
            Check(sent.RootElement.GetProperty("settings").GetProperty("audience").GetString() == "no_one" &&
                sent.RootElement.GetProperty("settings").GetProperty("expires").GetString()!.EndsWith('Z'), "Link settings use provider audience semantics and explicit UTC expiry");
        await client.ChangeLinkAsync(link, new SharedLinkChange { ChangeExpiry = true });
        Check(transport.LastBody.Contains("\"remove_expiration\":true", StringComparison.Ordinal), "Expiry removal uses the provider's explicit remove flag");
        transport.Restricted = true;
        mutations = transport.Effects;
        await Reject(() => client.ChangeLinkAsync(link, new SharedLinkChange { RequirePassword = true, Password = "fixture-only-password" }), "Unsupported password changes are rejected from fresh permissions");
        await Reject(() => client.RevokeLinkAsync(link), "Revocation obeys fresh provider permissions");
        Check(transport.Effects == mutations, "Restricted link settings and revocation cause no effect"); transport.Restricted = false;
        var requests = await client.ListRequestsAsync("home-fixture");
        Check(requests.Items[0].FileCount == 9 && requests.Next is not null, "File request count and pagination are retained");
        await client.ListRequestsAsync("home-fixture", requests.Next);
        Check(transport.LastRoute == "file_requests/list/continue", "File requests use the correct continuation endpoint");
        var request = requests.Items[0];
        var changed = new FileRequestChange(request.Title, request.Destination!, request.Description, false, true);
        await client.SaveRequestAsync("home-fixture", changed, request);
        using (var sent = JsonDocument.Parse(transport.LastBody))
            Check(sent.RootElement.GetProperty("deadline").GetProperty(".tag").GetString() == "update" &&
                sent.RootElement.GetProperty("deadline").GetProperty("update").ValueKind == JsonValueKind.Null,
                "Clearing a file request deadline uses the explicit nullable update union");
        transport.RequestTitle = "Changed elsewhere"; mutations = transport.Effects;
        await Reject(() => client.SaveRequestAsync("home-fixture", changed, request), "Concurrent request settings are not overwritten");
        Check(transport.Effects == mutations, "Concurrent-edit rejection precedes mutation"); transport.RequestTitle = "Fixture Request";
        var closed = await client.SetRequestOpenAsync(request, false);
        Check(!closed.IsOpen && !transport.LastBody.Contains("destination", StringComparison.Ordinal), "Close request changes only upload availability and preserves destination/files");
        var legacy = new DropboxAccountSharingClient(http, new Tokens(), binding with { Scopes = DropboxProviderConfiguration.RequiredScopes });
        calls = transport.Calls;
        await Reject(() => legacy.ListRequestsAsync("home-fixture"), "Legacy grant needs explicit file-request consent");
        Check(transport.Calls == calls, "Missing permission is not retried against the provider");
        await legacy.ListLinksAsync("home-fixture");
        Check(true, "Legacy grant still supports existing shared-link functionality");
        transport.Uncertain = true; mutations = transport.Effects;
        try { await client.CreateLinkAsync(item, "public"); throw new InvalidOperationException("Expected uncertain effect"); }
        catch (DropboxEffectUncertainException) { Check(transport.Effects == mutations + 1, "Uncertain write is sent at most once with no automatic replay"); }
        transport.Uncertain = false; transport.MalformedEffect = true;
        try { await client.CreateLinkAsync(item, "public"); throw new InvalidOperationException("Expected malformed effect uncertainty"); }
        catch (DropboxEffectUncertainException) { Check(true, "Malformed successful acknowledgement remains an uncertain effect"); }
        await Reject(() => Task.Run(() => DropboxAccountSharingClient.ValidateUrl("https://example.invalid/steal")), "Untrusted link origins never become launch actions");
        await GoogleSharingClassificationChecks.RunAsync();
        await GoogleSharingCauseFixChecks.RunAsync();
        Console.WriteLine("Account sharing checks passed.");
    }
    private static void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); Console.WriteLine("PASS " + description); }
    private static async Task Reject(Func<Task> action, string description)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or ArgumentException) { Console.WriteLine("PASS " + description); return; }
        throw new InvalidOperationException("Expected rejection: " + description);
    }
    private sealed class Tokens : IDropboxAccessTokenSource
    { public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("fixture-token-no-live-authority"); }
    private sealed class Transport : HttpMessageHandler
    {
        public string LastBody = "", LastNamespace = "", LastRoute = "", RequestTitle = "Fixture Request";
        public int Calls, Effects; public bool Moved, Restricted, Uncertain, MalformedEffect;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            Calls++; LastRoute = message.RequestUri!.AbsolutePath[3..]; LastBody = await message.Content!.ReadAsStringAsync(token);
            using var ns = JsonDocument.Parse(message.Headers.GetValues("Dropbox-API-Path-Root").Single()); LastNamespace = ns.RootElement.GetProperty("namespace_id").GetString()!;
            if (message.RequestUri.Host != "api.dropboxapi.com" || message.Headers.Authorization?.Parameter != "fixture-token-no-live-authority") throw new InvalidOperationException("Wrong transport identity");
            using var input = JsonDocument.Parse(LastBody); var body = input.RootElement;
            var effect = LastRoute is "sharing/create_shared_link_with_settings" or "sharing/modify_shared_link_settings" or "sharing/revoke_shared_link" or "file_requests/create" or "file_requests/update";
            if (effect) { Effects++; if (Uncertain) return new(HttpStatusCode.ServiceUnavailable); if (MalformedEffect) return Json(new { malformed = true }); }
            return LastRoute switch
            {
                "sharing/list_shared_links" => Json(new { links = new[] { Link() }, has_more = !body.TryGetProperty("cursor", out _), cursor = "fixture-cursor" }),
                "sharing/get_shared_link_metadata" or "sharing/create_shared_link_with_settings" or "sharing/modify_shared_link_settings" => Json(Link()),
                "sharing/revoke_shared_link" => Json<object?>(null),
                "files/get_metadata" => Json(new Dictionary<string, object> { [".tag"] = body.GetProperty("path").GetString() == "/uploads" ? "folder" : "file",
                    ["id"] = "id:fixture", ["name"] = "readme.txt", ["path_display"] = Moved ? "/moved.txt" : "/outside-pairs/readme.txt" }),
                "file_requests/list_v2" => Json(new { file_requests = new[] { Request() }, has_more = true, cursor = "requests-cursor" }),
                "file_requests/list/continue" => Json(new { file_requests = new[] { Request() }, has_more = false }),
                "file_requests/get" or "file_requests/create" => Json(Request()),
                "file_requests/update" => Json(Request(body.TryGetProperty("open", out var open) ? open.GetBoolean() : true)),
                _ => throw new InvalidOperationException("Unexpected route: " + LastRoute)
            };
        }
        private object Link() => new Dictionary<string, object>
        {
            [".tag"] = "file", ["url"] = "https://www.dropbox.com/scl/fi/fixture/readme.txt?rlkey=fixture", ["id"] = "id:fixture", ["name"] = "readme.txt",
            ["path_lower"] = "/outside-pairs/readme.txt", ["link_permissions"] = new
            {
                effective_audience = new Dictionary<string, string> { [".tag"] = "public" }, link_access_level = new Dictionary<string, string> { [".tag"] = "viewer" },
                can_revoke = !Restricted, can_set_expiry = !Restricted, can_remove_expiry = !Restricted, can_set_password = !Restricted, can_remove_password = !Restricted,
                allow_download = true, can_allow_download = !Restricted, can_disallow_download = !Restricted,
                audience_options = new[] { new { audience = new Dictionary<string, string> { [".tag"] = "public" }, allowed = true },
                    new { audience = new Dictionary<string, string> { [".tag"] = "no_one" }, allowed = !Restricted } }
            }
        };
        private object Request(bool open = true) => new { id = "fixture123", url = "https://www.dropbox.com/request/fixture123", title = RequestTitle,
            destination = "/uploads", description = "Fixture description", created = "2026-01-01T12:00:00Z", is_open = open, file_count = 9 };
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
