using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// Explicit live acceptance only. Never reached by the ordinary test suite. One empty,
// already selected test folder and one tiny file; no deletion or unrelated Drive scan.
internal static class GoogleDriveLiveChecks
{
    // The exact-fixture sharing inspect is an opt-in test harness read only. These are the approved
    // acceptance identities, and the mode refuses anything else before it opens product state.
    internal static string ApprovedSharingConnectionId => Environment.GetEnvironmentVariable("SYNCSHARK_LIVE_GOOGLE_CONNECTION") ?? "gdrive-connection-fixture-2";
    internal static string ApprovedSharingFolderId => Environment.GetEnvironmentVariable("SYNCSHARK_LIVE_GOOGLE_FOLDER") ?? "folder-test-fixture";
    internal static string ApprovedSharingFileId => Environment.GetEnvironmentVariable("SYNCSHARK_LIVE_GOOGLE_FILE") ?? "file-test-fixture";

    // The expected live acceptance account is never committed. Each live mode skips visibly
    // (and stays on exit code 0) when the operator has not supplied it.
    internal const string ExpectedAccountVariable = "SYNCSHARK_LIVE_GOOGLE_ACCOUNT";

    private static bool TryGetExpectedAccount(out string account)
    {
        account = Environment.GetEnvironmentVariable(ExpectedAccountVariable)?.Trim() ?? string.Empty;
        if (account.Length != 0) return true;
        Console.WriteLine("SKIP live Google Drive checks: set " + ExpectedAccountVariable +
            " to the expected account email. No profile or provider access occurred.");
        return false;
    }

