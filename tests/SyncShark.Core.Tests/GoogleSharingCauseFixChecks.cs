using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// F7: proves the root460 cause correction through the REAL GoogleDriveAccountSharingClient HTTP entry,
// the real CLI projection record and the actual shared summary the WPF sharing page renders. Every
// provider reply is injected; no credential, live endpoint, write verb or real Site/Map is touched.
internal static class GoogleSharingCauseFixChecks
{
    private const string NamespaceId = "fixture-permission-id";
    private const string SiteId = "site-fixture";
    private const string MapId = "map-fixture";
    private const string KeyedSiteId = "keyed-site-fixture";
    private const string SiteMime = "application/vnd.google-apps.site";
    private const string MapMime = "application/vnd.google-apps.map";
    private const string SiteLink = "https://sites.google.com/view/opaque-site/home?authuser=0";
    private const string MapLink = "https://www.google.com/maps/d/viewer?mid=opaque-map";
    private const string SiteDriveLink = "https://drive.google.com/open?id=" + SiteId;
    private const string Instruction = "Open these items in Google Drive, or ask their owner to check sharing.";

    internal static async Task RunAsync()
    {
        await NativeEditorNormalizationAsync();
        await NativeEditorRefusalsAsync();
        await ProviderReasonAsync();
        await ReasonAmbiguityAsync();
        await EffectUncertaintyAsync();
        SharedSummary();
        CliProjection();
        Console.WriteLine("Google sharing cause-fix checks passed.");
    }

