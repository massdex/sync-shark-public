using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// R1-R3/R5 focused proofs for the ONE provider-neutral exact-path lookup
// primitive: ISyncRemoteStore.FindEntryAtPathAsync and its Dropbox override. The wire
// supplies only synthetic Dropbox responses and refuses every endpoint an exact lookup
// must never use, and the legacy fallback is driven through a scoped listing adapter, so
// the classification and refusal decisions under proof are the shipped production code.
internal static class ExactItemDispatchChecks
{
    private static int _passed;

    internal static async Task RunAsync()
    {
        _passed = 0;
        await PresentItemAndFreshnessAsync();
        await ConfirmedAbsenceAsync();
        await RefusalsNeverAbsenceAsync();
        await SharedFallbackAndAmbiguityAsync();
        Console.WriteLine("PASS exact item dispatch (SSHARK595): " + _passed + " assertions");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Exact item dispatch check failed: " + message);
        _passed++;
        Console.WriteLine("PASS " + message);
    }

    private static async Task<(bool returnedAbsence, Exception? error)> ObserveAsync(Wire wire, string path)
    {
        using var http = new HttpClient(wire);
        try
        {
            var found = await Client(http).FindEntryAtPathAsync(path);
            return (found is null, null);
        }
        catch (Exception error)
        {
            return (false, error);
        }
    }

    private static async Task PresentItemAndFreshnessAsync()
    {
        // A present item is answered from one exact bound-root metadata read and the tree is
        // never enumerated, and a second call observes the provider again rather than a cache.
        var wire = new Wire { Display = "/Pilot/sub/file.txt" };
        using var http = new HttpClient(wire);
        ISyncRemoteStore remote = Client(http);
        var first = await remote.FindEntryAtPathAsync("sub/file.txt");
        Require(first is not null && first.Id == "id:file" && first.RelativePath == "sub/file.txt" &&
                first.Kind == PilotEntryKind.File && first.Revision == "rev-1" && first.Size == 11 &&
                first.ContentHash == new string('b', 64) && wire.RequestedPath == "id:pilot-folder/sub/file.txt",
            "a present item is one exact bound-root metadata read with the expected identity");
        Require(wire.ListCalls == 0 && wire.MetadataReads == 1,
            "a present exact-path lookup never enumerates the remote tree");
        wire.Revision = "rev-2";
        var second = await remote.FindEntryAtPathAsync("sub/file.txt");
        Require(second is not null && second.Revision == "rev-2" && wire.MetadataReads == 2 && wire.ListCalls == 0,
            "a repeated lookup returns fresh provider metadata, never the reviewed inventory");

        // A folder at the same exact path is observed as a directory, which is what the
        // create-folder and move-destination absence checks depend on.
        var folderWire = new Wire { Tag = "folder", Display = "/Pilot/Folder" };
        using var folderHttp = new HttpClient(folderWire);
        var folder = await Client(folderHttp).FindEntryAtPathAsync("Folder");
        Require(folder is not null && folder.Kind == PilotEntryKind.Directory && folder.Id == "id:folder" &&
                folder.RelativePath == "Folder" && folderWire.ListCalls == 0,
            "a folder at the exact path is observed as a directory identity without any listing");
    }

    private static async Task ConfirmedAbsenceAsync()
    {
        // Only Dropbox's own exact path-lookup classification proves absence, and it is one
        // metadata read with no tree enumeration.
        foreach (var (label, body) in new[]
                 {
                     ("structured", "{\"error_summary\":\"path/not_found/\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}"),
                     ("summary", "{\"error_summary\":\"path/not_found/\"}")
                 })
        {
            var wire = new Wire { FailStatus = HttpStatusCode.Conflict, FailBody = body };
            var (absence, error) = await ObserveAsync(wire, "sub/absent.txt");
            Require(absence && error is null && wire.ListCalls == 0 && wire.MetadataReads == 1,
                "the " + label + " path/not_found classification is the only proof of absence");
        }
    }