    internal static void ValidateSharingInventory(string productRoot, string connectionId)
    {
        var approved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Massdex", "SyncShark");
        if (!Path.GetFullPath(productRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(approved, StringComparison.OrdinalIgnoreCase) ||
            connectionId != ApprovedSharingConnectionId)
            throw new InvalidOperationException("The existing approved profile and Google account are required.");
    }

    // One metadata page only. Reuses the existing protected authentication/instance-lock route,
    // never returns private item identities or URLs, and refuses every provider write verb.
    internal static async Task ReadSharingInventoryAsync(string productRoot, string connectionId)
    {
        if (!TryGetExpectedAccount(out var expectedAccount)) return;
        ValidateSharingInventory(productRoot, connectionId);
        var stateRoot = Path.Combine(Path.GetFullPath(productRoot), "state");
        if (!Directory.Exists(stateRoot)) throw new InvalidOperationException("The existing profile is required.");
        using var authority = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "instance.lock"));
        var binding = new GoogleDriveConnectionStore(authority.StateAuthority).Load(connectionId)
            ?? throw new InvalidOperationException("The existing Google connection is required.");
        Require(binding.Email.Equals(expectedAccount, StringComparison.OrdinalIgnoreCase), "approved account identity");
        GoogleDriveProviderConfiguration.ValidateConnection(binding);
        using var endpoint = WindowsGoogleDriveRefreshTokenStore.OpenRegisteredClient();
        var tokens = new GoogleDriveAccessTokenSource(new WindowsGoogleDriveRefreshTokenStore(), endpoint, binding);
        using var reads = new SharingInventoryReadOnlyHandler();
        using var http = new HttpClient(reads);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var account = new GoogleDrivePilotClient(http, tokens, binding, ApprovedSharingFolderId,
            "/live-acceptance-sync-shark-2026-09-17");
        await account.VerifyAccountAsync(timeout.Token);
        var sharing = new GoogleDriveAccountSharingClient(http, tokens, binding);
        var page = await sharing.ListLinksAsync(binding.PermissionId, cancellationToken: timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result = "SHARING_PAGE_CLASSIFIED", providerMutation = false, pagesRead = 1,
            linkCount = page.Items.Count, issueCount = page.Issues?.Count ?? 0,
            incomplete = page.Incomplete, hasMore = page.Next is not null, readRequests = reads.ReadCount,
            oversizeRejected = reads.OversizeRejected,
            reasons = reads.ReasonCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { reason = pair.Key, count = pair.Value }).ToArray(),
            urlShapes = reads.UrlShapes.GroupBy(shape => shape)
                .Select(group => new
                {
                    group.Key.ValidAbsolute, group.Key.Https, group.Key.HostCategory, group.Key.DefaultPort,
                    group.Key.HasUserInfo, group.Key.HasFragment, group.Key.MimeCategory, count = group.Count()
                }).ToArray(),
            categories = (page.Issues ?? []).GroupBy(issue => new { issue.Code, issue.Stage, issue.DetailCode })
                .Select(group => new { group.Key.Code, group.Key.Stage, group.Key.DetailCode, count = group.Count() }).ToArray()
        }));
    }

    internal sealed class SharingInventoryReadOnlyHandler : DelegatingHandler
    {
        // D2/D3/D4: bounded, privacy-safe observation only. The inner response bytes and content headers
        // are forwarded unchanged, so the production adapter still sees exactly what the provider sent.
        private const int MaxInspectionBytes = 4 * 1024 * 1024;
        public int ReadCount { get; private set; }
        public bool OversizeRejected { get; private set; }
        public Dictionary<string, int> ReasonCounts { get; } = new(StringComparer.Ordinal);
        public List<ObserverUrlShape> UrlShapes { get; } = [];
        public SharingInventoryReadOnlyHandler() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri is not { Scheme: "https", Host: "www.googleapis.com", IsDefaultPort: true } uri ||
                !uri.AbsolutePath.StartsWith("/drive/v3/", StringComparison.Ordinal) || ++ReadCount > 400)
                throw new InvalidOperationException("The bounded Google sharing read-only diagnostic refused this request.");
            var response = await base.SendAsync(request, token);
            var denied = (int)response.StatusCode == 403;
            var listing = response.IsSuccessStatusCode && uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal);
            if (!denied && !listing) return response;
            var body = response.Content is null ? [] : await response.Content.ReadAsByteArrayAsync(token);
            if (body.Length > MaxInspectionBytes) OversizeRejected = true;
            else if (denied) Merge(ObserveReasonCodes(Encoding.UTF8.GetString(body)));
            else UrlShapes.AddRange(ObserveUrlShapes(Encoding.UTF8.GetString(body)));
            var forwarded = new HttpResponseMessage(response.StatusCode)
            { RequestMessage = request, ReasonPhrase = response.ReasonPhrase, Version = response.Version };
            forwarded.Content = new ByteArrayContent(body);
            if (response.Content is not null)
                foreach (var header in response.Content.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Dispose();
            return forwarded;
        }
        private void Merge(IReadOnlyDictionary<string, int> counts)
        {
            foreach (var pair in counts)
                ReasonCounts[pair.Key] = ReasonCounts.TryGetValue(pair.Key, out var prior) ? prior + pair.Value : pair.Value;
        }
    }

    internal sealed record ObserverUrlShape(bool ValidAbsolute, bool Https, string HostCategory, bool DefaultPort,
        bool HasUserInfo, bool HasFragment, string MimeCategory);

    private static readonly string[] ObserverReasonAllowlist =
    [
        "insufficientFilePermissions", "appNotAuthorizedToFile", "domainPolicy", "rateLimitExceeded",
        "userRateLimitExceeded", "dailyLimitExceeded", "sharingRateLimitExceeded", "forbidden",
        "insufficientPermissions", "ACCESS_TOKEN_SCOPE_INSUFFICIENT"
    ];

    private static readonly string[] ObserverHostCategories =
        ["drive.google.com", "docs.google.com", "sites.google.com", "script.google.com", "www.google.com", "maps.google.com", "forms.google.com"];

    private static readonly string[] ObserverMimeCategories =
        ["document", "spreadsheet", "presentation", "drawing", "form", "map", "site", "script", "folder", "shortcut"];

    // D3: only the finite reason allowlist, from error.errors[].reason and error.details[].reason.
    // Unknown or non-string becomes other, no reason element becomes missing, unparseable becomes
    // malformed. error.message is never read and no provider string is ever copied.
    internal static IReadOnlyDictionary<string, int> ObserveReasonCodes(string json)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Bump(string key) => counts[key] = counts.TryGetValue(key, out var prior) ? prior + 1 : 1;
        try
        {
            using var document = JsonDocument.Parse(json);
            var found = 0;
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "errors", "details" })
                {
                    if (!error.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) continue;
                    foreach (var entry in array.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("reason", out var reason)) continue;
                        found++;
                        var text = reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
                        Bump(text is not null && ObserverReasonAllowlist.Contains(text, StringComparer.Ordinal) ? text : "other");
                    }
                }
            }
            if (found == 0) Bump("missing");
        }
        catch (JsonException) { counts.Clear(); counts["malformed"] = 1; }
        return counts;
    }

    // D4: grouped finite URL-shape facts for entries whose web link the production gate rejects.
    internal static IReadOnlyList<ObserverUrlShape> ObserveUrlShapes(string json)
    {
        var shapes = new List<ObserverUrlShape>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return shapes;
            foreach (var entry in files.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var mime = entry.TryGetProperty("mimeType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
                var url = entry.TryGetProperty("webViewLink", out var web) && web.ValueKind == JsonValueKind.String ? web.GetString() : null;
                if (url is not null)
                {
                    try { GoogleDriveAccountSharingClient.ValidateUrl(url); continue; }
                    catch (Exception error) when (error is InvalidDataException or UriFormatException or ArgumentException) { }
                }
                shapes.Add(DescribeUrl(url, mime));
            }
        }
        catch (JsonException) { }
        return shapes;
    }

    internal static ObserverUrlShape DescribeUrl(string? url, string? mime)
    {
        var absolute = url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri is not null;
        var parsed = absolute ? new Uri(url!, UriKind.Absolute) : null;
        var host = parsed?.Host.ToLowerInvariant() ?? "";
        var hostCategory = ObserverHostCategories.Contains(host, StringComparer.Ordinal) ? host : "other";
        var mimeCategory = "other";
        const string prefix = "application/vnd.google-apps.";
        if (mime is not null && mime.StartsWith(prefix, StringComparison.Ordinal))
        {
            var suffix = mime[prefix.Length..];
            if (ObserverMimeCategories.Contains(suffix, StringComparer.Ordinal)) mimeCategory = suffix;
        }
        return new ObserverUrlShape(absolute, parsed?.Scheme == "https", hostCategory,
            parsed?.IsDefaultPort ?? false, parsed is not null && parsed.UserInfo.Length != 0,
            parsed is not null && parsed.Fragment.Length != 0, mimeCategory);
    }

    internal const string Release41Name = "release-0-3-41-check.txt";
    internal const string Release41RecoveryName = "release-0-3-41-check (recovered 20260922-225055).txt";
    private const string Release41Sha256 = "2448f7d36ea80a2e8408db5c135241147754b2262a9c64eddc646e1fb461ca3d";

    internal static void ValidateRelease41Readback(string productRoot, string connectionId, string folderId, string name)
    {
        var approved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Massdex", "SyncShark");
        if (!Path.GetFullPath(productRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(approved, StringComparison.OrdinalIgnoreCase) ||
            connectionId != ApprovedSharingConnectionId || folderId != ApprovedSharingFolderId ||
            name is not (Release41Name or Release41RecoveryName))
            throw new InvalidOperationException("The exact approved profile, account, folder and release fixture are required.");
    }

    internal static void Release41ReadbackGuardChecks()
    {
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Massdex", "SyncShark");
        ValidateRelease41Readback(profile, ApprovedSharingConnectionId, ApprovedSharingFolderId, Release41Name);
        ValidateRelease41Readback(profile, ApprovedSharingConnectionId, ApprovedSharingFolderId, Release41RecoveryName);
        foreach (var values in new[]
        {
            (profile + "-wrong", ApprovedSharingConnectionId, ApprovedSharingFolderId, Release41Name),
            (profile, "wrong-account", ApprovedSharingFolderId, Release41Name),
            (profile, ApprovedSharingConnectionId, "wrong-root", Release41Name),
            (profile, ApprovedSharingConnectionId, ApprovedSharingFolderId, "sync-check.txt")
        })
        {
            var refused = false;
            try { ValidateRelease41Readback(values.Item1, values.Item2, values.Item3, values.Item4); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused, "readback mismatches rejected before state or network access");
        }
        using var bounded = new MemoryStream(new byte[1024], 0, 1024, true, true);
        bounded.SetLength(0);
        var overflowRefused = false;
        try { bounded.Write(new byte[1025]); } catch (NotSupportedException) { overflowRefused = true; }
        Require(overflowRefused && bounded.Length == 0, "fixed readback buffer refuses oversized provider data");
        Console.WriteLine("PASS exact release41 readback guards and 1024-byte hard memory cap; no profile/provider access");
    }

    // Explicit, read-only acceptance of an already-created fixture. Not the old live-write route.
    internal static async Task ReadRelease41Async(string productRoot, string connectionId, string folderId, string name)
    {
        if (!TryGetExpectedAccount(out var expectedAccount)) return;
        ValidateRelease41Readback(productRoot, connectionId, folderId, name);
        var fixture = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestData", "Projects", "sync-shark", "cloud-pro-tests", "live-acceptance-b3e4f74-installed", Release41Name);
        var local = new byte[108];
        using (var input = File.OpenRead(fixture))
        {
            input.ReadExactly(local);
            Require(input.ReadByte() == -1, "local acceptance fixture is exactly 108 bytes");
        }
        Require(Convert.ToHexString(SHA256.HashData(local)).Equals(Release41Sha256, StringComparison.OrdinalIgnoreCase),
            "local acceptance fixture retains its approved content identity");
        using var authority = PilotInstanceLock.Acquire(Path.Combine(Path.GetFullPath(productRoot), "state", "instance.lock"));
        var binding = new GoogleDriveConnectionStore(authority.StateAuthority).Load(connectionId)
            ?? throw new InvalidOperationException("The exact connected account is required.");
        Require(binding.Email.Equals(expectedAccount, StringComparison.OrdinalIgnoreCase), "approved account identity");
        GoogleDriveProviderConfiguration.ValidateConnection(binding);
        using var endpoint = WindowsGoogleDriveRefreshTokenStore.OpenRegisteredClient();
        var tokens = new GoogleDriveAccessTokenSource(new WindowsGoogleDriveRefreshTokenStore(), endpoint, binding);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var client = new GoogleDrivePilotClient(http, tokens, binding, folderId, "/live-acceptance-sync-shark-2026-09-17");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await client.VerifyAccountAsync(timeout.Token);
        await client.VerifyRootIdentityAsync(timeout.Token);
        var entries = await client.ListEntriesAsync(timeout.Token);
        var matches = entries.Where(item => item.RelativePath == name).ToArray();
        if (matches.Length != 1 || matches[0].Size != 108)
            Console.WriteLine(JsonSerializer.Serialize(new { result = "FIXTURE_LOOKUP_MISMATCH", exactMatches = matches.Length,
                candidates = entries.Where(item => item.RelativePath.StartsWith("release-0-3-41-check", StringComparison.Ordinal))
                    .Take(8).Select(item => new { name = item.RelativePath, bytes = item.Size }) }));
        Require(matches.Length == 1 && matches[0].Size == 108 && !string.IsNullOrEmpty(matches[0].Id),
            "one exact 108-byte fixture exists; no unrelated or oversized file is downloaded");
        using var bytes = new MemoryStream(new byte[1024], 0, 1024, true, true);
        bytes.SetLength(0);
        await client.DownloadAsync(matches[0], bytes, timeout.Token);
        Require(bytes.Length == 108 && bytes.ToArray().SequenceEqual(local), "provider bytes match the original local fixture");
        Console.WriteLine(JsonSerializer.Serialize(new { result = "READBACK_MATCH", providerMutation = false,
            exactFixture = true, recoveryCopy = name == Release41RecoveryName, bytes = bytes.Length, sha256 = Release41Sha256 }));
    }

    internal static void ReportFailure(Exception error)
    {
        // Code locations only. Never print exception messages, HTTP requests, response bodies,
        // credential values or signed callback URLs from a live acceptance failure.
        var frames = new System.Diagnostics.StackTrace(error, true).GetFrames() ?? [];
        Console.Error.WriteLine(JsonSerializer.Serialize(new { result = "FAILED", type = error.GetType().Name,
            code = frames.Where(frame => frame.GetMethod()?.DeclaringType?.Assembly == typeof(GoogleDrivePilotClient).Assembly ||
                    frame.GetMethod()?.DeclaringType?.Assembly == typeof(GoogleDriveLiveChecks).Assembly)
                .Select(frame => new { method = frame.GetMethod()?.DeclaringType?.Name, line = frame.GetFileLineNumber() }) }));
    }

    internal static async Task RunAsync(string productRoot, string connectionId, string folderId, bool inspectOnly = false,
        bool resumeKnownFixture = false, bool metadataOnly = false, bool inspectSharing = false)
    {
        if (!TryGetExpectedAccount(out var expectedAccount)) return;
        // Opt-in test harness only: the sharing inspect requires the approved account and the approved test
        // folder BEFORE any product state is opened, so a mismatched request fails closed without loading or
        // touching the protected profile.
        if (inspectSharing &&
            (!string.Equals(connectionId, ApprovedSharingConnectionId, StringComparison.Ordinal) ||
             !string.Equals(folderId, ApprovedSharingFolderId, StringComparison.Ordinal)))
            throw new InvalidOperationException("The approved Google acceptance account and test folder are required for the sharing inspect.");
        var stateRoot = Path.Combine(Path.GetFullPath(productRoot), "state");
        // New connections use the dual-slot store and have no legacy JSONL file.
        // The protected store below, not a legacy filename, proves the account exists.
        if (!Directory.Exists(stateRoot))
            throw new InvalidOperationException("An existing connected test profile is required.");
        using var authority = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "instance.lock"));
        var binding = new GoogleDriveConnectionStore(authority.StateAuthority).Load(connectionId)
            ?? throw new InvalidOperationException("The exact connected Google account is required.");
        if (!binding.Email.Equals(expectedAccount, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This is not the approved Google acceptance account.");
        GoogleDriveProviderConfiguration.ValidateConnection(binding);
        using var endpoint = WindowsGoogleDriveRefreshTokenStore.OpenRegisteredClient();
        var tokens = new GoogleDriveAccessTokenSource(new WindowsGoogleDriveRefreshTokenStore(), endpoint, binding);
        using var ordinaryHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var raceHandler = new LiveRaceHandler();
        using var raceHttp = new HttpClient(raceHandler);
        var caps = GoogleDriveCapabilities.Baseline with { AtomicConditionalWrites = true };
        var ordinary = new GoogleDrivePilotClient(ordinaryHttp, tokens, binding, folderId,
            "/Apps/Sync Shark/Testing", capabilities: caps);
        var racing = new GoogleDrivePilotClient(raceHttp, tokens, binding, folderId,
            "/Apps/Sync Shark/Testing", capabilities: caps);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        await ordinary.VerifyAccountAsync(ct);
        Console.WriteLine("PASS live account identity");
        await ordinary.VerifyRootIdentityAsync(ct);
        Console.WriteLine("PASS live root identity");
        var folder = await ordinary.GetFileAsync(folderId, ct);
        if (string.IsNullOrEmpty(folder.Name) || !folder.Name.StartsWith("live-acceptance-", StringComparison.Ordinal) ||
            folder.Name.Length > 100)
            throw new InvalidOperationException("The exact live-acceptance test folder is required.");
        var entries = await ordinary.ListEntriesAsync(ct);
        if (inspectSharing)
        {
            // Exact-fixture, read-only precheck through the existing production sharing read. Only counts,
            // roles and a match boolean are printed: no URL, resource key, raw ACL, account or private data.
            if (entries.Count != 1 || entries[0].RelativePath != "sync-check.txt" || entries[0].Size > 1024 ||
                !string.Equals(entries[0].Id, ApprovedSharingFileId, StringComparison.Ordinal))
                throw new InvalidOperationException("The exact one-file live-acceptance fixture is required for the sharing inspect.");
            var sharing = new GoogleDriveAccountSharingClient(ordinaryHttp, tokens, binding);
            var sharingItem = await sharing.GetItemByIdAsync(binding.PermissionId, ApprovedSharingFileId, null, ct);
            var sharingLinks = await sharing.GetItemLinksAsync(sharingItem, ct);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "SHARING_INSPECTED",
                providerMutation = false,
                exactFixture = true,
                linkCount = sharingLinks.Count,
                publicLinkCount = sharingLinks.Count(link => string.Equals(link.Audience, "public", StringComparison.Ordinal)),
                // Boolean URL-shape evidence only: never disclose a resource key or arbitrary query value.
                providerUrlIsBare = sharingLinks.Count > 0 && sharingLinks.All(link =>
                    link.Url == "https://drive.google.com/file/d/" + ApprovedSharingFileId + "/view"),
                providerUrlIsDriveSdk = sharingLinks.Count > 0 && sharingLinks.All(link =>
                    link.Url == "https://drive.google.com/file/d/" + ApprovedSharingFileId + "/view?usp=drivesdk"),
                providerUrlIsSharing = sharingLinks.Count > 0 && sharingLinks.All(link =>
                    link.Url == "https://drive.google.com/file/d/" + ApprovedSharingFileId + "/view?usp=sharing"),
                roles = sharingLinks.Select(link => link.Access).Distinct(StringComparer.Ordinal)
                    .OrderBy(role => role, StringComparer.Ordinal).ToArray(),
                allRecordsMatchExactItem = sharingLinks.All(link =>
                    string.Equals(link.ItemId, ApprovedSharingFileId, StringComparison.Ordinal) &&
                    string.Equals(link.ConnectionId, binding.ConnectionId, StringComparison.Ordinal) &&
                    string.Equals(link.NamespaceId, binding.PermissionId, StringComparison.Ordinal))
            }));
            return;
        }
        if (metadataOnly)
        {
            await MetadataAsync(ordinary, racing, raceHandler, folderId, entries, ct);
            return;
        }
        if (inspectOnly)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { result = "INSPECTED", folderId,
                count = entries.Count, testFilePresent = entries.Any(entry => entry.RelativePath == "sync-check.txt"),
                providerMutation = false }));
            if (entries.Count == 1 && entries[0].RelativePath == "sync-check.txt" && entries[0].Size <= 1024)
            {
                using var observedBytes = new MemoryStream();
                await ordinary.DownloadAsync(entries[0], observedBytes, ct);
                var content = observedBytes.ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new { result = "READBACK", providerMutation = false,
                    firstPayload = content.SequenceEqual(Encoding.UTF8.GetBytes("Sync Shark live create and download check.\n")),
                    secondPayload = content.SequenceEqual(Encoding.UTF8.GetBytes("Sync Shark live current-version update check.\n")),
                    concurrentPayload = content.SequenceEqual(Encoding.UTF8.GetBytes("Concurrent live edit must survive the stale request.\n")) }));
                var entry = await ordinary.GetFileAsync(entries[0].Id ??
                    throw new InvalidOperationException("Test entry has no stable identity."), ct);
                // Exercise the existing read-only guard acquisition, without issuing any PUT/PATCH.
                var method = typeof(GoogleDrivePilotClient).GetMethod("ReadConditionalGuardAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                await (Task)method.Invoke(ordinary, [entry, ct])!;
                Console.WriteLine("PASS current live conditional guard acquisition; no mutation");
            }
            return;
        }
        if (entries.Count != 0 && !resumeKnownFixture)
            throw new InvalidOperationException("The exact empty live-acceptance test folder is required. No repeat upload attempted.");
        const string path = "sync-check.txt";
        static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
        static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
        var firstBytes = Bytes("Sync Shark live create and download check.\n");
        var secondBytes = Bytes("Sync Shark live current-version update check.\n");
        var resumedSecondPayload = false;
        string createdFileId;
        if (resumeKnownFixture)
        {
            Require(folderId == ApprovedSharingFolderId && entries.Count == 1 &&
                entries[0].RelativePath == path && entries[0].Size <= 1024,
                "exact retained owner fixture only");
            using var priorContent = new MemoryStream();
            await ordinary.DownloadAsync(entries[0], priorContent, ct);
            resumedSecondPayload = priorContent.ToArray().SequenceEqual(secondBytes);
            Require(priorContent.ToArray().SequenceEqual(firstBytes) || resumedSecondPayload,
                "retained first or acknowledged second payload, not unknown or concurrent content");
            createdFileId = entries[0].Id ?? throw new InvalidOperationException("Fixture has no stable identity.");
            Console.WriteLine("PASS retained owner fixture and exact known payload; create not replayed");
        }
        else
        {
            Console.WriteLine("PASS empty fixture preflight");
            var created = await ordinary.UploadCreateOnlyAsync(path, new MemoryStream(firstBytes), Hash(firstBytes), ct);
            createdFileId = created.FileId;
            Console.WriteLine("PASS live create acknowledgement");
        }
        var current = await ordinary.GetFileAsync(createdFileId, ct);
        using (var download = new MemoryStream())
        {
            await ordinary.DownloadAsync(current.ToRemoteEntry(), download, ct);
            Require(download.ToArray().SequenceEqual(resumedSecondPayload ? secondBytes : firstBytes), "known fixture download bytes");
        }
        Console.WriteLine("PASS live create/download bytes");
        var updated = await ordinary.UploadAsync(path, new MemoryStream(secondBytes), Hash(secondBytes), current.Revision, ct);
        Require(updated.FileId == createdFileId && updated.Revision != current.Revision, "current guarded update identity/version");
        Console.WriteLine("PASS current conditional update acknowledgement");
        current = await ReadSettledFixtureAsync(ordinary, createdFileId, secondBytes, ct);
        var concurrentBytes = Bytes("Concurrent live edit must survive the stale request.\n");
        Console.WriteLine("PASS current guarded update readback");
        raceHandler.TargetFileId = createdFileId;
        raceHandler.BeforeConditionalPut = async () =>
        {
            await ordinary.UploadAsync(path, new MemoryStream(concurrentBytes), Hash(concurrentBytes), current.Revision, ct);
        };
        var attemptedBytes = Bytes("This stale write must not replace the concurrent edit.\n");
        var refused = false;
        try { await racing.UploadAsync(path, new MemoryStream(attemptedBytes), Hash(attemptedBytes), current.Revision, ct); }
        catch (GoogleDriveBlockedException) { refused = true; }
        Require(refused && raceHandler.ConditionalPutCount == 1 && raceHandler.PreconditionFailures == 1,
            "real Google HTTP 412 and no retry");
        current = await ReadSettledFixtureAsync(ordinary, createdFileId, concurrentBytes, ct);
        Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", folderId,
            fileId = createdFileId, createDownload = true, currentGuardedUpdate = true,
            staleStatus = 412, staleRequests = 1, concurrentBytesPreserved = true,
            remoteFixtureRetained = true, productionCapabilityChanged = false }));
    }

    private static void Require(bool condition, string proof)
    { if (!condition) throw new InvalidOperationException("Live Google acceptance failed: " + proof); }

    private static async Task MetadataAsync(GoogleDrivePilotClient ordinary, GoogleDrivePilotClient racing,
        LiveRaceHandler handler, string folderId, IReadOnlyList<PilotRemoteEntry> entries, CancellationToken ct)
    {
        var id = ApprovedSharingFileId;
        const string original = "sync-check.txt";
        Require(folderId == ApprovedSharingFolderId && entries.Count == 1 &&
            entries[0].Id == id && entries[0].RelativePath == original,
            "exact retained fixture before one metadata acceptance, no automatic replay");
        var expectedBytes = Encoding.UTF8.GetBytes("Concurrent live edit must survive the stale request.\n");
        using (var content = new MemoryStream())
        {
            await ordinary.DownloadAsync(entries[0], content, ct);
            Require(content.ToArray().SequenceEqual(expectedBytes), "retained concurrent bytes before rename");
        }
        var current = await ordinary.GetFileAsync(id, ct);
        var renamed = await ordinary.MoveAsync((current with { RelativePath = original }).ToRemoteEntry(),
            "sync-check-renamed.txt", ct);
        Require(renamed.Id == id && renamed.RelativePath == "sync-check-renamed.txt", "live current rename");
        current = await ordinary.GetFileAsync(id, ct);
        await ordinary.MoveAsync((current with { RelativePath = "sync-check-renamed.txt" }).ToRemoteEntry(), original, ct);
        current = await ordinary.GetFileAsync(id, ct);
        var reviewed = (current with { RelativePath = original }).ToRemoteEntry();
        handler.TargetFileId = id;
        handler.BeforeConditionalPatch = async () =>
        {
            await ordinary.MoveAsync(reviewed, "sync-check-concurrent.txt", ct);
        };
        var refused = false;
        try { await racing.DeleteAsync(reviewed, ct); }
        catch (GoogleDriveBlockedException) { refused = true; }
        Require(refused && handler.ConditionalPatchCount == 1 && handler.PreconditionFailures == 1,
            "real stale PATCH refused with one HTTP412, no retry");
        current = await ordinary.GetFileAsync(id, ct);
        Require(!current.Trashed && current.Name == "sync-check-concurrent.txt", "concurrent rename survives stale trash");
        await ordinary.MoveAsync((current with { RelativePath = "sync-check-concurrent.txt" }).ToRemoteEntry(), original, ct);
        current = await ordinary.GetFileAsync(id, ct);
        using (var content = new MemoryStream())
        {
            await ordinary.DownloadAsync((current with { RelativePath = original }).ToRemoteEntry(), content, ct);
            Require(content.ToArray().SequenceEqual(expectedBytes), "rename rollback preserves exact bytes");
        }
        var trashBytes = Encoding.UTF8.GetBytes("Sync Shark recoverable-trash check.\n");
        var created = await ordinary.UploadCreateOnlyAsync("trash-check.txt", new MemoryStream(trashBytes),
            Convert.ToHexString(SHA256.HashData(trashBytes)).ToLowerInvariant(), ct);
        var trashCandidate = await ordinary.GetFileAsync(created.FileId, ct);
        await ordinary.DeleteAsync((trashCandidate with { RelativePath = "trash-check.txt" }).ToRemoteEntry(), ct);
        var trashed = await ordinary.GetFileAsync(created.FileId, ct);
        Require(trashed.Trashed && trashed.FileId == created.FileId, "exact newly created probe recoverably trashed");
        Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", folderId, fileId = id,
            currentRename = true, stalePatchStatus = 412, stalePatchRequests = handler.ConditionalPatchCount,
            concurrentNamePreserved = true, originalNameRestored = true, exactBytesPreserved = true,
            currentRecoverableTrash = true, trashProbeId = created.FileId, permanentDeletion = false }));
    }

    private static async Task<GoogleDriveInventoryEntry> ReadSettledFixtureAsync(GoogleDrivePilotClient client,
        string fileId, byte[] expected, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = await client.GetFileAsync(fileId, ct);
            using var content = new MemoryStream();
            try
            {
                await client.DownloadAsync(before.ToRemoteEntry(), content, ct);
                Require(content.ToArray().SequenceEqual(expected), "exact fixture bytes");
                return await client.GetFileAsync(fileId, ct);
            }
            catch (GoogleDriveBlockedException) when (attempt < 2)
            {
                var after = await client.GetFileAsync(fileId, ct);
                Console.WriteLine(JsonSerializer.Serialize(new { result = "READ_RESCAN", attempt,
                    versionChanged = before.Version != after.Version,
                    headRevisionSame = before.HeadRevisionId == after.HeadRevisionId,
                    checksumSame = before.Sha256 is not null && before.Sha256 == after.Sha256,
                    nameSame = before.Name == after.Name, sizeSame = before.Size == after.Size,
                    parentsSame = before.Parents.ToHashSet().SetEquals(after.Parents), trashed = after.Trashed }));
                // Read-only rescan of this known fixture; never retry a provider mutation here.
                Require(after.FileId == fileId && !after.Trashed && after.Size == expected.Length &&
                    after.Sha256 == Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
                    "unchanged exact fixture content before bounded read rescan");
            }
        }
        throw new InvalidOperationException("Fixture readback did not settle in the bounded read-only rescan.");
    }

    // Supplies a real competing write between guard acquisition and dispatch. Both
    // writes go to Google; no response/status or product decision is mocked.
    private sealed class LiveRaceHandler : DelegatingHandler
    {
        internal LiveRaceHandler() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        internal string? TargetFileId { get; set; }
        internal Func<Task>? BeforeConditionalPut { get; set; }
        internal Func<Task>? BeforeConditionalPatch { get; set; }
        internal int ConditionalPutCount { get; private set; }
        internal int ConditionalPatchCount { get; private set; }
        internal int PreconditionFailures { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var target = request.Method == HttpMethod.Put && request.Headers.IfMatch.Count != 0 &&
                request.RequestUri?.Host == "www.googleapis.com" &&
                request.RequestUri.AbsolutePath == "/upload/drive/v2/files/" + Uri.EscapeDataString(TargetFileId ?? "");
            if (target)
            {
                ConditionalPutCount++;
                if (ConditionalPutCount != 1) throw new InvalidOperationException("Unexpected conditional retry.");
                var action = BeforeConditionalPut ?? throw new InvalidOperationException("Missing bounded race action.");
                BeforeConditionalPut = null;
                await action();
            }
            var patchTarget = request.Method == HttpMethod.Patch && request.Headers.IfMatch.Count != 0 &&
                request.RequestUri?.Host == "www.googleapis.com" &&
                request.RequestUri.AbsolutePath == "/drive/v2/files/" + Uri.EscapeDataString(TargetFileId ?? "");
            if (patchTarget)
            {
                ConditionalPatchCount++;
                if (ConditionalPatchCount != 1) throw new InvalidOperationException("Unexpected conditional PATCH retry.");
                var action = BeforeConditionalPatch ?? throw new InvalidOperationException("Missing bounded PATCH race.");
                BeforeConditionalPatch = null;
                await action();
            }
            var response = await base.SendAsync(request, ct);
            if ((target || patchTarget) && response.StatusCode == HttpStatusCode.PreconditionFailed) PreconditionFailures++;
            return response;
        }
    }
}
