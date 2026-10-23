using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// Exercise the real provider adapter, not a replacement metadata decision. The
// wire supplies only synthetic responses and refuses every unlisted endpoint.
internal static class RemoteEntryLookupChecks
{
    internal static async Task RunAsync()
    {
        using var wire = new MetadataWire();
        using var http = new HttpClient(wire);
        ISyncRemoteStore remote = new DropboxPilotClient(http, new Token(), "/Pilot",
            new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id",
                "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes), "id:pilot-folder");

        var first = await remote.ReadEntryAsync("file.txt", "id:file");
        Require(first.Id == "id:file" && first.RelativePath == "file.txt" && first.Revision == "rev-1" &&
                wire.FileReads == 1 && wire.ListReads == 0,
            "exact Dropbox metadata lookup never enumerates the remote tree");
        wire.Revision = "rev-2";
        Require((await remote.ReadEntryAsync("file.txt", "id:file")).Revision == "rev-2",
            "each lookup returns fresh provider metadata, not the reviewed inventory");
        foreach (var mismatch in new[] { "path", "identity", "outside-root" })
        {
            wire.Mismatch = mismatch;
            var refused = false;
            try { await remote.ReadEntryAsync("file.txt", "id:file"); }
            catch (Exception error) when (error is IOException or InvalidDataException) { refused = true; }
            Require(refused, "exact metadata rejects " + mismatch + " substitution");
        }
        wire.Mismatch = null;
        wire.FailReads = 1;
        var before = wire.FileReads;
        Require((await remote.ReadEntryAsync("file.txt", "id:file")).Revision == "rev-2" &&
                wire.FileReads == before + 2,
            "a transient HTTP 500 is retried only as an idempotent metadata read");
        wire.FailReads = 10;
        before = wire.FileReads;
        var bounded = false;
        try { await remote.ReadEntryAsync("file.txt", "id:file"); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.InternalServerError)
        { bounded = true; }
        Require(bounded && wire.FileReads == before + 3 && wire.ListReads == 0,
            "persistent HTTP 500 ends after three read attempts without any mutation or whole-tree fallback");

        ISyncRemoteStore fallback = new FakeSyncRemoteStore();
        ((FakeSyncRemoteStore)fallback).Put("file.txt", "payload");
        Require((await fallback.ReadEntryAsync("file.txt", "id:file.txt")).RelativePath == "file.txt",
            "legacy adapters retain their shared safe exact-item fallback");
        var differentIdentity = false;
        try { await fallback.ReadEntryAsync("file.txt", "id:other"); }
        catch (IOException) { differentIdentity = true; }
        Require(differentIdentity, "the shared fallback rejects a replaced item at the same path");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Remote metadata check failed: " + message);
        Console.WriteLine("PASS " + message);
    }

    private sealed class Token : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("synthetic-metadata-token");
    }

    private sealed class MetadataWire : HttpMessageHandler
    {
        internal int FileReads, ListReads, FailReads;
        internal string Revision = "rev-1";
        internal string? Mismatch;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post) throw new InvalidOperationException("Unexpected method.");
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/2/users/get_current_account")
                return Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""");
            if (route.Contains("list_folder", StringComparison.Ordinal))
            {
                ListReads++;
                throw new InvalidOperationException("An exact item lookup must not enumerate a tree.");
            }
            if (route != "/2/files/get_metadata")
                throw new InvalidOperationException("Unexpected provider route: " + route);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var path = payload.RootElement.GetProperty("path").GetString();
            if (path == "id:pilot-folder")
                return Json("""{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_lower":"/pilot","path_display":"/Pilot"}""");
            if (path != "id:file") throw new InvalidOperationException("Unexpected metadata identity.");
            FileReads++;
            if (FailReads-- > 0)
            {
                var failed = new HttpResponseMessage(HttpStatusCode.InternalServerError);
                failed.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return failed;
            }
            var display = Mismatch switch
            { "path" => "/Pilot/other.txt", "outside-root" => "/Elsewhere/file.txt", _ => "/Pilot/file.txt" };
            return Json(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [".tag"] = "file", ["name"] = "file.txt", ["id"] = Mismatch == "identity" ? "id:other" : "id:file",
                ["path_display"] = display, ["path_lower"] = display.ToLowerInvariant(),
                ["rev"] = Revision, ["size"] = 7, ["content_hash"] = new string('a', 64)
            }));
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
