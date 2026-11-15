using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// Protocol fixtures enter the real sharing client. They do not replace identity, parsing,
// inheritance, stale-state, dispatch or readback decisions. No provider or filesystem is used.
internal static class GoogleDriveSharingChecks
{
    internal static async Task RunAsync()
    {
        await ListingAndCursorAsync();
        await SharedCorpusAsync();
        await CreateChangeRevokeAsync();
        await ExactPermissionLookupAsync();
        await DomainAndKindsAsync();
        await RefusalsAsync();
        await StaleAndIdentityAsync();
        await FailureOutcomesAsync();
        await PartialListingAsync();
        await NativeEditorLinkAsync();
        Console.WriteLine("PASS Google sharing real-client protocol checks: pagination/corpus/account identity, create/readback/roles/revoke, inherited/stale refusal, partial/uncertain outcomes without replay");
    }

    // F2/F3: the two observed native-editor metadata variants become the existing file-ID-bound Drive
    // link and still resolve their exact permission. The editor path and query stay opaque, and the
    // public link validator and every mutation gate are unchanged.
    private static async Task NativeEditorLinkAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = new GoogleDriveAccountSharingClient(http, new Tokens(), Binding()); var ns = client.Namespaces[0];
        var site = NativeFile("site-one", "application/vnd.google-apps.site", "https://sites.google.com/view/opaque-site/home?authuser=0", null);
        script.Add(HttpMethod.Get, "files", Json(new { files = new[] { site } }));
        script.Add(HttpMethod.Get, "files/site-one/permissions", Permissions(Link()));
        var page = await client.ListLinksAsync(ns);
        var link = page.Items.Single();
        Require(link.Url == "https://drive.google.com/open?id=site-one" && link.ResourceKey is null,
            "native site metadata normalizes to the file-ID-bound Drive link");
        var keyed = NativeFile("map-one", "application/vnd.google-apps.map", "https://www.google.com/maps/d/viewer?mid=opaque-map", "map-resource-key");
        script.Add(HttpMethod.Get, "files", Json(new { files = new[] { keyed } }));
        script.Add(HttpMethod.Get, "files/map-one/permissions", Permissions(Link()));
        var keyedLink = (await client.ListLinksAsync(ns)).Items.Single();
        Require(keyedLink.Url == "https://drive.google.com/open?id=map-one&resourcekey=map-resource-key" && keyedLink.ResourceKey == "map-resource-key",
            "native map metadata keeps the provider resource key on the normalized link");
        script.Add(HttpMethod.Get, "files/site-one", Json(site));
        script.Add(HttpMethod.Get, "files/site-one/permissions", Permissions(Link()));
        var selected = await AccountSharingLookup.ExactAsync(client, ns, link.Url, "anyone-link");
        Require(selected.ItemId == "site-one" && selected.Url == link.Url && selected.PermissionId == "anyone-link",
            "the normalized native editor address resolves its exact permission through the unchanged lookup");
        script.Add(HttpMethod.Get, "files", Json(new { files = new[] { NativeFile("rejected-one", "application/vnd.google-apps.site", "https://example.invalid/view/steal", null) } }));
        var refused = await client.ListLinksAsync(ns);
        Require(refused.Items.Count == 0 && refused.Issues!.Single().DetailCode == "unsupported-web-link",
            "an unapproved native editor host keeps the existing refusal and synthesizes no permission row");
        Require(script.Mutations == 0, "native editor normalization never mutates");
        script.Done();
    }

    private static Dictionary<string, object?> NativeFile(string id, string mime, string web, string? key) => new()
    {
        ["id"] = id, ["name"] = "Synthetic " + id, ["mimeType"] = mime, ["version"] = "1",
        ["webViewLink"] = web, ["resourceKey"] = key, ["driveId"] = null,
        ["parents"] = System.Array.Empty<string>(), ["trashed"] = false,
        ["capabilities"] = new { canShare = true, canDownload = true }
    };

    private static GoogleDriveConnectionBinding Binding(string identity = "account-one", bool shared = false) => new GoogleDriveConnectionBinding(
        GoogleDriveProviderConfiguration.RegisteredClientId, GoogleDriveConnectionBinding.CreateAccountKey(identity), identity,
        "fixture@example.invalid", "Synthetic account", [GoogleDriveProviderConfiguration.DriveScope],
        CredentialReference: "synthetic-only", SharedDrivesEnabled: shared).Normalize();

    private static async Task ListingAndCursorAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = new GoogleDriveAccountSharingClient(http, new Tokens(), Binding()); var ns = client.Namespaces[0];
        // An empty matching page must retain continuation. File query is documented visibility,
        // never the invalid shared=true query and never appProperties/app-created-only filtering.
        script.Add(HttpMethod.Get, "files", Json(new { files = new[] { File("file-one") }, nextPageToken = "second-page" }), inspect: (r, _) =>
        {
            var q = Query(r, "q"); Require(q == "trashed = false and visibility != 'limited'", "legitimate account-wide visibility query");
            Require(Query(r, "corpora") == "user" && Query(r, "includeItemsFromAllDrives") == "false", "My Drive corpus stays isolated");
        });
        script.Add(HttpMethod.Get, "files/file-one/permissions", Permissions(Member()));
        var first = await client.ListLinksAsync(ns);
        Require(first.Items.Count == 0 && first.Next is not null && !first.Incomplete, "zero matching links does not terminate pagination");
        using var otherHttp = new HttpClient(new Script());
        var other = new GoogleDriveAccountSharingClient(otherHttp, new Tokens(), Binding("account-two"));
        await RejectAsync<InvalidOperationException>(() => other.ListLinksAsync(other.Namespaces[0], first.Next));
        await RejectAsync<InvalidOperationException>(() => client.ListItemsAsync(ns, "root", first.Next));
        script.Add(HttpMethod.Get, "files", Json(new { files = new[] { File("file-two", key: "resource-key", includeKeyInUrl: true) } }),
            inspect: (r, _) => Require(Query(r, "pageToken") == "second-page", "file continuation preserved"));
        script.Add(HttpMethod.Get, "files/file-two/permissions", Json(new { permissions = new[] { Member() }, nextPageToken = "permissions-two" }),
            inspect: (r, _) => Require(r.Headers.GetValues("X-Goog-Drive-Resource-Keys").Single() == "file-two/resource-key", "resource key header remains bound to exact file"));
        script.Add(HttpMethod.Get, "files/file-two/permissions", Permissions(Link()),
            inspect: (r, _) => Require(Query(r, "pageToken") == "permissions-two", "permission pagination is consumed"));
        var second = await client.ListLinksAsync(ns, first.Next);
        var link = second.Items.Single();
        Require(second.Next is null && link.Url == Url("file-two", "resource-key") && link.ResourceKey == "resource-key", "provider URL and resource key preserved");
        Require(link.PermissionId == "anyone-link" && link.Provider == SyncProviders.GoogleDrive && link.CanEdit && link.CanRevoke && link.IsInherited == false, "direct manageable record bound to provider and permission");
        Require(!link.CanSetExpiry && !link.CanSetPassword && !link.CanAllowDownload && !link.CanDisallowDownload && !link.DownloadSettingKnown, "no unsupported capability promises");
        Require(link.AllowedRoles!.SequenceEqual(new[] { "reader", "commenter", "writer" }), "explicit supported role choices");
        script.Done();
    }

    private static async Task SharedCorpusAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = new GoogleDriveAccountSharingClient(http, new Tokens(), Binding(shared: true));
        script.Add(HttpMethod.Get, "drives", Json(new { drives = new[] { new { id = "drive-one" } }, nextPageToken = "more-drives" }));
        script.Add(HttpMethod.Get, "drives", Json(new { drives = new[] { new { id = "drive-two" } } }),
            inspect: (r, _) => Require(Query(r, "pageToken") == "more-drives", "shared drive discovery pagination"));
        await client.DiscoverNamespacesAsync();
        Require(client.Namespaces.SequenceEqual(new[] { "account-one", "drive-one", "drive-two" }), "only discovered enabled shared corpora admitted");
        script.Add(HttpMethod.Get, "files", Json(new { files = new[] { File("shared-file", drive: "drive-one") }, nextPageToken = "shared-next" }), inspect: (r, _) =>
        {
            Require(Query(r, "corpora") == "drive" && Query(r, "driveId") == "drive-one", "exact shared drive corpus");
            Require(Query(r, "supportsAllDrives") == "true" && Query(r, "includeItemsFromAllDrives") == "true", "shared support is explicit");
        });
        script.Add(HttpMethod.Get, "files/shared-file/permissions", Permissions(Link(inherited: true)));
        var page = await client.ListLinksAsync("drive-one");
        Require(!page.Items.Single().CanRevoke && page.Items.Single().IsInherited == true, "visible inherited links are read-only");
        var cursor = page.Next!;
        await RejectAsync<InvalidOperationException>(() => client.ListLinksAsync("drive-two", cursor));
        // Altering only the outer record cannot transplant the inner corpus binding.
        await RejectAsync<InvalidDataException>(() => client.ListLinksAsync("drive-two", cursor with { NamespaceId = "drive-two" }));
        script.Add(HttpMethod.Get, "files", Json(new { files = new[] { File("wrong-corpus", drive: "drive-two") } }));
        var bad = await client.ListLinksAsync("drive-one");
        Require(bad.Items.Count == 0 && bad.Incomplete && bad.Issues!.Count == 1, "provider corpus mismatch never becomes an actionable record");
        using var disabledHttp = new HttpClient(new Script());
        var disabled = new GoogleDriveAccountSharingClient(disabledHttp, new Tokens(), Binding());
        await disabled.DiscoverNamespacesAsync();
        Require(disabled.Namespaces.Count == 1, "discovery does not enable shared drives");
        await RejectAsync<InvalidOperationException>(() => disabled.ListLinksAsync("drive-one"));
        script.Done();
    }

    private static async Task CreateChangeRevokeAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = new GoogleDriveAccountSharingClient(http, new Tokens(), Binding()); var ns = client.Namespaces[0];
        script.Add(HttpMethod.Get, "files/file-one", Json(File("file-one")));
        var item = await client.GetItemAsync(ns, "file-one");
        Snapshot(script, "file-one", "1", Member());
        script.Add(HttpMethod.Post, "files/file-one/permissions", Json(Link()), inspect: (r, body) =>
        {
            using var json = JsonDocument.Parse(body!); var b = json.RootElement;
            Require(b.GetProperty("type").GetString() == "anyone" && b.GetProperty("role").GetString() == "reader" && !b.GetProperty("allowFileDiscovery").GetBoolean(), "reader, unlisted link is default");
            Require(!b.TryGetProperty("emailAddress", out _) && Query(r, "sendNotificationEmail") is null, "no recipient or invalid notification parameter");
        });
        Snapshot(script, "file-one", "2", Member(), Link());
        var created = await client.CreateLinkAsync(item, "public");
        Require(created.CanRevoke && created.RevisionFingerprint != item.RevisionFingerprint, "creation confirmed by fresh permission snapshot");
        Snapshot(script, "file-one", "2", Member(), Link());
        script.Add(HttpMethod.Patch, "files/file-one/permissions/anyone-link", Json(Link(role: "commenter")), inspect: (_, body) =>
        {
            using var json = JsonDocument.Parse(body!);
            Require(json.RootElement.EnumerateObject().Count() == 1 && json.RootElement.GetProperty("role").GetString() == "commenter", "role change cannot rewrite unrelated ACL fields");
        });
        Snapshot(script, "file-one", "3", Member(), Link(role: "commenter"));
        var changed = await client.ChangeLinkAsync(created, new() { Role = "commenter" });
        Require(changed.Access == "commenter" && changed.PermissionId == created.PermissionId, "exact permission role readback");
        Snapshot(script, "file-one", "3", Member(), Link(role: "commenter"));
        script.Add(HttpMethod.Delete, "files/file-one/permissions/anyone-link", "", HttpStatusCode.NoContent);
        Snapshot(script, "file-one", "4", Member());
        await client.RevokeLinkAsync(changed);
        Require(script.Mutations == 3 && script.EffectPaths.All(path => path.StartsWith("files/file-one/permissions", StringComparison.Ordinal)), "only selected file permission mutated, unrelated member preserved");
        script.Done();
    }

    private static async Task ExactPermissionLookupAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = new GoogleDriveAccountSharingClient(http, new Tokens(), Binding()); var ns = client.Namespaces[0];
        // No account-listing response exists in this fixture. An exact selection must work
        // even when that file would be absent from every bounded account inventory page.
        Snapshot(script, "file-one", "1", Member(), Link(), Link("domain-link", type: "domain", domain: "example.invalid"));
        var selected = await AccountSharingLookup.ExactAsync(client, ns, Url("file-one"), "anyone-link");
        Require(selected.PermissionId == "anyone-link" && selected.ItemId == "file-one", "exact permission resolved directly on its selected file");
        Snapshot(script, "file-one", "1", Member(), Link(), Link("domain-link", type: "domain", domain: "example.invalid"));
        await RejectAsync<InvalidOperationException>(() => AccountSharingLookup.ExactAsync(client, ns, Url("file-one"), null));
        Snapshot(script, "file-one", "1", Member(), Link());
        await RejectAsync<InvalidOperationException>(() => AccountSharingLookup.ExactAsync(client, ns, Url("file-one"), "missing-link"));
        Snapshot(script, "file-one", "1", Member(), Link());
        await RejectAsync<InvalidOperationException>(() => AccountSharingLookup.ExactAsync(client, ns, Url("file-one"), "unrelated-member"));
        await RejectAsync<InvalidOperationException>(() => AccountSharingLookup.ExactAsync(client, "foreign-namespace", Url("file-one"), "anyone-link"));
        await RejectAsync<InvalidDataException>(() => AccountSharingLookup.ExactAsync(client, ns, "https://example.invalid/file/d/file-one/view", "anyone-link"));
        Require(script.Mutations == 0, "direct lookup never mutates or chooses an ambiguous, missing, member or foreign permission");
        script.Done();
    }

    private static async Task DomainAndKindsAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = new GoogleDriveAccountSharingClient(http, new Tokens(), Binding()); var ns = client.Namespaces[0];
        var native = File("native-file", mime: "application/vnd.google-apps.document");
        script.Add(HttpMethod.Get, "files/native-file", Json(native));
        var item = await client.GetItemAsync(ns, "native-file");
        Require(item.Kind == "native_document" && item.Path == "native-file", "native document has an exact ID, not invented local path");
        script.Add(HttpMethod.Get, "files/native-file", Json(native));
        script.Add(HttpMethod.Get, "files/native-file/permissions", Permissions(Member()));
        var domain = Link("domain:example.invalid", "writer", type: "domain", domain: "example.invalid");
        script.Add(HttpMethod.Post, "files/native-file/permissions", Json(domain), inspect: (_, body) =>
        {
            using var json = JsonDocument.Parse(body!);
            Require(json.RootElement.GetProperty("domain").GetString() == "example.invalid" && json.RootElement.GetProperty("role").GetString() == "writer", "explicit domain and permitted role passed exactly");
        });
        script.Add(HttpMethod.Get, "files/native-file", Json(native));
        script.Add(HttpMethod.Get, "files/native-file/permissions", Permissions(Member(), domain));
        var created = await client.CreateLinkAsync(item, "domain:example.invalid", "writer");
        Require(created.Audience == "domain:example.invalid" && created.Domain == "example.invalid", "domain link represented truthfully");
        script.Add(HttpMethod.Get, "files/root", Json(File("root-folder", mime: GoogleDrivePilotClient.FolderMimeType)));
        script.Add(HttpMethod.Get, "files", Json(new { files = new[] { native, File("shortcut-file", mime: "application/vnd.google-apps.shortcut") } }),
            inspect: (r, _) => Require(Query(r, "q") == "trashed = false and 'root-folder' in parents", "browse selects actual root identity"));
        var browse = await client.ListItemsAsync(ns, "/");
        Require(browse.Items.Select(i => i.Kind).SequenceEqual(new[] { "native_document", "shortcut" }), "item kinds retained without resolving shortcut target");
        await RejectAsync<InvalidDataException>(() => client.GetItemAsync(ns, "/guessed/path"));
        await RejectAsync<InvalidDataException>(() => client.GetLinkAsync(ns, "https://example.invalid/file/d/file-one/view"));
        script.Done();
    }

    private static async Task RefusalsAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = new GoogleDriveAccountSharingClient(http, new Tokens(), Binding()); var ns = client.Namespaces[0];
        Snapshot(script, "file-one", "1", Member(), Link(inherited: true));
        var inherited = await client.GetLinkAsync(ns, Url("file-one"));
        Snapshot(script, "file-one", "1", Member(), Link(inherited: true));
        await RejectAsync<InvalidOperationException>(() => client.RevokeLinkAsync(inherited));
        Snapshot(script, "file-one", "1", Member(), Link(inherited: true));
        await RejectAsync<InvalidOperationException>(() => client.ChangeLinkAsync(inherited, new() { Role = "writer" }));
        var deniedFile = File("file-two", canShare: false);
        script.Add(HttpMethod.Get, "files/file-two", Json(deniedFile));
        var item = await client.GetItemAsync(ns, "file-two");
        script.Add(HttpMethod.Get, "files/file-two", Json(deniedFile));
        script.Add(HttpMethod.Get, "files/file-two/permissions", Permissions(Member()));
        await RejectAsync<InvalidOperationException>(() => client.CreateLinkAsync(item, "public"));
        await RejectAsync<NotSupportedException>(() => client.ChangeLinkAsync(inherited, new() { RequirePassword = true, Password = "synthetic-only" }));
        await RejectAsync<NotSupportedException>(() => client.ChangeLinkAsync(inherited, new() { ChangeExpiry = true }));
        await RejectAsync<NotSupportedException>(() => client.ChangeLinkAsync(inherited, new() { AllowDownload = false }));
        await RejectAsync<NotSupportedException>(() => client.ChangeLinkAsync(inherited, new() { Audience = "domain:example.invalid" }));
        await RejectAsync<InvalidDataException>(() => client.ChangeLinkAsync(inherited, new() { Role = "owner" }));
        await RejectAsync<NotSupportedException>(() => client.ListRequestsAsync(ns));
        await RejectAsync<NotSupportedException>(() => client.GetRequestAsync(ns, "request"));
        await RejectAsync<NotSupportedException>(() => client.SaveRequestAsync(ns, new("title", "/", "description", true)));
        // A URL can correspond to multiple link audience permissions; never choose one implicitly.
        Snapshot(script, "file-three", "1", Link(), Link("domain-link", type: "domain", domain: "example.invalid"));
        await RejectAsync<InvalidOperationException>(() => client.GetLinkAsync(ns, Url("file-three")));
        Require(script.Mutations == 0, "refusals emitted no mutation"); script.Done();
    }

    private static async Task StaleAndIdentityAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = new GoogleDriveAccountSharingClient(http, new Tokens(), Binding()); var ns = client.Namespaces[0];
        Snapshot(script, "file-one", "1", Member(), Link());
        var expected = await client.GetLinkAsync(ns, Url("file-one"));
        Snapshot(script, "file-one", "1", Member(), Link(role: "writer"));
        await RejectAsync<InvalidOperationException>(() => client.ChangeLinkAsync(expected, new() { Role = "commenter" }));
        script.Add(HttpMethod.Get, "files/file-one", Json(File("file-one", canShare: false)));
        script.Add(HttpMethod.Get, "files/file-one/permissions", Permissions(Member(), Link()));
        await RejectAsync<InvalidOperationException>(() => client.RevokeLinkAsync(expected));
        await RejectAsync<InvalidOperationException>(() => client.RevokeLinkAsync(expected with { ConnectionId = Binding("account-two").ConnectionId }));
        await RejectAsync<InvalidOperationException>(() => client.RevokeLinkAsync(expected with { Provider = SyncProviders.Dropbox }));
        await RejectAsync<InvalidOperationException>(() => client.RevokeLinkAsync(expected with { ItemId = "file-two" }));
        Snapshot(script, "file-two", "1", Member(), Link());
        await RejectAsync<InvalidOperationException>(() => client.RevokeLinkAsync(expected with { ItemId = "file-two", Url = Url("file-two") }));
        Require(script.Mutations == 0, "stale state, changed capabilities, account and file transplant rejected before mutation"); script.Done();
    }

    private static async Task FailureOutcomesAsync()
    {
        foreach (var failure in new[] { "rejection", "server", "transport", "malformed", "readback", "unrelated" })
        {
            using var script = new Script(); using var http = new HttpClient(script);
            var client = new GoogleDriveAccountSharingClient(http, new Tokens(), Binding()); var ns = client.Namespaces[0];
            Snapshot(script, "file-one", "1", Member(), Link());
            var expected = await client.GetLinkAsync(ns, Url("file-one"));
            Snapshot(script, "file-one", "1", Member(), Link());
            script.Add(HttpMethod.Patch, "files/file-one/permissions/anyone-link",
                failure == "malformed" ? "not-json" : Json(Link(role: "writer")),
                failure == "rejection" ? HttpStatusCode.Forbidden : failure == "server" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
                failure: failure == "transport" ? new IOException("Synthetic transport disconnect") : null);
            if (failure == "readback") script.Add(HttpMethod.Get, "files/file-one", "{}", HttpStatusCode.ServiceUnavailable);
            if (failure == "unrelated") Snapshot(script, "file-one", "2", Member("writer"), Link(role: "writer"));
            if (failure == "rejection") await RejectAsync<GoogleDriveApiException>(() => client.ChangeLinkAsync(expected, new() { Role = "writer" }));
            else await RejectAsync<GoogleDriveEffectUncertainException>(() => client.ChangeLinkAsync(expected, new() { Role = "writer" }));
            Require(script.Mutations == 1, "rejection or uncertain outcome was never replayed: " + failure); script.Done();
        }
    }

    private static async Task PartialListingAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = new GoogleDriveAccountSharingClient(http, new Tokens(), Binding());
        script.Add(HttpMethod.Get, "files", Json(new { files = new[] { File("denied-file"), File("good-file") }, nextPageToken = "next", incompleteSearch = true }));
        script.Add(HttpMethod.Get, "files/denied-file/permissions", "{}", HttpStatusCode.Forbidden);
        script.Add(HttpMethod.Get, "files/good-file/permissions", Permissions(Link()));
        var result = await client.ListLinksAsync(client.Namespaces[0]);
        Require(result.Incomplete && result.Items.Single().ItemId == "good-file" && result.Issues!.Count == 2 && result.Next is not null,
            "partial failures retain good records, exact failed identity and continuation without claiming completeness");
        script.Add(HttpMethod.Get, "files", Json(new { nextPageToken = "after-empty" }));
        var empty = await client.ListLinksAsync(client.Namespaces[0], result.Next);
        Require(empty.Items.Count == 0 && empty.Next is not null && !empty.Incomplete, "empty provider page with omitted files preserves continuation");
        script.Done();
    }

    private static Dictionary<string, object?> File(string id, string version = "1", bool canShare = true,
        string? drive = null, string? key = null, bool includeKeyInUrl = false, string mime = "application/octet-stream") => new()
    {
        ["id"] = id, ["name"] = "Synthetic " + id, ["mimeType"] = mime, ["version"] = version,
        ["webViewLink"] = Url(id, includeKeyInUrl ? key : null), ["resourceKey"] = key, ["driveId"] = drive,
        ["parents"] = System.Array.Empty<string>(), ["trashed"] = false,
        ["capabilities"] = new { canShare, canDownload = true }
    };
    private static Dictionary<string, object?> Link(string id = "anyone-link", string role = "reader", bool inherited = false,
        string type = "anyone", string? domain = null) => new()
    {
        ["id"] = id, ["type"] = type, ["role"] = role, ["domain"] = domain, ["allowFileDiscovery"] = false,
        ["permissionDetails"] = new[] { new { permissionType = "file", inherited, inheritedFrom = inherited ? "parent-folder" : null, role } }
    };
    private static Dictionary<string, object?> Member(string role = "owner") => new()
    { ["id"] = "unrelated-member", ["type"] = "user", ["role"] = role };
    private static string Permissions(params Dictionary<string, object?>[] values) => Json(new { permissions = values });
    private static string Url(string id, string? key = null) => "https://drive.google.com/file/d/" + id + "/view" + (key is null ? "" : "?resourcekey=" + key);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static void Snapshot(Script script, string id, string version, params Dictionary<string, object?>[] permissions)
    {
        script.Add(HttpMethod.Get, "files/" + id, Json(File(id, version)));
        script.Add(HttpMethod.Get, "files/" + id + "/permissions", Permissions(permissions));
    }
    private static string? Query(HttpRequestMessage request, string name)
    {
        foreach (var part in request.RequestUri!.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == name) return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
        }
        return null;
    }
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Google sharing checks: " + message); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Google sharing checks: expected " + typeof(T).Name);
    }
    private sealed class Tokens : IGoogleDriveAccessTokenSource
    { public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("synthetic-not-a-credential"); }
    private sealed class Script : HttpMessageHandler
    {
        private readonly Queue<Step> _steps = new();
        internal int Mutations { get; private set; }
        internal List<string> EffectPaths { get; } = [];
        internal void Add(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK,
            Action<HttpRequestMessage, string?>? inspect = null, Exception? failure = null) => _steps.Enqueue(new(method, path, json, status, inspect, failure));
        internal void Done() => Require(_steps.Count == 0, "all recorded protocol responses consumed exactly once");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_steps.TryDequeue(out var step)) throw new InvalidOperationException("Google sharing checks: unexpected extra HTTP request or replay");
            Require(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "www.googleapis.com", "fixed Google API origin");
            var path = request.RequestUri.AbsolutePath["/drive/v3/".Length..];
            Require(request.Method == step.Method && path == step.Path, "expected exact HTTP method and file/permission route");
            Require(request.Headers.Authorization?.Scheme == "Bearer", "protected token callback feeds authorization header");
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method != HttpMethod.Get) { Mutations++; EffectPaths.Add(path); }
            step.Inspect?.Invoke(request, body);
            if (step.Failure is not null) throw step.Failure;
            return new HttpResponseMessage(step.Status) { Content = new StringContent(step.Json, Encoding.UTF8, "application/json") };
        }
        private sealed record Step(HttpMethod Method, string Path, string Json, HttpStatusCode Status,
            Action<HttpRequestMessage, string?>? Inspect, Exception? Failure);
    }
}