    private static async Task RefusalsNeverAbsenceAsync()
    {
        // Nothing other than the explicit path/not_found classification may be read as
        // absence: each refusal below must raise instead of returning a null entry.
        var refusalCases = new (string Label, HttpStatusCode Status, string Body)[]
        {
            ("another endpoint family's not_found", HttpStatusCode.Conflict, "{\"error_summary\":\"not_found\"}"),
            ("a path conflict", HttpStatusCode.Conflict, "{\"error_summary\":\"path/conflict/\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"conflict\"}}}"),
            ("a contradictory summary", HttpStatusCode.Conflict, "{\"error_summary\":\"path/not_found/\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"conflict\"}}}"),
            ("an unparsable refusal body", HttpStatusCode.Conflict, "{not-json"),
            ("a rejected access token", HttpStatusCode.Unauthorized, "{\"error_summary\":\"invalid_access_token/value\"}"),
            ("a missing permission", HttpStatusCode.Forbidden, "{\"error_summary\":\"missing_scope/..\"}"),
            ("an absent route", HttpStatusCode.NotFound, "{\"error_summary\":\"path/not_found/\"}")
        };
        foreach (var (label, status, body) in refusalCases)
        {
            var wire = new Wire { FailStatus = status, FailBody = body };
            var (absence, error) = await ObserveAsync(wire, "sub/file.txt");
            Require(!absence && error is DropboxApiException && wire.ListCalls == 0,
                "a " + label + " refusal is an error and never absence");
        }

        // Transport unavailability is retried only as an idempotent read and then raises.
        var unavailable = new Wire { FailStatus = HttpStatusCode.ServiceUnavailable };
        var (transportAbsence, transportError) = await ObserveAsync(unavailable, "sub/file.txt");
        Require(!transportAbsence && transportError is DropboxApiException { StatusCode: HttpStatusCode.ServiceUnavailable } &&
                unavailable.MetadataReads == 3 && unavailable.ListCalls == 0,
            "a persistent transport failure ends after three bounded metadata reads and is never absence");

        // Substitution, a foreign root and a malformed acceptance body are refused too.
        foreach (var (label, wire) in new (string, Wire)[]
                 {
                     ("a substituted in-root path", new Wire { Display = "/Pilot/sub/other.txt" }),
                     ("a metadata path outside the bound root", new Wire { Display = "/Elsewhere/file.txt" }),
                     ("a foreign object identity", new Wire { Id = "sharedid:other" }),
                     ("an absent object identity", new Wire { Id = "" })
                 })
        {
            var (absence, error) = await ObserveAsync(wire, "sub/file.txt");
            Require(!absence && error is IOException or InvalidDataException && wire.ListCalls == 0,
                label + " is refused as an error and never absence");
        }

        var malformed = new Wire { MalformedBody = true };
        var (malformedAbsence, malformedError) = await ObserveAsync(malformed, "sub/file.txt");
        Require(!malformedAbsence && malformedError is not null && malformed.ListCalls == 0,
            "an accepted but malformed metadata body is an error and never absence");
    }

    private static async Task SharedFallbackAndAmbiguityAsync()
    {
        // An adapter without a native metadata endpoint keeps the honest shared fallback:
        // the fresh listing is used, one exact name is returned and a replaced identity at
        // the same path is refused.
        var fallback = new FakeSyncRemoteStore();
        fallback.Put("file.txt", "payload");
        var listings = 0;
        fallback.BeforeListEntriesForTest = _ => { listings++; return Task.CompletedTask; };
        ISyncRemoteStore legacy = fallback;
        var present = await legacy.FindEntryAtPathAsync("file.txt");
        Require(present is not null && present.Id == "id:file.txt" && listings == 1,
            "the shared fallback returns the present item from one fresh listing");
        var absent = await legacy.FindEntryAtPathAsync("absent.txt");
        Require(absent is null && listings == 2,
            "the shared fallback proves absence only from a fresh unambiguous listing");
        Require((await legacy.ReadEntryAsync("file.txt", "id:file.txt")).RelativePath == "file.txt",
            "the shared fallback keeps its identity-conditioned exact item read");
        var replaced = false;
        try { await legacy.ReadEntryAsync("file.txt", "id:other"); }
        catch (IOException) { replaced = true; }
        Require(replaced, "the shared fallback refuses a replaced item at the same path");

        // Two legacy names that collapse onto one observed path are ambiguous: they are
        // refused rather than reported as present or as proven absent.
        ISyncRemoteStore ambiguous = new LegacyListStore(
            new PilotRemoteEntry("file.txt", PilotEntryKind.File, "id:lower", "rev:1", 7, null),
            new PilotRemoteEntry("FILE.txt", PilotEntryKind.File, "id:upper", "rev:1", 7, null));
        var ambiguityRefused = false;
        try { await ambiguous.FindEntryAtPathAsync("file.txt"); }
        catch (IOException) { ambiguityRefused = true; }
        Require(ambiguityRefused, "duplicate legacy names at the same exact path are refused, never read as absence");
        ISyncRemoteStore identicalNames = new LegacyListStore(
            new PilotRemoteEntry("file.txt", PilotEntryKind.File, "id:first", "rev:1", 7, null),
            new PilotRemoteEntry("file.txt", PilotEntryKind.File, "id:second", "rev:1", 7, null));
        var duplicateRefused = false;
        try { await identicalNames.FindEntryAtPathAsync("file.txt"); }
        catch (IOException) { duplicateRefused = true; }
        Require(duplicateRefused, "identical path spellings with competing identities are also ambiguous");
        Require(await ambiguous.FindEntryAtPathAsync("absent.txt") is null,
            "an unambiguous legacy miss is still proven absence");
        ISyncRemoteStore single = new LegacyListStore(
            new PilotRemoteEntry("only.txt", PilotEntryKind.File, "id:only", "rev:1", 7, null));
        Require((await single.FindEntryAtPathAsync("only.txt"))?.Id == "id:only",
            "one unambiguous legacy listing name is returned as the exact item");
    }

