using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// W6: proves the W1-W3 sharing-error classification through the REAL GoogleDriveAccountSharingClient
// with injected HTTP responses. No provider call, credential, live endpoint or write verb is involved;
// every request this stub sees is asserted to be a GET.
internal static class GoogleSharingClassificationChecks
{
    private const string NamespaceId = "fixture-permission-id";
    private const string FileA = "file-fixture-a";
    private const string FileB = "file-fixture-b";

    internal static async Task RunAsync()
    {
        var approvedProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Massdex", "SyncShark");
        GoogleDriveLiveChecks.ValidateSharingInventory(approvedProfile, GoogleDriveLiveChecks.ApprovedSharingConnectionId);
        foreach (var input in new[] { (approvedProfile + "-wrong", GoogleDriveLiveChecks.ApprovedSharingConnectionId), (approvedProfile, "wrong-account") })
        {
            var rejected = false;
            try { GoogleDriveLiveChecks.ValidateSharingInventory(input.Item1, input.Item2); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Live diagnostic rejects a foreign profile/account before state or network access");
        }
        var summary = new AccountSharingIssueSummary();
        var denied = new AccountSharingIssue(null, "not displayed", "access-restricted", "permissions");
        summary.AddPage("account-a", "Same label", [denied, denied], true);
        summary.AddPage("account-a", "Same label", [denied], false);
        summary.AddPage("account-b", "Same label", [denied], true);
        Check(summary.Lines.Contains("Same label: Access restricted (3 items)") &&
            summary.Lines.Contains("Same label: Access restricted (1 item)"),
            "The production UI accumulator sums pages and isolates account identities even with identical display labels");
        Check(summary.Lines.Count == 3, "The incomplete notice is retained once after a successful later page");
        summary.Clear();
        Check(summary.Lines.Count == 0, "Refreshing clears warning categories, counts and incomplete state");
        summary.AddPage("dropbox", "Dropbox fixture", [new(null, "Legacy warning")], false);
        summary.AddPage("future", "Future fixture", [new(null, "private payload", "unknown-code")], false);
        Check(summary.Lines.Contains("Dropbox fixture: Legacy warning") &&
            summary.Lines.Contains("Future fixture: Item could not be inspected (1 item)") &&
            !summary.Lines.Any(line => line.Contains("private payload", StringComparison.Ordinal) || line.Contains("Google", StringComparison.Ordinal)),
            "Legacy warnings retain their meaning and unknown classified providers do not leak text or become Google errors");

        foreach (var pair in new (HttpStatusCode Status, string Code)[]
        {
            (HttpStatusCode.Unauthorized, "sign-in-needed"),
            // F5: a 403 without a recognised finite reason is an unclassified Google request failure,
            // never a claim that this account was denied access.
            (HttpStatusCode.Forbidden, "google-request-failed"),
            (HttpStatusCode.NotFound, "no-longer-accessible"),
            ((HttpStatusCode)429, "rate-limited"),
            (HttpStatusCode.InternalServerError, "google-request-failed")
        })
        {
            using var transport = new Transport { PermissionsStatus = pair.Status };
            using var http = new HttpClient(transport);
            var page = await Client(http).ListLinksAsync(NamespaceId);
            var issue = page.Issues!.Single();
            Check(issue.Code == pair.Code && issue.Stage == "permissions" && issue.ItemId == FileA,
                "Google " + (int)pair.Status + " classifies as " + pair.Code + " at the permissions stage");
            Check(page.Items.Count == 0 && page.Incomplete && page.Next is not null,
                "A failed item keeps the provider cursor and marks the page incomplete");
            Check(transport.OnlyReads, "A classified failure issues no provider write verb");
        }

        using (var transport = new Transport { Malformed = true })
        using (var http = new HttpClient(transport))
        {
            var issue = (await Client(http).ListLinksAsync(NamespaceId)).Issues!.Single();
            Check(issue.Code == "invalid-response" && issue.DetailCode == "malformed-json",
                "A malformed provider body is classified as an invalid response");
        }

        using (var transport = new Transport { TransportFailure = true })
        using (var http = new HttpClient(transport))
        {
            var issue = (await Client(http).ListLinksAsync(NamespaceId)).Issues!.Single();
            Check(issue.Code == "connection-failed" && issue.Stage == "permissions",
                "A transport failure is classified as a connection failure");
        }

        using (var transport = new Transport { OmitName = true })
        using (var http = new HttpClient(transport))
        {
            var issue = (await Client(http).ListLinksAsync(NamespaceId)).Issues!.Single();
            Check(issue.Code == "invalid-response" && issue.DetailCode == "missing-metadata" && issue.Stage == "file-metadata",
                "Incomplete item metadata is classified at the file-metadata stage");
        }

        using (var transport = new Transport { OmitWebLink = true })
        using (var http = new HttpClient(transport))
        {
            var issue = (await Client(http).ListLinksAsync(NamespaceId)).Issues!.Single();
            Check(issue.Code == "invalid-response" && issue.DetailCode == "unsupported-web-link" && issue.Stage == "link-projection",
                "A missing web link is classified at the link-projection stage");
        }

        using (var transport = new Transport { SecondFileFails = true, PermissionsStatus = HttpStatusCode.Forbidden })
        using (var http = new HttpClient(transport))
        {
            var page = await Client(http).ListLinksAsync(NamespaceId);
            Check(page.Items.Count == 1 && page.Items[0].ItemId == FileA,
                "Mixed results keep the successful link when a later item fails");
            Check(page.Issues!.Count == 1 && page.Issues[0].ItemId == FileB && page.Issues[0].Code == "google-request-failed",
                "The failing item alone is reported with its classification");
            Check(page.Incomplete && page.Next is not null, "Mixed results keep the cursor and stay incomplete");
            var body = JsonSerializer.Serialize(page.Issues[0]);
            // The item id is the pre-existing, unchanged correlation field; the guarantee under test is
            // that no provider URL, name, key or token is ever copied into the issue.
            Check(!body.Contains("drive.google.com", StringComparison.Ordinal) && !body.Contains("fixture.txt", StringComparison.Ordinal) &&
                !body.Contains("anyoneWithLink", StringComparison.Ordinal) && body.Contains("google-request-failed", StringComparison.Ordinal),
                "The classified issue carries the code and never the provider URL, name or permission identity");
        }

        using (var transport = new Transport { SecondPage = true })
        using (var http = new HttpClient(transport))
        {
            var client = Client(http);
            var first = await client.ListLinksAsync(NamespaceId);
            Check(first.Items.Count == 1 && first.Items[0].ItemId == FileA && first.Next is not null,
                "The first corpus page returns its link and a cursor");
            var second = await client.ListLinksAsync(NamespaceId, first.Next);
            Check(second.Items.Count == 1 && second.Items[0].ItemId == FileB && second.Next is null,
                "The production cursor reads the second corpus page and then terminates");
        }

        using (var transport = new Transport())
        using (var http = new HttpClient(transport))
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var propagated = false;
            try { await Client(http).ListLinksAsync(NamespaceId, null, cancelled.Token); }
            catch (OperationCanceledException) { propagated = true; }
            Check(propagated && transport.Calls == 0, "Cancellation propagates and is never converted into an issue");
        }

        using (var transport = new Transport { ExposeForeignNamespace = true })
        using (var http = new HttpClient(transport))
        {
            var rejected = false;
            try { await Client(http).ListLinksAsync("foreign-namespace"); }
            catch (Exception) { rejected = true; }
            Check(rejected && transport.Calls == 0, "A namespace outside this account is rejected before any provider request");
        }

        var legacy = new AccountSharingIssue(FileA, "legacy");
        // D5: deterministic observer and sanitizer checks over synthetic response JSON.
        var allowed = GoogleDriveLiveChecks.ObserveReasonCodes(JsonSerializer.Serialize(new
        {
            error = new
            {
                errors = new[] { new { reason = "insufficientFilePermissions" } },
                details = new[] { new { reason = "rateLimitExceeded" } },
                message = "SENTINEL-SECRET-MESSAGE"
            }
        }));
        Check(allowed.Count == 2 && allowed.TryGetValue("insufficientFilePermissions", out var allowedCount) && allowedCount == 1 &&
            allowed.TryGetValue("rateLimitExceeded", out var limitedCount) && limitedCount == 1,
            "403 reasons map to the finite allowlist without provider text");
        var unknown = GoogleDriveLiveChecks.ObserveReasonCodes(JsonSerializer.Serialize(new { error = new { errors = new[] { new { reason = "brandNewProviderReason" } } } }));
        Check(unknown.Count == 1 && unknown.TryGetValue("other", out var unknownCount) && unknownCount == 1,
            "An unknown reason maps to other and never the provider string");
        Check(GoogleDriveLiveChecks.ObserveReasonCodes(JsonSerializer.Serialize(new { error = new { } })).ContainsKey("missing"),
            "A missing reason maps to missing");
        Check(GoogleDriveLiveChecks.ObserveReasonCodes("{not-json").ContainsKey("malformed"),
            "A malformed body maps to malformed");
        var supported = GoogleDriveLiveChecks.ObserveUrlShapes(JsonSerializer.Serialize(new
        {
            files = new[] { new { mimeType = "application/vnd.google-apps.document", webViewLink = "https://docs.google.com/document/d/fixture-id/edit" } }
        }));
        Check(supported.Count == 0, "A supported web link produces no URL-shape fact");
        var rejectedShapes = GoogleDriveLiveChecks.ObserveUrlShapes(JsonSerializer.Serialize(new
        {
            files = new[] { new { mimeType = "application/vnd.google-apps.spreadsheet", webViewLink = "https://example.invalid/SENTINEL-PATH?q=SENTINEL-QUERY#SENTINEL-FRAGMENT" } }
        }));
        Check(rejectedShapes.Count == 1 && rejectedShapes[0].ValidAbsolute && rejectedShapes[0].Https && rejectedShapes[0].HostCategory == "other" &&
            rejectedShapes[0].DefaultPort && !rejectedShapes[0].HasUserInfo && rejectedShapes[0].HasFragment && rejectedShapes[0].MimeCategory == "spreadsheet",
            "A rejected web link reports only finite shape facts");
        var shapeText = JsonSerializer.Serialize(rejectedShapes[0]);
        Check(!shapeText.Contains("example.invalid", StringComparison.Ordinal) && !shapeText.Contains("SENTINEL", StringComparison.Ordinal) &&
            !shapeText.Contains("fixture-id", StringComparison.Ordinal),
            "No host, path, query or fragment value can escape the URL-shape record");
        // A 403 whose JSON body carries no reason element is the missing case; the observer must still
        // forward those exact bytes to the production adapter.
        var deniedBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = new { } }));
        using (var forbidStub = new ObserverStubHandler(deniedBody, HttpStatusCode.Forbidden))
        {
            using var observer = new GoogleDriveLiveChecks.SharingInventoryReadOnlyHandler { InnerHandler = forbidStub };
            using var observerClient = new HttpClient(observer);
            using var deniedPage = await observerClient.GetAsync("https://www.googleapis.com/drive/v3/files/abc/permissions");
            var forwardedBytes = await deniedPage.Content.ReadAsByteArrayAsync();
            Check(forwardedBytes.Length == deniedBody.Length && forwardedBytes.SequenceEqual(deniedBody), "The observer forwards the provider bytes unchanged");
            Check(observer.ReasonCounts.ContainsKey("missing"), "A 403 without a reason element is counted as missing");
            var writeRefused = false;
            try { await observerClient.PostAsync("https://www.googleapis.com/drive/v3/files", null); }
            catch (Exception) { writeRefused = true; }
            Check(writeRefused, "The observer refuses a non-GET provider request");
        }
        using (var oversizeStub = new ObserverStubHandler(new byte[4 * 1024 * 1024 + 1], HttpStatusCode.Forbidden))
        {
            using var oversizeObserver = new GoogleDriveLiveChecks.SharingInventoryReadOnlyHandler { InnerHandler = oversizeStub };
            using var oversizeClient = new HttpClient(oversizeObserver);
            using var oversizeResponse = await oversizeClient.GetAsync("https://www.googleapis.com/drive/v3/files/abc/permissions");
            Check(oversizeObserver.OversizeRejected && oversizeObserver.ReasonCounts.Count == 0, "An oversized body is rejected without inspection");
        }

        // R3: a cursor bound to another connection is rejected before any provider request.
        using (var transport = new Transport())
        using (var http = new HttpClient(transport))
        {
            var client = Client(http);
            var first = await client.ListLinksAsync(NamespaceId);
            var calls = transport.Calls;
            var rejected = false;
            try { await Client(http).ListLinksAsync(NamespaceId, first.Next! with { ConnectionId = "wrong-account" }); }
            catch (Exception) { rejected = true; }
            Check(rejected && transport.Calls == calls,
                "A continuation bound to another connection is rejected before any provider request");
        }

        // R3: the real CLI projection record, serialised at the CLI's documented camelCase boundary.
        var cliData = new SyncCliSharingListData("google-fixture", NamespaceId, [], 0, true, true,
            [new AccountSharingIssue(FileB, "m", "access-restricted", "permissions", null)]);
        var cliJson = JsonSerializer.Serialize(cliData, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Check(cliJson.Contains("\"code\":\"access-restricted\"", StringComparison.Ordinal) &&
            cliJson.Contains("\"stage\":\"permissions\"", StringComparison.Ordinal) &&
            cliJson.Contains("\"itemId\":\"" + FileB + "\"", StringComparison.Ordinal) &&
            cliJson.Contains("\"incomplete\":true", StringComparison.Ordinal),
            "The real CLI sharing-list projection carries the classification at its camelCase boundary");

        Check(legacy.Code is null && legacy.Stage is null && legacy.DetailCode is null,
            "The legacy two-argument issue construction remains valid");
        var legacyBody = JsonSerializer.Serialize(legacy);
        Check(legacyBody.Contains("\"ItemId\"", StringComparison.Ordinal) && legacyBody.Contains("\"Message\"", StringComparison.Ordinal) &&
            !legacyBody.Contains("Code", StringComparison.Ordinal) && !legacyBody.Contains("Stage", StringComparison.Ordinal) &&
            !legacyBody.Contains("DetailCode", StringComparison.Ordinal),
            "An unclassified issue keeps its exact legacy JSON shape");
        var classifiedBody = JsonSerializer.Serialize(new AccountSharingIssue(FileA, "m", "access-restricted", "permissions", "missing-metadata"));
        Check(classifiedBody.Contains("\"Code\":\"access-restricted\"", StringComparison.Ordinal) &&
            classifiedBody.Contains("\"Stage\":\"permissions\"", StringComparison.Ordinal) &&
            classifiedBody.Contains("\"DetailCode\":\"missing-metadata\"", StringComparison.Ordinal),
            "The CLI issue projection carries the classification fields");

        Console.WriteLine("Google sharing classification checks passed.");
    }

    private static GoogleDriveAccountSharingClient Client(HttpClient http)
    {
        var binding = new GoogleDriveConnectionBinding(
            GoogleDriveProviderConfiguration.ProductionClientId, "account-key-fixture", NamespaceId,
            "fixture@example.test", "Fixture Account", [GoogleDriveProviderConfiguration.DriveScope],
            CredentialReference: "credential-reference-fixture", ConnectionId: "google-fixture");
        return new GoogleDriveAccountSharingClient(http, new Tokens(), binding);
    }

    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }

    private sealed class Tokens : IGoogleDriveAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("fixture-token-no-live-authority");
    }

    private sealed class ObserverStubHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly HttpStatusCode _status;
        public ObserverStubHandler(byte[] body, HttpStatusCode status) { _body = body; _status = status; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new ByteArrayContent(_body) });
    }

    private sealed class Transport : HttpMessageHandler
    {
        public HttpStatusCode PermissionsStatus = HttpStatusCode.OK;
        public bool Malformed, TransportFailure, OmitName, OmitWebLink, SecondFileFails, SecondPage, ExposeForeignNamespace;
        public int Calls;
        private readonly List<string> _methods = [];

        public bool OnlyReads => _methods.TrueForAll(method => string.Equals(method, "GET", StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            Check(message.Method == HttpMethod.Get, "The adapter fixture refuses every provider write verb");
            Calls++; _methods.Add(message.Method.Method);
            var path = message.RequestUri!.AbsolutePath;
            if (path.EndsWith("/files", StringComparison.Ordinal))
            {
                var query = message.RequestUri.Query;
                if (SecondPage && !query.Contains("pageToken", StringComparison.Ordinal))
                    return Task.FromResult(Json(new { nextPageToken = "fixture-page-two", files = new[] { File(FileA) } }));
                if (SecondFileFails)
                    return Task.FromResult(Json(new { nextPageToken = "fixture-page-more", files = new[] { File(FileA), File(FileB) } }));
                return SecondPage
                    ? Task.FromResult(Json(new { files = new[] { File(FileB) } }))
                    : Task.FromResult(Json(new { nextPageToken = "fixture-page-more", files = new[] { File(FileA) } }));
            }
            if (path.EndsWith("/permissions", StringComparison.Ordinal))
            {
                // The corpus fetch is deliberately outside the per-item catch, so the transport fault is
                // scoped to the permissions request that the classification path actually covers.
                if (TransportFailure) return Task.FromException<HttpResponseMessage>(new IOException("fixture transport failure"));
                var failing = !SecondFileFails || path.Contains(FileB, StringComparison.Ordinal);
                if (Malformed)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{not-json", Encoding.UTF8, "application/json") });
                if (failing && !PermissionsStatus.Equals(HttpStatusCode.OK))
                    return Task.FromResult(new HttpResponseMessage(PermissionsStatus));
                return Task.FromResult(Json(new { permissions = new[] { new { id = "anyoneWithLink", type = "anyone", role = "reader" } } }));
            }
            throw new InvalidOperationException("Unexpected provider route: " + path);
        }

        private Dictionary<string, object> File(string id)
        {
            var file = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = id, ["mimeType"] = "text/plain", ["version"] = "1",
                ["capabilities"] = new { canShare = true, canDownload = true }
            };
            if (!OmitName) file["name"] = "fixture.txt";
            if (!OmitWebLink) file["webViewLink"] = "https://drive.google.com/file/d/" + id + "/view";
            return file;
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
