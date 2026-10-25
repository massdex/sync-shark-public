using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

internal static class SharingChecks
{
    internal static async Task RunAsync()
    {
        var transport = new SharingTransport(); using var http = new HttpClient(transport);
        var connection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:sharing-check", "sharing-root", "sharing-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        var client = new DropboxPilotClient(http, new Token(), "/Sharing", connection, "id:sharing-root");
        var item = await client.GetSharingItemAsync("sample.txt");
        Require(item.ItemId == transport.ItemId && item.Links.Count == 0 && transport.Effects == 0,
            "Opening sharing reads exact identity without creating public access");
        var link = await client.CreateSharedLinkAsync(item.RelativePath, item.ItemId, DropboxLinkAudience.AnyoneWithLink);
        Require(link.CanRevoke && link.Visibility == "public" && transport.Effects == 1,
            "Explicit create sends one viewer-link request and reads its permissions");
        await Reject(() => client.CreateSharedLinkAsync(item.RelativePath, item.ItemId, DropboxLinkAudience.AnyoneWithLink));
        Require(transport.Effects == 1, "A duplicate create never repeats the effect");
        link = await client.ChangeSharedLinkAudienceAsync(item.RelativePath, item.ItemId, link.Url, DropboxLinkAudience.TeamOnly);
        Require(link.Visibility == "team_only" && transport.PreservedAccess,
            "Changing audience does not silently change access or expiry");
        transport.ItemId = "id:replacement";
        var effects = transport.Effects;
        await Reject(() => client.RevokeSharedLinkAsync(item.RelativePath, item.ItemId, link.Url));
        Require(transport.Effects == effects, "Replaced item is rejected before changing its access");
        transport.ItemId = item.ItemId; transport.ForeignLink = true;
        await Reject(() => client.GetSharingItemAsync(item.RelativePath));
        Require(transport.Effects == effects, "Parent/foreign item link is not accepted as this item's link");
        transport.ForeignLink = false; transport.Incomplete = true;
        await Reject(() => client.GetSharingItemAsync(item.RelativePath));
        transport.Incomplete = false;
        await client.RevokeSharedLinkAsync(item.RelativePath, item.ItemId, link.Url);
        Require(!transport.HasLink, "Revoke confirms absence with a fresh direct-link read");
        transport.FailAfterCreate = true;
        await Reject(() => client.CreateSharedLinkAsync(item.RelativePath, item.ItemId, DropboxLinkAudience.AnyoneWithLink));
        Require(transport.Effects == effects + 2 && transport.HasLink,
            "Lost creation response remains uncertain and is never automatically retried");
        transport.WrongAccount = true; effects = transport.Effects;
        await Reject(() => client.GetSharingItemAsync(item.RelativePath));
        Require(transport.Effects == effects, "Wrong provider account stops before item sharing requests");
        Console.WriteLine("PASS Production shared-link success, rejection and uncertain-response paths");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or HttpRequestException) { return; }
        throw new InvalidOperationException("Sharing request should have been rejected.");
    }
    private sealed class Token : IDropboxAccessTokenSource
    { public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("fixture-sharing-access"); }
    private sealed class SharingTransport : HttpMessageHandler
    {
        internal string ItemId = "id:sharing-item";
        internal bool HasLink, ForeignLink, Incomplete, FailAfterCreate, WrongAccount;
        internal bool PreservedAccess;
        internal int Effects;
        private string _visibility = "public";
        private const string Url = "https://www.dropbox.com/scl/fi/fixture/sample.txt?dl=0";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var endpoint = request.RequestUri!.AbsolutePath;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            object? response;
            if (endpoint.EndsWith("users/get_current_account"))
                response = new { account_id = WrongAccount ? "dbid:other" : "dbid:sharing-check", root_info = new { root_namespace_id = "sharing-root", home_namespace_id = "sharing-home" } };
            else if (endpoint.EndsWith("files/get_metadata"))
            {
                var path = body.RootElement.GetProperty("path").GetString();
                var root = path == "id:sharing-root";
                response = new Dictionary<string, object> { [".tag"] = root ? "folder" : "file", ["id"] = root ? "id:sharing-root" : ItemId,
                    ["name"] = root ? "Sharing" : "sample.txt", ["path_display"] = root ? "/Sharing" : "/Sharing/sample.txt" };
            }
            else if (endpoint.EndsWith("sharing/list_shared_links"))
            {
                Require(body.RootElement.GetProperty("direct_only").GetBoolean(), "Link read is restricted to the selected item");
                response = new { links = HasLink ? new[] { Link() } : Array.Empty<object>(), has_more = Incomplete };
            }
            else if (endpoint.EndsWith("sharing/create_shared_link_with_settings"))
            {
                Effects++; HasLink = true;
                Require(body.RootElement.GetProperty("path").GetString() == ItemId &&
                    body.RootElement.GetProperty("settings").GetProperty("access").GetString() == "viewer", "Create is identity-bound and viewer-only");
                if (FailAfterCreate) throw new HttpRequestException("Controlled response loss after the provider effect.");
                response = Link();
            }
            else if (endpoint.EndsWith("sharing/modify_shared_link_settings"))
            {
                Effects++; var settings = body.RootElement.GetProperty("settings");
                _visibility = settings.GetProperty("requested_visibility").GetString()!;
                PreservedAccess = !settings.TryGetProperty("access", out _) && !settings.TryGetProperty("expires", out _);
                response = Link();
            }
            else if (endpoint.EndsWith("sharing/revoke_shared_link")) { Effects++; HasLink = false; response = null; }
            else throw new InvalidOperationException("Unexpected sharing endpoint.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
        private object Link() => new { id = ForeignLink ? "id:parent" : ItemId, url = Url,
            link_permissions = new { resolved_visibility = new Dictionary<string, string> { [".tag"] = _visibility },
                link_access_level = new Dictionary<string, string> { [".tag"] = "viewer" }, can_revoke = true } };
    }
}