    // F2/F3: the two observed native-editor metadata variants become the existing file-ID-bound Drive
    // link, keep their provider resource key, resolve their exact permission through the unchanged
    // lookup, and leave every already-supported Drive and Docs address untouched.
    private static async Task NativeEditorNormalizationAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = Client(http); var ns = client.Namespaces[0];
        script.Add(HttpMethod.Get, "/drive/v3/files", JsonBody(new
        {
            nextPageToken = "cause-fix-page-two",
            files = new object[]
            {
                File(SiteId, SiteMime, SiteLink),
                File(MapId, MapMime, MapLink),
                File(KeyedSiteId, SiteMime, "https://sites.google.com/view/keyed-site", key: "site-resource-key")
            }
        }));
        script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId + "/permissions", Permissions(Link()));
        script.Add(HttpMethod.Get, "/drive/v3/files/" + MapId + "/permissions", Permissions(Link()));
        script.Add(HttpMethod.Get, "/drive/v3/files/" + KeyedSiteId + "/permissions", Permissions(Link()));
        var first = await client.ListLinksAsync(ns);
        Require(first.Items.Count == 3 && (first.Issues?.Count ?? 0) == 0, "both native editor variants normalize instead of being rejected");
        var site = first.Items.Single(item => item.ItemId == SiteId);
        var map = first.Items.Single(item => item.ItemId == MapId);
        var keyed = first.Items.Single(item => item.ItemId == KeyedSiteId);
        Require(site.Url == SiteDriveLink, "the site variant normalizes to the file-ID-bound Drive link");
        Require(map.Url == "https://drive.google.com/open?id=" + MapId, "the map variant normalizes to the file-ID-bound Drive link");
        Require(site.ResourceKey is null && map.ResourceKey is null, "no resource key is invented for a keyless native editor item");
        Require(keyed.Url == "https://drive.google.com/open?id=" + KeyedSiteId + "&resourcekey=site-resource-key" && keyed.ResourceKey == "site-resource-key",
            "the provider metadata resource key is preserved on the normalized address");
        Require(first.Next is not null && !first.Incomplete, "normalization keeps the provider cursor");
        AccountSharingLookup.ValidateUrl(site.Url);
        script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId, JsonBody(File(SiteId, SiteMime, SiteLink)));
        script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId + "/permissions", Permissions(Link()));
        var selected = await AccountSharingLookup.ExactAsync(client, ns, site.Url, "anyone-link");
        Require(selected.ItemId == SiteId && selected.PermissionId == "anyone-link" && selected.Url == site.Url,
            "the normalized address passes the unchanged validator and resolves its exact permission");
        script.Add(HttpMethod.Get, "/drive/v3/files", JsonBody(new
        {
            files = new object[]
            {
                File("doc-fixture", "application/vnd.google-apps.document", "https://docs.google.com/document/d/doc-fixture/edit"),
                File("plain-fixture", "application/octet-stream", "https://drive.google.com/file/d/plain-fixture/view")
            }
        }));
        script.Add(HttpMethod.Get, "/drive/v3/files/doc-fixture/permissions", Permissions(Link()));
        script.Add(HttpMethod.Get, "/drive/v3/files/plain-fixture/permissions", Permissions(Link()));
        var second = await client.ListLinksAsync(ns, first.Next);
        Require(second.Items.Any(item => item.ItemId == "doc-fixture" && item.Url == "https://docs.google.com/document/d/doc-fixture/edit") &&
            second.Items.Any(item => item.ItemId == "plain-fixture" && item.Url == "https://drive.google.com/file/d/plain-fixture/view"),
            "existing supported Drive and Docs addresses are unchanged");
        Require(script.Mutations == 0 && script.Methods.TrueForAll(method => method == "GET"), "the whole normalization path issues bounded reads only");
        script.Done();
    }

    // F2: only the exact host/MIME pair is rewritten. Anything else keeps the existing refusal, and a
    // refused item must not spend a further provider request or synthesize a permission row.
    private static async Task NativeEditorRefusalsAsync()
    {
        // Every one of these is refused while the item metadata is read, so the refusal must cost
        // nothing further: no permission request, no other provider call and no write verb.
        foreach (var (mime, url, detail, description) in new (string, string, string, string)[]
        {
            (SiteMime, "https://example.invalid/view/steal", "unsupported-web-link", "an unapproved host"),
            (MapMime, "https://sites.google.com/view/spoof", "unsupported-web-link", "a host that does not match its MIME type"),
            (SiteMime, "http://sites.google.com/view/insecure", "unsupported-web-link", "an insecure address"),
            (SiteMime, "https://user:credential@sites.google.com/view/credentialed", "unsupported-web-link", "embedded user information"),
            (SiteMime, "https://sites.google.com:8443/view/ported", "unsupported-web-link", "a non-default port"),
            (SiteMime, "https://sites.google.com/view/fragmented#section", "unsupported-web-link", "a fragment"),
            ("application/vnd.google-apps.document", "https://sites.google.com/view/wrong-mime", "unsupported-web-link", "a MIME type without this host mapping"),
            (SiteMime, "https://drive.google.com/open?id=another-item", "item-web-link-mismatch", "a web link that identifies another item"),
            (SiteMime, "https://sites.google.com/view/" + new string('x', 8200), "unsupported-web-link", "an oversized address")
        })
        {
            // The corpus fetch is deliberately outside the per-item catch, so a transport fault is
            // scoped to the per-item request that the classification path actually covers.
            var issue = await RefusedItemAsync(File(SiteId, mime, url), queuePermissions: false);
            Require(issue.Code == "invalid-response" && issue.DetailCode == detail && issue.Stage == "file-metadata",
                "a native editor address with " + description + " keeps the established refusal detail " + detail);
        }
        // A missing web link is refused at link projection, after the item was read: still no write verb.
        var missing = await RefusedItemAsync(File(SiteId, SiteMime, null), queuePermissions: true);
        Require(missing.Code == "invalid-response" && missing.DetailCode == "unsupported-web-link" && missing.Stage == "link-projection",
            "a missing web link keeps the existing link-projection refusal");
    }

    // One item, one corpus page. queuePermissions is true only for a variant that is read far enough
    // to ask for the permission collection. The returned script state is asserted by the caller.
    private static async Task<AccountSharingIssue> RefusedItemAsync(Dictionary<string, object?> entry, bool queuePermissions)
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = Client(http); var ns = client.Namespaces[0];
        script.Add(HttpMethod.Get, "/drive/v3/files", JsonBody(new { files = new object[] { entry } }));
        if (queuePermissions) script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId + "/permissions", Permissions(Link()));
        var page = await client.ListLinksAsync(ns);
        var issue = page.Issues?.Single();
        Require(page.Items.Count == 0 && issue is not null && issue.ItemId == SiteId,
            "a refused item yields no link and one identified issue");
        Require(script.Unexpected == 0 && script.Mutations == 0,
            "a refused address issues no further provider request and no write verb");
        script.Done();
        return issue!;
    }

    // F4/F5: every allowlisted finite reason is classified from the bounded body alone, and no
    // missing, malformed, oversized, unrecognised or ambiguous body can invent a cause or leak text.
    private static async Task ProviderReasonAsync()
    {
        foreach (var (reason, code, detail) in new (string, string, string)[]
        {
            ("insufficientFilePermissions", "access-restricted", "insufficientFilePermissions"),
            ("appNotAuthorizedToFile", "access-restricted", "appNotAuthorizedToFile"),
            ("domainPolicy", "access-restricted", "domainPolicy"),
            ("forbidden", "access-restricted", "forbidden"),
            ("insufficientPermissions", "access-restricted", "insufficientPermissions"),
            ("ACCESS_TOKEN_SCOPE_INSUFFICIENT", "access-restricted", "ACCESS_TOKEN_SCOPE_INSUFFICIENT"),
            ("rateLimitExceeded", "rate-limited", "rateLimitExceeded"),
            ("userRateLimitExceeded", "rate-limited", "userRateLimitExceeded"),
            ("dailyLimitExceeded", "rate-limited", "dailyLimitExceeded"),
            ("sharingRateLimitExceeded", "rate-limited", "sharingRateLimitExceeded")
        })
        {
            var issue = await DeniedIssueAsync(ReasonBody(reason, "SENTINEL-PROVIDER-MESSAGE"));
            Require(issue.Code == code && issue.DetailCode == detail && issue.Stage == "permissions",
                "403 " + reason + " classifies as " + code + " with its finite detail");
            Require(!JsonSerializer.Serialize(issue).Contains("SENTINEL", StringComparison.Ordinal), "no provider text reaches the classified issue for " + reason);
        }
        foreach (var (body, description) in new (byte[]?, string)[]
        {
            (Encoding.UTF8.GetBytes("{not-json"), "malformed"),
            ([], "empty"),
            (JsonBody(new { error = new { message = "SENTINEL-PROVIDER-MESSAGE" } }), "message-only"),
            (JsonBody(new { error = new { errors = new[] { new { reason = "brandNewProviderReason" } } } }), "unrecognised"),
            (JsonBody(new { error = new { errors = new[] { new { reason = "insufficientFilePermissions" } }, details = new[] { new { reason = "rateLimitExceeded" } } } }), "ambiguous"),
            (OversizeReasonBody(), "oversized")
        })
        {
            var issue = await DeniedIssueAsync(body);
            Require(issue.Code == "google-request-failed" && issue.DetailCode is null,
                "a " + description + " 403 body stays an unclassified Google request failure");
            Require(!JsonSerializer.Serialize(issue).Contains("SENTINEL", StringComparison.Ordinal), "no raw provider text is exposed for a " + description + " body");
        }
        // A finite reason explains a 403; it never rewrites another status family.
        var missing = await DeniedIssueAsync(ReasonBody("insufficientFilePermissions", "ignored"), HttpStatusCode.NotFound);
        Require(missing.Code == "no-longer-accessible" && missing.DetailCode is null, "a finite reason does not change the 404 family");
    }

    // G3: through the real adapter HTTP entry, a cause is claimed only when every reason element in the
    // bounded non-success body is one of the recognized finite values. Anything unreadable, non-string
    // or unrecognized anywhere makes the whole body ambiguous and the failure stays an unclassified
    // Google request failure, never a definite statement about why Google refused.
    private static async Task ReasonAmbiguityAsync()
    {
        const string known = "insufficientFilePermissions";
        const string unknown = "SENTINEL-UNKNOWN-REASON";
        foreach (var (label, json, code, detail) in new (string, string, string, string?)[]
        {
            ("an unrecognized errors reason before a known errors reason",
                Body("[{\"reason\":\"" + unknown + "\"},{\"reason\":\"" + known + "\"}]"), "google-request-failed", null),
            ("a known errors reason before an unrecognized errors reason",
                Body("[{\"reason\":\"" + known + "\"},{\"reason\":\"" + unknown + "\"}]"), "google-request-failed", null),
            ("a known errors reason beside an unrecognized details reason",
                Body("[{\"reason\":\"" + known + "\"}]", "[{\"reason\":\"" + unknown + "\"}]"), "google-request-failed", null),
            ("an unrecognized errors reason beside a known details reason",
                Body("[{\"reason\":\"" + unknown + "\"}]", "[{\"reason\":\"" + known + "\"}]"), "google-request-failed", null),
            ("a non-string errors reason beside a known errors reason",
                Body("[{\"reason\":7},{\"reason\":\"" + known + "\"}]"), "google-request-failed", null),
            ("a malformed errors element beside a known errors reason",
                Body("[{\"reason\":\"" + known + "\"},\"unreadable\"]"), "google-request-failed", null),
            ("a malformed details element beside a known errors reason",
                Body("[{\"reason\":\"" + known + "\"}]", "[\"unreadable\"]"), "google-request-failed", null),
            ("an errors element without a reason beside a known details reason",
                Body("[{\"message\":\"no reason element\"}]", "[{\"reason\":\"" + known + "\"}]"), "google-request-failed", null),
            ("only unrecognized reasons across both arrays",
                Body("[{\"reason\":\"" + unknown + "\"}]", "[{\"reason\":\"OTHER-SENTINEL\"}]"), "google-request-failed", null),
            ("two different recognized reasons",
                Body("[{\"reason\":\"" + known + "\"}]", "[{\"reason\":\"rateLimitExceeded\"}]"), "google-request-failed", null),
            ("recognized identical duplicate errors reasons",
                Body("[{\"reason\":\"" + known + "\"},{\"reason\":\"" + known + "\"}]"), "access-restricted", known),
            ("a typed details object without a reason beside a known errors reason",
                Body("[{\"reason\":\"" + known + "\"}]", "[{\"@type\":\"type.googleapis.com/google.rpc.Help\",\"links\":[]}]"), "access-restricted", known),
            ("a recognized details reason with an absent errors array",
                "{\"error\":{\"details\":[{\"reason\":\"" + known + "\"}]}}", "access-restricted", known)
        })
            await AmbiguityPageAsync(label, RawBody(json), code, detail);
    }

    // G3: the same page carries one denied item and one inspectable item, so the successful record, the
    // provider cursor and the incomplete flag are all observable next to the classified failure.
    private static async Task AmbiguityPageAsync(string label, byte[] deniedBody, string expectedCode, string? expectedDetail)
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = Client(http); var ns = client.Namespaces[0];
        script.Add(HttpMethod.Get, "/drive/v3/files", JsonBody(new
        {
            nextPageToken = "ambiguity-next",
            files = new object[]
            {
                File(SiteId, SiteMime, SiteLink),
                File("good-fixture", "application/octet-stream", "https://drive.google.com/file/d/good-fixture/view")
            }
        }));
        script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId + "/permissions", deniedBody, HttpStatusCode.Forbidden);
        script.Add(HttpMethod.Get, "/drive/v3/files/good-fixture/permissions", Permissions(Link()));
        var page = await client.ListLinksAsync(ns);
        script.Done();
        var issue = page.Issues!.Single();
        Require(page.Items.Count == 1 && page.Items[0].ItemId == "good-fixture" && page.Next is not null && page.Incomplete,
            label + " keeps the successful record, the provider cursor and the incomplete flag");
        Require(issue.Code == expectedCode && issue.DetailCode == expectedDetail && issue.Stage == "permissions" && issue.ItemId == SiteId,
            label + " classifies as " + expectedCode + " with detail " + (expectedDetail ?? "none"));
        Require(!JsonSerializer.Serialize(issue).Contains("SENTINEL", StringComparison.Ordinal),
            label + " never exposes a raw provider string");
        Require(script.Mutations == 0 && script.Unexpected == 0,
            label + " issues no write verb and no unplanned provider request");
    }

    private static string Body(string errors, string? details = null) =>
        "{\"error\":{\"errors\":" + errors + (details is null ? "" : ",\"details\":" + details) + "}}";

    private static byte[] RawBody(string json) => Encoding.UTF8.GetBytes(json);

    // F4: effect uncertainty is settled before any error body is read, cancellation still propagates,
    // and a denied effect is never replayed.
    private static async Task EffectUncertaintyAsync()
    {
        foreach (var (status, uncertain, description) in new (HttpStatusCode, bool, string)[]
        {
            (HttpStatusCode.InternalServerError, true, "a server failure"),
            (HttpStatusCode.Forbidden, false, "a denied change")
        })
        {
            using var script = new Script(); using var http = new HttpClient(script);
            var client = Client(http); var ns = client.Namespaces[0];
            var native = File(SiteId, SiteMime, SiteLink);
            script.Add(HttpMethod.Get, "/drive/v3/files", JsonBody(new { files = new object[] { native } }));
            script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId + "/permissions", Permissions(Link()));
            var link = (await client.ListLinksAsync(ns)).Items.Single();
            script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId, JsonBody(native));
            script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId + "/permissions", Permissions(Link()));
            var expected = await client.GetLinkAsync(ns, link.Url);
            script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId, JsonBody(native));
            script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId + "/permissions", Permissions(Link()));
            script.Add(HttpMethod.Patch, "/drive/v3/files/" + SiteId + "/permissions/anyone-link",
                ReasonBody("insufficientFilePermissions", "SENTINEL-PROVIDER-MESSAGE"), status);
            if (uncertain)
            {
                await RejectAsync<GoogleDriveEffectUncertainException>(() => client.ChangeLinkAsync(expected, new SharedLinkChange { Role = "commenter" }));
            }
            else
            {
                var denied = await CaptureAsync<GoogleDriveApiException>(() => client.ChangeLinkAsync(expected, new SharedLinkChange { Role = "commenter" }));
                Require(denied.ProviderReason == "insufficientFilePermissions" && (int)denied.StatusCode! == 403,
                    "a denied change keeps its finite provider reason for the " + description);
            }
            Require(script.Mutations == 1, "an uncertain or denied " + description + " is sent at most once with no replay");
            script.Done();
        }
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var propagated = false;
            try { await Client(http).ListLinksAsync(NamespaceId, null, cancelled.Token); }
            catch (OperationCanceledException) { propagated = true; }
            Require(propagated && script.Methods.Count == 0, "cancellation propagates before any provider request and never becomes an issue");
        }
    }

    // F6: the shared summary the WPF sharing page renders states the owner-facing cause, offers one
    // instruction, and never advises reauthentication for a permission denial.
    private static void SharedSummary()
    {
        var summary = new AccountSharingIssueSummary();
        summary.AddPage("google-a", "Owner", [
            new AccountSharingIssue(SiteId, "not displayed", "access-restricted", "permissions", "insufficientFilePermissions"),
            new AccountSharingIssue(MapId, "not displayed", "access-restricted", "permissions", "insufficientFilePermissions")], true);
        summary.AddPage("google-a", "Owner", [
            new AccountSharingIssue("third-item", "not displayed", "access-restricted", "permissions", "insufficientFilePermissions")], false);
        Require(summary.Lines.Contains("Owner: Sharing details unavailable to this account (3 items)"),
            "the shared summary names the owner-facing cause with its cumulative item count");
        Require(summary.Lines.Contains(Instruction), "one instruction tells the user where the sharing details can be checked");
        Require(summary.Lines.Count(line => line == Instruction) == 1, "the instruction is shown once however many items and pages report it");
        Require(!summary.Lines.Any(line => line.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("reconnect", StringComparison.OrdinalIgnoreCase) || line.Contains("unauthor", StringComparison.OrdinalIgnoreCase)),
            "a permission denial never advises reauthentication");
        Require(summary.Lines.Contains("Available links are shown; some items could not be inspected."), "the incomplete notice is retained");
        summary.AddPage("google-a", "Owner", [new AccountSharingIssue("rate-item", "not displayed", "rate-limited", "permissions", "userRateLimitExceeded")], false);
        Require(summary.Lines.Contains("Owner: Rate limited (1 item)") &&
            summary.Lines.Contains("Google is limiting requests. Wait a little, then refresh."),
            "a rate-limited cause gets wait and refresh guidance");
        summary.Clear();
        Require(summary.Lines.Count == 0, "refreshing clears the instruction with the rest of the warning state");

        var unknown = new AccountSharingIssueSummary();
        unknown.AddPage("google-b", "Other", [new AccountSharingIssue("unclassified", "not displayed", "google-request-failed", "permissions")], false);
        Require(unknown.Lines.SequenceEqual(new[] { "Other: Google request failed (1 item)" }),
            "an unclassified 403 is reported as a failed Google request without claiming restricted access");

        var other = new AccountSharingIssueSummary();
        other.AddPage("google-c", "Owner", [new AccountSharingIssue("other-reason", "not displayed", "access-restricted", "permissions", "domainPolicy")], false);
        Require(other.Lines.SequenceEqual(new[] { "Owner: Access restricted (1 item)" }),
            "another allowlisted 403 reason keeps the existing restricted wording without the new instruction");

        var legacy = new AccountSharingIssueSummary();
        legacy.AddPage("dropbox", "Dropbox", [new AccountSharingIssue(null, "Legacy warning")], false);
        Require(legacy.Lines.SequenceEqual(new[] { "Dropbox: Legacy warning" }), "unclassified legacy warnings are unchanged");
    }

    private static void CliProjection()
    {
        var data = new SyncCliSharingListData("google-fixture", NamespaceId, [], 0, true, true,
            [new AccountSharingIssue(SiteId, "not displayed", "access-restricted", "permissions", "insufficientFilePermissions")]);
        var camel = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Require(camel.Contains("\"detailCode\":\"insufficientFilePermissions\"", StringComparison.Ordinal) &&
            camel.Contains("\"code\":\"access-restricted\"", StringComparison.Ordinal) &&
            camel.Contains("\"stage\":\"permissions\"", StringComparison.Ordinal),
            "the real CLI projection carries the finite reason at its camelCase boundary");
        Require(!camel.Contains("SENTINEL", StringComparison.Ordinal) && !camel.Contains("sites.google.com", StringComparison.Ordinal) &&
            !camel.Contains("opaque-site", StringComparison.Ordinal),
            "the CLI projection never carries provider text or the opaque editor address");
    }

    private static async Task<AccountSharingIssue> DeniedIssueAsync(byte[]? body, HttpStatusCode status = HttpStatusCode.Forbidden)
    {
        using var script = new Script(); using var http = new HttpClient(script);
        var client = Client(http); var ns = client.Namespaces[0];
        script.Add(HttpMethod.Get, "/drive/v3/files", JsonBody(new { files = new object[] { File(SiteId, SiteMime, SiteLink) } }));
        script.Add(HttpMethod.Get, "/drive/v3/files/" + SiteId + "/permissions", body, status);
        var page = await client.ListLinksAsync(ns);
        script.Done();
        Require(page.Items.Count == 0 && (page.Issues?.Count ?? 0) == 1 && script.Unexpected == 0,
            "a denied item yields exactly one issue, no link and no extra request");
        Require(script.Mutations == 0, "a classified read failure issues no write verb");
        return page.Issues!.Single();
    }

    private static byte[] ReasonBody(string reason, string message) => JsonBody(new { error = new { errors = new[] { new { reason } }, message } });

    private static byte[] OversizeReasonBody() => JsonBody(new
    { error = new { errors = new[] { new { reason = "insufficientFilePermissions" } } }, padding = new string('x', 70 * 1024) });

    private static byte[] JsonBody(object value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));

    private static Dictionary<string, object?> File(string id, string mime, string? web, string? key = null) => new()
    {
        ["id"] = id, ["name"] = "Synthetic " + id, ["mimeType"] = mime, ["version"] = "1",
        ["webViewLink"] = web, ["resourceKey"] = key, ["driveId"] = null,
        ["parents"] = Array.Empty<string>(), ["trashed"] = false,
        ["capabilities"] = new { canShare = true, canDownload = true }
    };

    private static Dictionary<string, object?> Link() => new()
    {
        ["id"] = "anyone-link", ["type"] = "anyone", ["role"] = "reader", ["allowFileDiscovery"] = false,
        ["permissionDetails"] = new[] { new { permissionType = "file", inherited = false, inheritedFrom = (string?)null, role = "reader" } }
    };

    private static Dictionary<string, object?> Member() => new()
    { ["id"] = "unrelated-member", ["type"] = "user", ["role"] = "owner" };

    private static byte[] Permissions(params Dictionary<string, object?>[] values) => JsonBody(new { permissions = values });

    private static GoogleDriveAccountSharingClient Client(HttpClient http) => new(http, new Tokens(),
        new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.ProductionClientId, "account-key-fixture", NamespaceId,
            "fixture@example.test", "Fixture Account", [GoogleDriveProviderConfiguration.DriveScope],
            CredentialReference: "credential-reference-fixture", ConnectionId: "google-fixture"));

    private static void Require(bool value, string description)
    { if (!value) throw new InvalidOperationException("Google sharing cause-fix checks: " + description); }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Google sharing cause-fix checks: expected " + typeof(T).Name);
    }

    private static async Task<T> CaptureAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T captured) { return captured; }
        throw new InvalidOperationException("Google sharing cause-fix checks: expected " + typeof(T).Name);
    }

    private sealed class Tokens : IGoogleDriveAccessTokenSource
    { public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("fixture-token-no-live-authority"); }

    private sealed record Step(HttpMethod Method, string Path, byte[] Body, HttpStatusCode Status);

    private sealed class Script : HttpMessageHandler
    {
        private readonly Queue<Step> _steps = new();
        internal int Mutations { get; private set; }
        internal int Unexpected { get; private set; }
        internal List<string> Methods { get; } = [];
        internal void Add(HttpMethod method, string path, byte[]? body, HttpStatusCode status = HttpStatusCode.OK)
            => _steps.Enqueue(new(method, path, body ?? [], status));
        internal void Done() => Require(_steps.Count == 0, "all recorded provider responses consumed exactly once");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Methods.Add(request.Method.Method);
            if (request.Method != HttpMethod.Get) Mutations++;
            Require(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "www.googleapis.com",
                "every adaptor request uses the fixed Google API origin");
            if (!_steps.TryDequeue(out var step)) { Unexpected++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }); }
            Require(request.Method == step.Method && request.RequestUri.AbsolutePath == step.Path,
                "unexpected provider route " + request.Method.Method + " " + request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(step.Status) { Content = new ByteArrayContent(step.Body) });
        }
    }
}