    private static DropboxPilotClient Client(HttpClient http) => new(http, new Token(), "/Pilot",
        new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id",
            "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes), "id:pilot-folder");

    private sealed class Token : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("synthetic-exact-item-token");
    }

    // The scoped listing adapter implements only the observation surface the shared
    // fallback needs; every effect member refuses, so this fixture cannot stand in for a
    // production decision about creating, moving or deleting anything.
    private sealed class LegacyListStore(params PilotRemoteEntry[] entries) : ISyncRemoteStore
    {
        public Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PilotRemoteEntry>>(entries);

        public Task VerifyControlNamespaceAsync(SyncRemoteControlNamespaceBinding binding, string installationId,
            string namespaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The scoped legacy listing adapter has no control namespace.");

        public Task<PilotUploadResult> UploadCreateOnlyAsync(string relativePath, Stream source,
            string expectedContentHash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The scoped legacy listing adapter performs no effects.");

        public Task<PilotUploadResult> UploadAsync(string relativePath, Stream source,
            string expectedContentHash, string? expectedRemoteRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The scoped legacy listing adapter performs no effects.");

        public Task<SyncDownloadResult> DownloadAsync(PilotRemoteEntry expected, Stream destination,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The scoped legacy listing adapter performs no effects.");

        public Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected, string recoveryPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The scoped legacy listing adapter performs no effects.");

        public Task<SyncArchiveResult?> GetRecoveryAsync(string recoveryPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The scoped legacy listing adapter has no recovery root.");

        public Task DeleteAsync(PilotRemoteEntry expected, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The scoped legacy listing adapter performs no effects.");
    }

    private sealed class Wire : HttpMessageHandler
    {
        internal int MetadataReads, ListCalls;
        internal string Revision = "rev-1";
        internal string Tag = "file";
        internal string? Display;
        internal string? Id;
        internal HttpStatusCode FailStatus = HttpStatusCode.OK;
        internal string FailBody = "";
        internal bool MalformedBody;
        internal string? RequestedPath;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
                throw new InvalidOperationException("An exact lookup must use POST only.");
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/2/users/get_current_account")
                return Json("{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}");
            if (route.Contains("list_folder", StringComparison.Ordinal))
            {
                ListCalls++;
                throw new InvalidOperationException("An exact lookup must never enumerate the tree: " + route);
            }
            if (route != "/2/files/get_metadata")
                throw new InvalidOperationException("Unexpected exact-lookup provider route: " + route);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var requested = body.RootElement.GetProperty("path").GetString();
            if (requested == "id:pilot-folder")
                return Json("{\".tag\":\"folder\",\"name\":\"Pilot\",\"id\":\"id:pilot-folder\",\"path_lower\":\"/pilot\",\"path_display\":\"/Pilot\"}");
            RequestedPath = requested;
            MetadataReads++;
            if (FailStatus != HttpStatusCode.OK || MalformedBody)
            {
                var failed = new HttpResponseMessage(MalformedBody ? HttpStatusCode.OK : FailStatus);
                if (!MalformedBody) failed.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                failed.Content = new StringContent(MalformedBody ? "{not-json" : FailBody, Encoding.UTF8,
                    "application/json");
                return failed;
            }
            var display = Display ?? "/Pilot/sub/file.txt";
            return Tag == "folder"
                ? Json(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [".tag"] = "folder", ["name"] = "Folder", ["id"] = Id ?? "id:folder",
                    ["path_display"] = display, ["path_lower"] = display.ToLowerInvariant()
                }))
                : Json(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [".tag"] = "file", ["name"] = "file.txt", ["id"] = Id ?? "id:file",
                    ["path_display"] = display, ["path_lower"] = display.ToLowerInvariant(),
                    ["rev"] = Revision, ["size"] = 11, ["content_hash"] = new string('b', 64)
                }));
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }
}
